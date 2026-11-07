using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using Xunit;

namespace UltimatePoKeSync.Analysis.Tests;

public sealed class BattleMatchupAnalyzerTests
{
    private static readonly GameIdentity SoulSilver =
        new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private static readonly MoveSlot Thunderbolt = AnalysisTestData.Move(85, "Thunderbolt", PokemonType.Electric);
    private static readonly MoveSlot Surf = AnalysisTestData.Move(57, "Surf", PokemonType.Water);
    private static readonly MoveSlot QuickAttack = AnalysisTestData.Move(98, "Quick Attack", PokemonType.Normal);
    private static readonly MoveSlot ThunderWave = AnalysisTestData.Move(86, "Thunder Wave", PokemonType.Electric);
    private static readonly MoveSlot SeismicToss = AnalysisTestData.Move(69, "Seismic Toss", PokemonType.Fighting);
    private static readonly MoveSlot RockSlide = AnalysisTestData.Move(157, "Rock Slide", PokemonType.Rock);
    private static readonly MoveSlot Earthquake = AnalysisTestData.Move(89, "Earthquake", PokemonType.Ground);
    private static readonly MoveSlot Flamethrower = AnalysisTestData.Move(53, "Flamethrower", PokemonType.Fire);

    private static readonly OpponentSnapshot Gyarados = new(
        130, "GYARADOS", 0, 32, PokemonType.Water, PokemonType.Flying, 45, 100, 100, StatusCondition.None);

    private static PokemonSnapshot Raichu() => AnalysisTestData.Member(
        slot: 0, primaryType: PokemonType.Electric, speciesName: "Raichu", speciesId: 26,
        moves: [Thunderbolt, Surf, QuickAttack, ThunderWave]);

    private static PokemonSnapshot Golem() => AnalysisTestData.Member(
        slot: 1, primaryType: PokemonType.Rock, secondaryType: PokemonType.Ground,
        speciesName: "Golem", speciesId: 76, moves: [RockSlide, Earthquake]);

    private static PokemonSnapshot Ampharos() => AnalysisTestData.Member(
        slot: 2, primaryType: PokemonType.Electric, speciesName: "Ampharos", speciesId: 181,
        moves: [Thunderbolt]);

    private static PartySnapshot Party(params PokemonSnapshot[] members) =>
        new(SoulSilver, members, DateTimeOffset.UnixEpoch, 1, []);

    private static BattleSnapshot Battle(
        OpponentSnapshot opponent, uint? active, bool wild = true, BattleKind kind = BattleKind.Single) =>
        new(SoulSilver, kind, wild, opponent, active, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Electric_on_water_flying_is_four_times()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer().Analyze(Party(Raichu()), Battle(Gyarados, 0))!;

        MoveMatchup thunderbolt = matchup.ActiveMoves.Single(move => move.Move.MoveId == 85);
        Assert.Equal(4, thunderbolt.Multiplier);
        Assert.True(thunderbolt.IsStab);
        Assert.Equal(MoveEffect.Damaging, thunderbolt.Effect);
        Assert.Equal(0.5, matchup.ActiveMoves.Single(move => move.Move.MoveId == 57).Multiplier);
        Assert.Equal(MoveEffect.Status, matchup.ActiveMoves.Single(move => move.Move.MoveId == 86).Effect);
    }

    [Fact]
    public void Incoming_assumes_the_opponents_own_types()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer().Analyze(Party(Raichu()), Battle(Gyarados, 0))!;

