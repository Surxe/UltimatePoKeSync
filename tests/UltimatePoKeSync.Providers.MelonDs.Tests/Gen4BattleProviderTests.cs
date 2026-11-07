using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// The battle chain of D-076: the VBlank slot's handler says whether a battle runs, its
/// argument is the BattleSystem, and the battlers sit past its context. Addresses are the ones
/// measured live; the BattleSystem moves between boots, so the tests use two of them.
/// </summary>
public sealed class Gen4BattleProviderTests
{
    private const uint Slot = 0x021D110C;
    private const uint BattleHandler = 0x02239731;
    private const uint FieldHandler = 0x021E5901;

    private static void Write(byte[] ram, uint address, uint value) =>
        BitConverter.GetBytes(value).CopyTo(ram, (int)(address - Gen5Ram.BaseAddress));

    private static void Write16(byte[] ram, uint address, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(ram, (int)(address - Gen5Ram.BaseAddress));

    private static void Battler(byte[] ram, uint at, ushort species, byte level, int hp, uint maxHp, uint pid, uint status = 0)
    {
        Write16(ram, at, species);
        ram[at + 0x0C - Gen5Ram.BaseAddress] = 0x55; // a move id: must never reach the snapshot
        ram[at + 0x26 - Gen5Ram.BaseAddress] = 0xE0; // shiny bits above a form of 0
        ram[at + 0x34 - Gen5Ram.BaseAddress] = level;
        Write(ram, at + 0x4C, (uint)hp);
        Write(ram, at + 0x50, maxHp);
        Write(ram, at + 0x68, pid);
        Write(ram, at + 0x6C, status);
    }

    private static byte[] InBattle(uint system, uint battleType = 0)
    {
        byte[] ram = Gen4Ram.Build();
        uint context = system + 0x3000;

        Write(ram, Slot, BattleHandler);
        Write(ram, Slot + 4, system);
        Write(ram, system + 0x2C, battleType);
        Write(ram, system + 0x30, context);
        Battler(ram, context + 0x2D40, 169, 49, 155, 155, 0x7A5ED2BA);
        Battler(ram, context + 0x2D40 + 0xC0, 104, 13, 39, 39, 0x31EF4C37, status: 0x40);
        return ram;
    }

    private static async Task<RawBattleSnapshot?> ReadBattle(byte[] ram)
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, ram);
        await using var provider = new MelonDsProvider("127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        // The battle read relies on the party loop having named the cartridge.
        await MelonDsProviderTests.FirstSnapshot(provider);
        return await provider.ReadBattleAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(0x022C02C8u)]
    [InlineData(0x022C0278u)]
    public async Task The_battle_is_found_through_the_vblank_slot_wherever_it_moved(uint system)
    {
        RawBattleSnapshot battle = (await ReadBattle(InBattle(system)))!;

        Assert.True(battle.InBattle);
        Assert.Equal("IPGE", battle.Game.GameCode);
        Assert.Equal(new RawBattler(169, 0, 49, 155, 155, 0, 0x7A5ED2BA), battle.Player);
        Assert.Equal(new RawBattler(104, 0, 13, 39, 39, 0x40, 0x31EF4C37), battle.Opponent);
    }

    [Fact]
    public async Task The_battle_type_is_passed_through()
    {
        RawBattleSnapshot battle = (await ReadBattle(InBattle(0x022C02C8, battleType: 1)))!;

        Assert.Equal(1u, battle.BattleTypeFlags);
    }

    [Fact]
    public async Task Another_handler_in_the_slot_means_no_battle()
    {
        byte[] ram = InBattle(0x022C02C8);
        Write(ram, Slot, FieldHandler);

        RawBattleSnapshot battle = (await ReadBattle(ram))!;

        Assert.False(battle.InBattle);
        Assert.Null(battle.Opponent);
    }

    [Fact]
    public async Task A_context_outside_main_ram_is_no_answer()
    {
        byte[] ram = InBattle(0x022C02C8);
        Write(ram, 0x022C02C8 + 0x30, 0);

        Assert.Null(await ReadBattle(ram));
    }

    [Fact]
    public async Task An_unmapped_cartridge_has_no_battle_read()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, Gen5Ram.Build());
        await using var provider = new MelonDsProvider("127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));
        await MelonDsProviderTests.FirstSnapshot(provider);

        Assert.Null(await provider.ReadBattleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nothing_is_read_before_the_cartridge_is_known()
    {
        await using var stub = new FakeGdbStub(Gen5Ram.BaseAddress, InBattle(0x022C02C8));
        await using var provider = new MelonDsProvider("127.0.0.1", stub.Port, TimeSpan.FromMilliseconds(20));

        Assert.Null(await provider.ReadBattleAsync(TestContext.Current.CancellationToken));
    }
}
