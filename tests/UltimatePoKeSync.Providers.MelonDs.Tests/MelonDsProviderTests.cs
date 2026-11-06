using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// The whole chain below the parser: identify the cartridge, follow the pointer, read the
/// party, and say nothing when any of it does not add up. See D-040.
/// </summary>
public sealed class MelonDsProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ACartridgeHeaderNamesTheGame()
    {
        byte[] header = Gen5Ram.Header("IRBI", "POKEMON B", revision: 0);

        GameIdentity? game = Gen5MemoryMap.ReadIdentity(header);

        Assert.NotNull(game);
        Assert.Equal("IRBI", game.GameCode);
        Assert.Equal("POKEMON B", game.Title);
        Assert.Equal(PokemonGeneration.Gen5, game.Generation);
    }

    /// <summary>
    /// Before the game has booted far enough the header is not there yet, and what is read
    /// is whatever the memory happened to hold.
    /// </summary>
    [Fact]
    public void BytesThatAreNotAHeaderNameNoGame()
    {
        Assert.Null(Gen5MemoryMap.ReadIdentity(new byte[Gen5MemoryMap.HeaderLength]));
        Assert.Null(Gen5MemoryMap.ReadIdentity([1, 2, 3]));
    }

    [Fact]
    public void OnlyTheCartridgesThatWereVerifiedAreMapped()
    {
        // Five cartridges were run, and no two share an address. Version and language both
        // move it, which is the whole reason each code is measured. See D-054 and D-061.
        uint[] pointers =
        [
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IRBI")).PartyPointer,
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IRBO")).PartyPointer,
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IRAI")).PartyPointer,
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IRAO")).PartyPointer,
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IREO")).PartyPointer,
            Assert.IsType<Gen5MemoryMap>(Gen5MemoryMap.For("IRDO")).PartyPointer,
        ];

        Assert.Equal(
            [0x0224F88Cu, 0x0224F98Cu, 0x0224F8ACu, 0x0224F9ACu, 0x0223B4C4u, 0x0223B504u],
            pointers);
        Assert.Equal(pointers.Length, pointers.Distinct().Count());

        // Black 2 lands nowhere near the four originals, which is the evidence that the gaps
        // between those four were never a rule to extrapolate from.
        Assert.True(pointers[^1] < pointers[0] - 0x10000);

        // Japanese Black exists and is Gen 5, but nobody has run it. It must be refused rather
        // than read at the address of any cartridge above, however close they look.
        Assert.Null(Gen5MemoryMap.For("IRBJ"));
        Assert.True(Gen5MemoryMap.IsGen5("IRBJ"));

        // Italian Black 2 is a different cartridge again, and unmapped.
        Assert.Null(Gen5MemoryMap.For("IREI"));
        Assert.True(Gen5MemoryMap.IsGen5("IREI"));

        // And something that is not a Gen 5 game at all.
        Assert.False(Gen5MemoryMap.IsGen5("ADAE"));
    }

    [Fact]
    public async Task ThePartyIsFoundByFollowingThePointer()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen5Ram.Build());
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        RawPartySnapshot snapshot = await FirstSnapshot(provider);

        Assert.Equal("IRBI", snapshot.Game.GameCode);
        Assert.Equal(1, snapshot.PartyCount);
        Assert.Equal(6, snapshot.SlotCapacity);
        Assert.Equal(220, snapshot.SlotSize);

        // The marker the fake wrote into slot 0, arriving unmangled through six reads.
        Assert.Equal(Gen5Ram.SlotMarker, snapshot.GetSlot(0).Span[0]);
    }

    /// <summary>
    /// A pointer that leads somewhere that is not a party must produce nothing. The
    /// alternative is a team invented out of whatever those bytes happened to be.
    /// </summary>
    [Fact]
    public async Task APointerLeadingNowhereProducesNoSnapshot()
    {
        byte[] ram = Gen5Ram.Build(partyCount: 9);

        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, ram);
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await FirstSnapshotOrNull(provider, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task AGameThatIsNotMappedProducesNoSnapshot()
    {
        // Japanese Black: a real Gen 5 cartridge whose address nobody has verified. It has to
        // be a code that is genuinely unmapped, and this fixture has now moved twice as the
        // supported set grew, first off Italian White and then off English Black.
        byte[] ram = Gen5Ram.Build(gameCode: "IRBJ");

        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, ram);
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await FirstSnapshotOrNull(provider, TimeSpan.FromMilliseconds(400)));
    }

    /// <summary>A party sitting still must not be re-sent a hundred times a minute.</summary>
    [Fact]
    public async Task NothingIsEmittedWhileNothingChanges()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen5Ram.Build());
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));

        var seen = new List<RawPartySnapshot>();
        try
        {
            await foreach (RawPartySnapshot snapshot in provider.ReadSnapshotsAsync(cancellation.Token))
            {
                seen.Add(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // The deadline is how the loop ends.
        }

        Assert.Single(seen);
    }

    internal static async Task<RawPartySnapshot> FirstSnapshot(MelonDsProvider provider) =>
        await FirstSnapshotOrNull(provider, TimeSpan.FromSeconds(5))
        ?? throw new InvalidOperationException("no snapshot arrived");

    internal static async Task<RawPartySnapshot?> FirstSnapshotOrNull(
        MelonDsProvider provider,
        TimeSpan patience)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(patience);

        try
        {
            await foreach (RawPartySnapshot snapshot in provider.ReadSnapshotsAsync(cancellation.Token))
            {
                return snapshot;
            }
        }
        catch (OperationCanceledException)
        {
            // Running out of patience is one of the answers.
        }

        return null;
    }
}
