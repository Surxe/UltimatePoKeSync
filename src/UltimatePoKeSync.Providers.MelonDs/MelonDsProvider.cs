using System.Runtime.CompilerServices;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Providers.MelonDs;

/// <summary>
/// Streams party snapshots out of melonDS, for Gen 4 and 5. See D-039, D-040 and D-067.
/// </summary>
/// <remarks>
/// <para>
/// This polls where the mGBA provider is pushed to. There is no script on the other side to
/// notice a change and send it: melonDS answers questions, so the questions have to be
/// asked. Each poll is one 4-byte read to follow the pointer, one for the party header and
/// one per slot (D-072), which is why the default interval is a second rather than a
/// frame.
/// </para>
/// <para>
/// A snapshot is emitted only when the bytes change, so a party sitting still costs the
/// consumer nothing.
/// </para>
/// </remarks>
public sealed class MelonDsProvider : IEmulatorProvider, IEmulatorMemoryReader, IBattleReader
{
    private readonly MelonDsMemoryReader _reader;
    private readonly TimeSpan _interval;

    private EmulatorConnectionState _state = EmulatorConnectionState.Idle;
    private ulong _sequence;

    /// <summary>The cartridge the party loop last identified, so a battle read need not ask again.</summary>
    private volatile GameIdentity? _game;

