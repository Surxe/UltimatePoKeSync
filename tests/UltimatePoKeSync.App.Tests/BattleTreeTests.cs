using UltimatePoKeSync.App.ViewModels;
using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.App.Tests;

/// <summary>The Battle tab's two tree diagrams (D-081).</summary>
public sealed class BattleTreeTests
{
    private static readonly GameIdentity SoulSilver = new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private static readonly OpponentSnapshot Quagsire = new(
        195, "QUAGSIRE", 0, 38, PokemonType.Water, PokemonType.Ground, 90, 70, 100, StatusCondition.None);

    private static PokemonSnapshot Member(
        PokemonType primary, PokemonType secondary, int ability, string abilityName, params MoveSlot[] moves) => new()
    {
        SlotIndex = 0, SpeciesId = 1, SpeciesName = "MEMBER", Level = 40,
        PrimaryType = primary, SecondaryType = secondary, BaseStats = new StatBlock(50, 50, 50, 50, 50, 50),
        IndividualValues = new StatBlock(31, 31, 31, 31, 31, 31), EffortValues = new StatBlock(0, 0, 0, 0, 0, 0),
        CurrentStats = new StatBlock(100, 100, 100, 100, 100, 100), NatureId = 0, NatureName = "Hardy",
        AbilityId = ability, AbilityName = abilityName, HeldItemId = 0, HeldItemName = "-", Moves = moves,
        IsEgg = false, IsShiny = false, PersonalityValue = 0, Gender = PokemonGender.Unknown,
        CurrentHp = 100, Status = StatusCondition.None, Friendship = 70, Experience = 0,
    };

    private static BattleViewModel Battle(PokemonSnapshot active, OpponentSnapshot opponent)
    {
        var viewModel = new BattleViewModel();
        viewModel.UpdateParty(new PartySnapshot(SoulSilver, [active], DateTimeOffset.UnixEpoch, 1, []));
        viewModel.UpdateBattle(new BattleSnapshot(SoulSilver, BattleKind.Single, true, opponent, 0u, DateTimeOffset.UnixEpoch));
        return viewModel;
    }

    [Fact]
    public void A_move_branches_to_each_opponent_type_and_joins_in_its_result()
    {
        BattleViewModel viewModel = Battle(
            Member(PokemonType.Electric, PokemonType.None, 9, "Static",
                new MoveSlot(85, "Thunderbolt", PokemonType.Electric, 15, 15),
                new MoveSlot(202, "Giga Drain", PokemonType.Grass, 10, 10)),
            Quagsire);

        TreeRow thunderbolt = viewModel.Offense[0];
        Assert.Equal("Thunderbolt", thunderbolt.Label);
        Assert.Equal("STAB", thunderbolt.Tag);
        Assert.Equal(["WATER", "GROUND"], thunderbolt.Branches.Select(branch => branch.Type.UpperType));
        Assert.Equal(["x2", "x0"], thunderbolt.Branches.Select(branch => branch.Multiplier));
        Assert.Equal("x0", thunderbolt.Result);

        TreeRow gigaDrain = viewModel.Offense[1];
        Assert.Equal(["x2", "x2"], gigaDrain.Branches.Select(branch => branch.Multiplier));
        Assert.Equal("x4", gigaDrain.Result);
    }

    [Fact]
    public void A_status_move_has_no_branches()
    {
        BattleViewModel viewModel = Battle(
            Member(PokemonType.Electric, PokemonType.None, 9, "Static",
                new MoveSlot(86, "Thunder Wave", PokemonType.Electric, 20, 20)),
            Quagsire);

        TreeRow thunderWave = viewModel.Offense.Single();
        Assert.False(thunderWave.HasBranches);
        Assert.Equal("status", thunderWave.Result);
        Assert.Equal(string.Empty, thunderWave.Tag);
    }

    [Fact]
    public void Each_opponent_type_branches_to_yours_and_an_ability_is_starred()
    {
        BattleViewModel viewModel = Battle(
            Member(PokemonType.Ghost, PokemonType.Poison, 26, "Levitate",
                new MoveSlot(247, "Shadow Ball", PokemonType.Ghost, 15, 15)),
            Quagsire);

        Assert.Equal(["WATER", "GROUND"], viewModel.Defense.Select(row => row.Type.UpperType));

        TreeRow ground = viewModel.Defense[1];
        Assert.Equal(["GHOST", "POISON"], ground.Branches.Select(branch => branch.Type.UpperType));
        Assert.Equal(["x1", "x2"], ground.Branches.Select(branch => branch.Multiplier));
        Assert.Equal("x0*", ground.Result);
        Assert.Contains("Levitate", ground.Note);
        Assert.Contains("x2", ground.Note);

        Assert.Equal("x1", viewModel.Defense[0].Result);
        Assert.False(viewModel.Defense[0].HasNote);
    }

    [Fact]
    public void Without_the_active_pokemon_the_trees_are_empty()
    {
        var viewModel = new BattleViewModel();
        viewModel.UpdateParty(new PartySnapshot(
            SoulSilver, [Member(PokemonType.Electric, PokemonType.None, 9, "Static")], DateTimeOffset.UnixEpoch, 1, []));
        viewModel.UpdateBattle(new BattleSnapshot(SoulSilver, BattleKind.Single, true, Quagsire, 99u, DateTimeOffset.UnixEpoch));

        Assert.True(viewModel.HasMatchup);
        Assert.False(viewModel.HasActive);
        Assert.Empty(viewModel.Offense);
        Assert.Empty(viewModel.Defense);
    }
}
