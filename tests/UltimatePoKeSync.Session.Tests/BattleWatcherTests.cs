using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Session.Tests;

public sealed class BattleWatcherTests
{
    private static readonly GameIdentity SoulSilver = new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private static readonly OpponentSnapshot Geodude = new(
        74, "GEODUDE", 0, 10, PokemonType.Rock, PokemonType.Ground, 255, 30, 30, StatusCondition.None);

    private static readonly RawBattleSnapshot InBattle =
        new(SoulSilver, true, 0, null, null, DateTimeOffset.UnixEpoch);

    private static readonly RawBattleSnapshot OutOfBattle =
        new(SoulSilver, false, 0, null, null, DateTimeOffset.UnixEpoch);

    private static BattleSnapshot Wild(OpponentSnapshot opponent) =>
        new(SoulSilver, BattleKind.Single, true, opponent, 1, DateTimeOffset.UtcNow);

    private sealed class ScriptedReader(params RawBattleSnapshot?[] reads) : IBattleReader
    {
        private int _next;

        public Task<RawBattleSnapshot?> ReadBattleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(reads[Math.Min(_next++, reads.Length - 1)]);
    }

    /// <summary>Decodes "in battle" reads to the next scripted battle, so a test reads like a timeline.</summary>
    private static Func<RawBattleSnapshot, BattleSnapshot?> Decoder(params BattleSnapshot?[] battles)
    {
        int next = 0;
        return raw => raw.InBattle
            ? battles[Math.Min(next++, battles.Length - 1)]
            : BattleSnapshot.None with { Game = raw.Game };
    }

    private static async Task<List<BattleSnapshot>> Run(IBattleReader reader, Func<RawBattleSnapshot, BattleSnapshot?> decode, int polls)
    {
        var watcher = new BattleWatcher(reader, decode);
        var seen = new List<BattleSnapshot>();
        watcher.BattleChanged += (_, battle) => seen.Add(battle);

        for (int i = 0; i < polls; i++)
        {
            await watcher.PollOnceAsync();
        }

        return seen;
    }

    [Fact]
    public async Task Reports_a_battle_once_however_often_it_is_read()
    {
        List<BattleSnapshot> seen = await Run(
            new ScriptedReader(InBattle),
            Decoder(Wild(Geodude), Wild(Geodude), Wild(Geodude)),
            polls: 3);

        Assert.Single(seen);
        Assert.Equal("GEODUDE", seen[0].Opponent!.SpeciesName);
    }

    [Fact]
    public async Task Reports_hp_changes()
    {
        List<BattleSnapshot> seen = await Run(
            new ScriptedReader(InBattle),
            Decoder(Wild(Geodude), Wild(Geodude with { CurrentHp = 12 })),
            polls: 2);

        Assert.Equal([30, 12], seen.Select(battle => battle.Opponent!.CurrentHp));
    }

    [Fact]
    public async Task A_refused_or_unanswered_read_keeps_what_is_shown()
    {
        List<BattleSnapshot> seen = await Run(
            new ScriptedReader(InBattle, null, InBattle),
            Decoder(Wild(Geodude), null),
            polls: 3);

        Assert.Single(seen);
    }

    [Fact]
    public async Task One_read_saying_the_battle_ended_is_not_believed()
    {
        List<BattleSnapshot> seen = await Run(
            new ScriptedReader(InBattle, OutOfBattle, InBattle),
            Decoder(Wild(Geodude)),
            polls: 3);

        Assert.Single(seen);
        Assert.True(seen[0].IsRunning);
    }

    [Fact]
    public async Task Two_reads_saying_it_ended_clear_it()
    {
        List<BattleSnapshot> seen = await Run(
            new ScriptedReader(InBattle, OutOfBattle, OutOfBattle),
            Decoder(Wild(Geodude)),
            polls: 3);

        Assert.Equal(2, seen.Count);
        Assert.False(seen[1].IsRunning);
    }
}