    public MelonDsProvider(
        string host = "127.0.0.1",
        int port = MelonDsMemoryReader.DefaultPort,
        TimeSpan? pollInterval = null)
    {
        _reader = new MelonDsMemoryReader(host, port);
        _interval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public string Name => "melonDS";

    public EmulatorConnectionState State => _state;

    public event EventHandler<EmulatorConnectionState>? StateChanged;

    public bool CanRead => _reader.CanRead;

    public Task<byte[]?> ReadMemoryAsync(
        uint address,
        int length,
        CancellationToken cancellationToken = default) =>
        _reader.ReadMemoryAsync(address, length, cancellationToken);

    public async IAsyncEnumerable<RawPartySnapshot> ReadSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[]? previous = null;
        int previousCount = -1;

        SetState(EmulatorConnectionState.Connecting);

        while (!cancellationToken.IsCancellationRequested)
        {
            RawPartySnapshot? snapshot = null;

            try
            {
                snapshot = await ReadPartyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (snapshot is null)
            {
                // melonDS closed, the stub is off, or the game is not one we have mapped.
                // All of them are ordinary states of the world; keep asking.
                SetState(_state == EmulatorConnectionState.Streaming
                    ? EmulatorConnectionState.Reconnecting
                    : EmulatorConnectionState.Connecting);
            }
            else
            {
                SetState(EmulatorConnectionState.Streaming);

                bool changed = previousCount != snapshot.PartyCount
                    || previous is null
                    || !previous.AsSpan().SequenceEqual(snapshot.PartyData.Span);

                if (changed)
                {
                    previous = snapshot.PartyData.ToArray();
                    previousCount = snapshot.PartyCount;
                    yield return snapshot;
                }
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// One poll: who is this game, where does its party live, and what is in it. Returns
    /// null for anything that is not a complete, plausible answer: a half-read party is
    /// worse than none, because it parses.
    /// </summary>
    private async Task<RawPartySnapshot?> ReadPartyAsync(CancellationToken cancellationToken)
    {
        byte[]? header = await _reader
            .ReadMemoryAsync(Gen5MemoryMap.CartridgeHeader, Gen5MemoryMap.HeaderLength, cancellationToken)
            .ConfigureAwait(false);

        if (header is null || Gen5MemoryMap.ReadIdentity(header) is not GameIdentity game)
        {
            _game = null;
            return null;
        }

        _game = game;

        if (Locate(game.GameCode) is not (uint pointerAddress, uint offset, int slotSize))
        {
            return null;
        }

        byte[]? pointer = await _reader
            .ReadMemoryAsync(pointerAddress, 4, cancellationToken)
            .ConfigureAwait(false);

        if (pointer is null)
        {
            return null;
        }

        uint target = BitConverter.ToUInt32(pointer);
        uint head = target + offset;
        if (target is < 0x02000000 or >= 0x02400000 || head is < 0x02000000 or >= 0x02400000)
        {
            // The pointer does not point into main RAM, so the game has not built its save
            // blocks yet: it is still on the title screen, or mid-load.
            return null;
        }

        // The header, then each slot in a request of its own. The stub answers one request a
        // frame, and the game rewrites a Pokémon's record while the player walks, so a read
        // that splits a slot across two requests can join one frame's personality value and
        // checksum to the next frame's data: a record that fails its checksum, or decodes into
        // nonsense, though the game never held it. A slot fits in one request in both
        // generations, so no record is ever read across a frame boundary. See D-072.
        byte[]? partyHeader = await _reader
            .ReadMemoryAsync(head, Gen5MemoryMap.FirstSlotOffset, cancellationToken)
            .ConfigureAwait(false);

        if (partyHeader is null)
        {
            return null;
        }

        int count = BitConverter.ToInt32(partyHeader, Gen5MemoryMap.PartyCountOffset);
        if (count is < 0 or > Gen5MemoryMap.SlotCapacity)
        {
            // Following the pointer landed somewhere that is not a party. Saying nothing is
            // the only honest answer; the alternative is a team invented out of noise.
            return null;
        }

        var slots = new byte[slotSize * Gen5MemoryMap.SlotCapacity];
        for (int slot = 0; slot < Gen5MemoryMap.SlotCapacity; slot++)
        {
            byte[]? bytes = await _reader
                .ReadMemoryAsync(
                    (uint)(head + Gen5MemoryMap.FirstSlotOffset + (slot * slotSize)),
                    slotSize,
                    cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                return null;
            }

            bytes.CopyTo(slots, slot * slotSize);
        }

        return new RawPartySnapshot(
            game,
            count,
            slots,
            slotSize,
            DateTimeOffset.UtcNow,
            ++_sequence);
    }

    /// <summary>
    /// Where a mapped cartridge keeps the word to follow, how far past its target the party
    /// head sits, and how big a slot is. Both generations put a capacity and a count in front
    /// of six slots, so only these three differ. See D-067.
    /// </summary>
    private static (uint Pointer, uint Offset, int SlotSize)? Locate(string gameCode)
    {
        if (Gen5MemoryMap.For(gameCode) is Gen5MemoryMap gen5)
        {
            return (gen5.PartyPointer, 0, Gen5MemoryMap.SlotSize);
        }

        if (Gen4MemoryMap.For(gameCode) is Gen4MemoryMap gen4)
        {
            return (gen4.SaveBlockPointer, gen4.PartyOffset, Gen4MemoryMap.SlotSize);
        }

        return null;
    }

    /// <summary>
    /// The battle on the field, for a cartridge with a battle map. See D-076.
    /// </summary>
    /// <remarks>
    /// One 8-byte read outside a battle: the VBlank slot, whose handler says whether a battle is
    /// running. In one, three more: the battle type and context pointer, then each battler's
    /// record in a request of its own, so neither is joined across two frames (D-072). Only the
    /// fields of <see cref="RawBattler"/> leave this method; the rest of each record, the
    /// opponent's moves and stats among it, is dropped here (D-075).
    /// </remarks>
    public async Task<RawBattleSnapshot?> ReadBattleAsync(CancellationToken cancellationToken = default)
    {
        if (_game is not GameIdentity game || Gen4BattleMap.For(game.GameCode) is not Gen4BattleMap map)
        {
            return null;
        }

        byte[]? slot = await _reader.ReadMemoryAsync(map.VBlankSlot, 8, cancellationToken).ConfigureAwait(false);
        if (slot is null)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        uint handler = BitConverter.ToUInt32(slot, 0);
        uint system = BitConverter.ToUInt32(slot, 4);

        if (handler != map.BattleVBlankHandler)
        {
            return new RawBattleSnapshot(game, false, 0, null, null, now);
        }

        if (!InMainRam(system))
        {
            return null;
        }

        byte[]? head = await _reader
            .ReadMemoryAsync(system + Gen4BattleMap.BattleTypeOffset, 8, cancellationToken)
            .ConfigureAwait(false);

        if (head is null)
        {
            return null;
        }

        uint battleType = BitConverter.ToUInt32(head, 0);
        uint context = BitConverter.ToUInt32(head, 4);
        if (!InMainRam(context))
        {
            return null;
        }

        uint mons = context + Gen4BattleMap.BattleMonsOffset;
        RawBattler? player = await ReadBattlerAsync(mons, cancellationToken).ConfigureAwait(false);
        RawBattler? opponent = await ReadBattlerAsync(mons + Gen4BattleMap.BattleMonSize, cancellationToken)
            .ConfigureAwait(false);

        if (player is null || opponent is null)
        {
            return null;
        }

        return new RawBattleSnapshot(game, true, battleType, player, opponent, now);
    }

    private async Task<RawBattler?> ReadBattlerAsync(uint address, CancellationToken cancellationToken)
    {
        byte[]? bytes = await _reader
            .ReadMemoryAsync(address, Gen4BattleMap.BattleMonReadLength, cancellationToken)
            .ConfigureAwait(false);

        return bytes is null ? null : ToBattler(bytes);
    }

    /// <summary>The allowed fields of a <c>BattleMon</c>, and nothing else (D-075).</summary>
    internal static RawBattler ToBattler(ReadOnlySpan<byte> record) => new(
        SpeciesId: BitConverter.ToUInt16(record[Gen4BattleMap.SpeciesOffset..]),
        Form: record[Gen4BattleMap.FormOffset] & 0x1F,
        Level: record[Gen4BattleMap.LevelOffset],
        CurrentHp: BitConverter.ToInt32(record[Gen4BattleMap.HpOffset..]),
        MaxHp: (int)Math.Min(BitConverter.ToUInt32(record[Gen4BattleMap.MaxHpOffset..]), int.MaxValue),
        StatusFlags: BitConverter.ToUInt32(record[Gen4BattleMap.StatusOffset..]),
        Personality: BitConverter.ToUInt32(record[Gen4BattleMap.PersonalityOffset..]));

    private static bool InMainRam(uint address) => address is >= 0x02000000 and < 0x02400000;

    private void SetState(EmulatorConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public ValueTask DisposeAsync() => _reader.DisposeAsync();
}