        Assert.Equal([PokemonType.Water, PokemonType.Flying], matchup.Incoming.Select(hit => hit.AttackingType));
        Assert.Equal(1, matchup.Incoming[0].Multiplier);
        Assert.Equal(0.5, matchup.Incoming[1].Multiplier);
    }

    [Fact]
    public void Switch_options_keep_party_order_not_best_first()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer()
            .Analyze(Party(Raichu(), Golem(), Ampharos()), Battle(Gyarados, 0))!;

        // Golem takes x4 from Water but keeps its slot ahead of Ampharos: the tab does not
        // rank (D-078).
        Assert.Equal(["Raichu", "Golem", "Ampharos"], matchup.SwitchOptions.Select(option => option.Member.SpeciesName));
        Assert.True(matchup.SwitchOptions[0].IsActive);
        Assert.Equal(4, matchup.SwitchOptions[1].WorstIncoming);
        Assert.Equal([PokemonType.Water, PokemonType.Flying], matchup.SwitchOptions[1].Incoming.Select(hit => hit.AttackingType));
        Assert.Equal(157, matchup.SwitchOptions[1].BestMove!.Move.MoveId);
    }

    [Fact]
    public void A_dual_type_result_carries_one_factor_per_defending_type()
    {
        var forretress = new OpponentSnapshot(
            205, "FORRETRESS", 0, 40, PokemonType.Bug, PokemonType.Steel, 75, 100, 100, StatusCondition.None);
        PokemonSnapshot charizard = AnalysisTestData.Member(
            slot: 0, primaryType: PokemonType.Fire, secondaryType: PokemonType.Flying, moves: [Flamethrower]);

        MoveMatchup flamethrower = new BattleMatchupAnalyzer()
            .Analyze(Party(charizard), Battle(forretress, 0))!.ActiveMoves.Single();

        Assert.Equal(4, flamethrower.Multiplier);
        Assert.Equal(
            [new TypeFactor(PokemonType.Bug, 2), new TypeFactor(PokemonType.Steel, 2)],
            flamethrower.Factors);
    }

    [Fact]
    public void An_immunity_inside_a_dual_type_shows_in_the_factors()
    {
        MoveMatchup earthquake = new BattleMatchupAnalyzer()
            .Analyze(Party(Golem()), Battle(Gyarados, active: 1))!
            .ActiveMoves.Single(move => move.Move.MoveId == 89);

        Assert.Equal(0, earthquake.Multiplier);
        Assert.Equal(
            [new TypeFactor(PokemonType.Water, 1), new TypeFactor(PokemonType.Flying, 0)],
            earthquake.Factors);
    }

    [Fact]
    public void An_ability_that_overrides_the_chart_is_flagged()
    {
        var dugtrio = new OpponentSnapshot(
            51, "DUGTRIO", 0, 30, PokemonType.Ground, PokemonType.None, 50, 100, 100, StatusCondition.None);
        PokemonSnapshot gengar = AnalysisTestData.Member(
            slot: 0, primaryType: PokemonType.Ghost, secondaryType: PokemonType.Poison,
            abilityId: Gen4AbilityIds.Levitate, moves: [Thunderbolt]);
        PokemonSnapshot raichu = Raichu() with { SlotIndex = 1, PersonalityValue = 1 };

        BattleMatchup matchup = new BattleMatchupAnalyzer().Analyze(Party(gengar, raichu), Battle(dugtrio, 0))!;

        IncomingHit levitating = matchup.Incoming.Single();
        Assert.Equal(0, levitating.Multiplier);
        Assert.True(levitating.AbilityAdjusted);
        Assert.Equal(
            [new TypeFactor(PokemonType.Ghost, 1), new TypeFactor(PokemonType.Poison, 2)],
            levitating.Factors);

        IncomingHit plain = matchup.SwitchOptions.Single(option => option.Member.SpeciesName == "Raichu").Incoming.Single();
        Assert.Equal(2, plain.Multiplier);
        Assert.False(plain.AbilityAdjusted);
    }

    [Fact]
    public void The_active_pokemon_is_found_by_personality_not_by_slot()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer()
            .Analyze(Party(Raichu(), Golem()), Battle(Gyarados, active: 1))!;

        Assert.Equal("Golem", matchup.Active!.SpeciesName);
    }

    [Fact]
    public void An_unmatched_active_pokemon_still_shows_the_opponent()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer()
            .Analyze(Party(Raichu()), Battle(Gyarados, active: 99))!;

        Assert.Null(matchup.Active);
        Assert.Empty(matchup.ActiveMoves);
        Assert.NotEmpty(matchup.Weaknesses);
    }

    [Fact]
    public void Weaknesses_group_the_chart_and_leave_out_neutral()
    {
        BattleMatchup matchup = new BattleMatchupAnalyzer().Analyze(Party(Raichu()), Battle(Gyarados, 0))!;

        Assert.Equal([4, 2, 0.5, 0], matchup.Weaknesses.Select(bucket => bucket.Multiplier));
        Assert.Equal([PokemonType.Electric], matchup.Weaknesses[0].Types);
        Assert.Equal([PokemonType.Rock], matchup.Weaknesses[1].Types);
        Assert.Equal([PokemonType.Ground], matchup.Weaknesses[3].Types);
    }

    [Fact]
    public void Fixed_damage_keeps_immunities_but_drops_the_scaling()
    {
        var gastly = new OpponentSnapshot(
            92, "GASTLY", 0, 20, PokemonType.Ghost, PokemonType.Poison, 190, 50, 50, StatusCondition.None);
        var gyarados = Gyarados;
        PokemonSnapshot tosser = AnalysisTestData.Member(slot: 0, moves: [SeismicToss]);

        Assert.Equal(0, new BattleMatchupAnalyzer().Analyze(Party(tosser), Battle(gastly, 0))!.ActiveMoves[0].Multiplier);
        MoveMatchup vsGyarados = new BattleMatchupAnalyzer().Analyze(Party(tosser), Battle(gyarados, 0))!.ActiveMoves[0];
        Assert.Equal(MoveEffect.Fixed, vsGyarados.Effect);
        Assert.Equal(1, vsGyarados.Multiplier);
    }

    [Fact]
    public void Gen4_steel_still_resists_ghost_and_dark()
    {
        var gengar = new OpponentSnapshot(
            94, "GENGAR", 0, 40, PokemonType.Ghost, PokemonType.Poison, 45, 100, 100, StatusCondition.None);
        PokemonSnapshot steelix = AnalysisTestData.Member(
            slot: 0, primaryType: PokemonType.Steel, secondaryType: PokemonType.Ground, moves: [Earthquake]);

        BattleMatchup matchup = new BattleMatchupAnalyzer().Analyze(Party(steelix), Battle(gengar, 0))!;

        Assert.Equal(0.5, matchup.Incoming.Single(hit => hit.AttackingType == PokemonType.Ghost).Multiplier);
        Assert.Equal(0, matchup.Incoming.Single(hit => hit.AttackingType == PokemonType.Poison).Multiplier);
    }

    [Fact]
    public void Catch_chances_only_in_a_wild_battle()
    {
        var analyzer = new BattleMatchupAnalyzer();

        Assert.Equal(3, analyzer.Analyze(Party(Raichu()), Battle(Gyarados, 0, wild: true))!.CatchChances.Count);
        Assert.Empty(analyzer.Analyze(Party(Raichu()), Battle(Gyarados, 0, wild: false))!.CatchChances);
    }

    [Fact]
    public void Nothing_outside_a_single_battle()
    {
        var analyzer = new BattleMatchupAnalyzer();

        Assert.Null(analyzer.Analyze(Party(Raichu()), BattleSnapshot.None));
        Assert.Null(analyzer.Analyze(Party(Raichu()), Battle(Gyarados, 0, kind: BattleKind.Double)));
    }

    /// <summary>
    /// The fairness rule of D-075, held by the shape of the record: nothing that is not on the
    /// screen or in a Pokédex can reach the UI, because there is no field to carry it.
    /// </summary>
    [Fact]
    public void The_opponent_record_carries_nothing_the_screen_does_not_show()
    {
        string[] fields = [.. typeof(OpponentSnapshot).GetProperties().Select(property => property.Name).Order()];

        Assert.Equal(
            [
                "CatchRate", "CurrentHp", "Form", "HpFraction", "IsFainted", "Level", "MaxHp",
                "PrimaryType", "SecondaryType", "SpeciesId", "SpeciesName", "Status",
            ],
            fields);
    }
}
