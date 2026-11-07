using UltimatePoKeSync.Analysis;
using UltimatePoKeSync.App.ViewModels;
using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.App.Tests;

/// <summary>The working in the Battle tab's chart tooltips (D-078, D-079).</summary>
public sealed class BattleWorkingTests
{
    private static readonly TypeFactor[] BugSteel =
        [new(PokemonType.Bug, 2), new(PokemonType.Steel, 2)];

    [Fact]
    public void A_dual_type_shows_each_factor_and_the_product()
    {
        Assert.Equal("FIRE → BUG x2 · STEEL x2 = x4", BattleViewModel.Working(PokemonType.Fire, BugSteel, 4));
    }

    [Fact]
    public void A_single_type_has_no_product()
    {
        TypeFactor[] grass = [new(PokemonType.Grass, 2)];

        Assert.Equal("FIRE → GRASS x2", BattleViewModel.Working(PokemonType.Fire, grass, 2));
    }

    [Fact]
    public void An_ability_override_is_named()
    {
        TypeFactor[] ghostPoison = [new(PokemonType.Ghost, 1), new(PokemonType.Poison, 2)];

        Assert.Equal(
            "GROUND → GHOST x1 · POISON x2 = x2, then Levitate makes it x0",
            BattleViewModel.Working(PokemonType.Ground, ghostPoison, 0, "Levitate"));
    }

    [Fact]
    public void Status_moves_have_no_working()
    {
        Assert.Equal(string.Empty, BattleViewModel.Working(PokemonType.Electric, [], 1));
    }
}
