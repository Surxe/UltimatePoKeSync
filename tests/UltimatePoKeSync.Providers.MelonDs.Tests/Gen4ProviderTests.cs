using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// The provider's chain for a Gen 4 cartridge: the header names it, the pointer leads to the
/// save block, and the party sits at a fixed offset past it in 236-byte slots. See D-067.
/// </summary>
public sealed class Gen4ProviderTests
{
    [Fact]
    public void ACartridgeHeaderNamesAGen4Game()
    {
        GameIdentity? game = Gen5MemoryMap.ReadIdentity(Gen5Ram.Header("IPGE", "POKEMON SS", 0));

        Assert.NotNull(game);
        Assert.Equal("IPGE", game.GameCode);
        Assert.Equal("POKEMON SS", game.Title);
        Assert.Equal(PokemonGeneration.Gen4, game.Generation);

        // And a Gen 5 header is still a Gen 5 header.
        Assert.Equal(
            PokemonGeneration.Gen5,
            Gen5MemoryMap.ReadIdentity(Gen5Ram.Header("IRBI", "POKEMON B", 0))!.Generation);
    }

    [Fact]
    public void OnlyTheVerifiedGen4CartridgeIsMapped()
    {
        Gen4MemoryMap map = Assert.IsType<Gen4MemoryMap>(Gen4MemoryMap.For("IPGE"));
        Assert.Equal(0x0211186Cu, map.SaveBlockPointer);
        Assert.Equal(0xD080u, map.PartyOffset);

        // HeartGold, other languages of SoulSilver, and the Sinnoh games are Gen 4 too, and
        // none of them has been run. They are recognised and refused, not read at IPGE's
        // address, for the reason D-035 gives.
        foreach (string code in new[] { "IPKE", "IPGP", "IPGD", "CPUE", "ADAE", "APAE" })
        {
            Assert.True(Gen4MemoryMap.IsGen4(code), code);
            Assert.Null(Gen4MemoryMap.For(code));
        }

        Assert.False(Gen4MemoryMap.IsGen4("IRBI"));
        Assert.False(Gen4MemoryMap.IsGen4("BPEE"));
        Assert.Equal(["SoulSilver (English)"], Gen4MemoryMap.Mapped);
    }

    [Fact]
    public async Task ThePartyIsFoundPastTheSaveBlockTheGameMovesOnEveryBoot()
    {
        // Two of the save block addresses measured live, a boot apart. The same code has to
        // find the party at both, which a hardcoded head could not.
        foreach (uint saveBlock in new uint[] { 0x0226F260, 0x0226F284 })
        {
            await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen4Ram.Build(saveBlock: saveBlock));
            await using var provider = new MelonDsProvider(
                "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

            RawPartySnapshot snapshot = await MelonDsProviderTests.FirstSnapshot(provider);

            Assert.Equal("IPGE", snapshot.Game.GameCode);
            Assert.Equal(PokemonGeneration.Gen4, snapshot.Game.Generation);
            Assert.Equal(6, snapshot.PartyCount);
            Assert.Equal(236, snapshot.SlotSize);
            Assert.Equal(Gen4Ram.FirstSlotMarker, snapshot.GetSlot(0).Span[0]);
            Assert.Equal(Gen4Ram.LastByteMarker, snapshot.GetSlot(5).Span[^1]);
        }
    }

    /// <summary>
    /// Before the save is loaded the pointer is not yet a main RAM address, and nothing may
    /// be read past it.
    /// </summary>
    [Fact]
    public async Task ASaveBlockPointerOutsideMainRamProducesNoSnapshot()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen4Ram.Build(saveBlock: 0));
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await MelonDsProviderTests.FirstSnapshotOrNull(
            provider, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task AnUnmappedGen4GameProducesNoSnapshot()
    {
        // HeartGold, laid out exactly like SoulSilver. It may well keep its party in the same
        // place, and that is precisely the guess this refuses to make.
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen4Ram.Build(gameCode: "IPKE"));
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await MelonDsProviderTests.FirstSnapshotOrNull(
            provider, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task ACountTheGameCannotHaveProducesNoSnapshot()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen4Ram.Build(partyCount: 7));
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await MelonDsProviderTests.FirstSnapshotOrNull(
            provider, TimeSpan.FromMilliseconds(400)));
    }

    /// <summary>
    /// The game rewrites a record while the player walks, and the stub answers one request a
    /// frame, so a slot split across two requests can be stitched from two frames. Each slot
    /// must arrive in a request of its own, in both generations. See D-072.
    /// </summary>
    [Theory]
    [InlineData("IPGE")]
    [InlineData("IRBI")]
    public async Task NoReadRequestSpansTwoSlots(string gameCode)
    {
        bool gen4 = gameCode == "IPGE";
        byte[] ram = gen4 ? Gen4Ram.Build() : Gen5Ram.Build();
        uint head = gen4 ? Gen4Ram.PartyHead : Gen5Ram.PartyHead;
        int slotSize = gen4 ? Gen4MemoryMap.SlotSize : Gen5MemoryMap.SlotSize;

        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, ram);
        await using var provider = new MelonDsProvider(
            "127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        await MelonDsProviderTests.FirstSnapshot(provider);

        uint firstSlot = head + (uint)Gen5MemoryMap.FirstSlotOffset;
        uint end = firstSlot + (uint)(slotSize * Gen5MemoryMap.SlotCapacity);
        (uint Address, int Length)[] partyReads =
        [
            .. stub.Commands.ToArray()
                .Where(command => command.StartsWith('m'))
                .Select(command => command[1..].Split(','))
                .Select(parts => (Convert.ToUInt32(parts[0], 16), Convert.ToInt32(parts[1], 16)))
                .Where(read => read.Item1 >= firstSlot && read.Item1 < end),
        ];

        Assert.NotEmpty(partyReads);
        Assert.All(partyReads, read =>
        {
            Assert.Equal(0u, (read.Address - firstSlot) % (uint)slotSize);
            Assert.Equal(slotSize, read.Length);
        });
    }
}
