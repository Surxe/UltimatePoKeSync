using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Parsing.Tests;

public sealed class Gen4BattleDecoderTests
{
    private static readonly GameIdentity SoulSilver = new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private static readonly RawBattler Crobat = new(169, 0, 49, 155, 155, 0, 0x1234_5678);

    private static readonly RawBattler Geodude = new(74, 0, 10, 30, 30, 0, 0x0BAD_F00D);

    private static RawBattleSnapshot Raw(uint flags, RawBattler? player, RawBattler? opponent, bool inBattle = true) =>
        new(SoulSilver, inBattle, flags, player, opponent, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_wild_single_battle_gets_pokedex_data()
    {
        BattleSnapshot battle = new Gen4BattleDecoder().Decode(Raw(0, Crobat, Geodude))!;

        Assert.Equal(BattleKind.Single, battle.Kind);
        Assert.True(battle.IsWild);
        Assert.Equal(0x1234_5678u, battle.PlayerActivePersonality);

        OpponentSnapshot geodude = battle.Opponent!;
        Assert.Equal("GEODUDE", geodude.SpeciesName.ToUpperInvariant());
        Assert.Equal(PokemonType.Rock, geodude.PrimaryType);
        Assert.Equal(PokemonType.Ground, geodude.SecondaryType);
        Assert.Equal(255, geodude.CatchRate);
        Assert.Equal(10, geodude.Level);
    }

    [Fact]
    public void A_mono_type_has_no_second_type()
    {
        RawBattler zubat = new(41, 0, 8, 20, 20, 0, 1);
        RawBattler pikachu = new(25, 0, 8, 20, 20, 0, 1);

        Assert.Equal(PokemonType.Flying, new Gen4BattleDecoder().Decode(Raw(0, Crobat, zubat))!.Opponent!.SecondaryType);
        Assert.Equal(PokemonType.None, new Gen4BattleDecoder().Decode(Raw(0, Crobat, pikachu))!.Opponent!.SecondaryType);
    }

    [Theory]
    [InlineData(Gen4BattleDecoder.BattleTypeTrainer)]
    [InlineData(Gen4BattleDecoder.BattleTypeSafari)]
    [InlineData(Gen4BattleDecoder.BattleTypeBugContest)]
    public void No_standard_balls_in_trainer_safari_or_contest_battles(uint flags)
    {
        Assert.False(new Gen4BattleDecoder().Decode(Raw(flags, Crobat, Geodude))!.IsWild);
    }

    [Fact]
    public void A_double_battle_is_recognised_and_not_read()
    {
        BattleSnapshot battle = new Gen4BattleDecoder().Decode(
            Raw(Gen4BattleDecoder.BattleTypeTrainer | Gen4BattleDecoder.BattleTypeDoubles, Crobat, Geodude))!;

        Assert.Equal(BattleKind.Double, battle.Kind);
        Assert.Null(battle.Opponent);
    }

    [Fact]
    public void Out_of_battle_is_none()
    {
        Assert.False(new Gen4BattleDecoder().Decode(Raw(0, null, null, inBattle: false))!.IsRunning);
    }

    [Theory]
    [InlineData(0, 10, 30, 30)]
    [InlineData(494, 10, 30, 30)]
    [InlineData(74, 0, 30, 30)]
    [InlineData(74, 101, 30, 30)]
    [InlineData(74, 10, 31, 30)]
    [InlineData(74, 10, 0, 0)]
    public void An_implausible_opponent_is_refused(int species, int level, int hp, int maxHp)
    {
        RawBattler torn = new((ushort)species, 0, level, hp, maxHp, 0, 1);

        Assert.Null(new Gen4BattleDecoder().Decode(Raw(0, Crobat, torn)));
    }

    [Fact]
    public void An_empty_player_side_still_shows_the_opponent()
    {
        BattleSnapshot battle = new Gen4BattleDecoder().Decode(Raw(0, new RawBattler(0, 0, 0, 0, 0, 0, 0), Geodude))!;

        Assert.Null(battle.PlayerActivePersonality);
        Assert.NotNull(battle.Opponent);
    }

    [Fact]
    public void Status_uses_the_shared_status_byte()
    {
        RawBattler asleep = Geodude with { StatusFlags = 0b011 };

        Assert.Equal(StatusCondition.Sleep, new Gen4BattleDecoder().Decode(Raw(0, Crobat, asleep))!.Opponent!.Status);
    }
}
