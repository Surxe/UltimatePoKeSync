using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using Xunit;

namespace UltimatePoKeSync.Analysis.Tests;

public sealed class Gen3RulesTests
{
    private readonly Gen3Rules _rules = Gen3Rules.Instance;

    [Fact]
    public void ChartContainsTheSeventeenGen3TypesAndNoFairy()
    {
        Assert.Equal(17, _rules.TypeChart.Types.Count);
        Assert.DoesNotContain(PokemonType.Fairy, _rules.TypeChart.Types);
        Assert.Equal(PokemonType.Normal, _rules.TypeChart.Types[0]);
        Assert.Equal(PokemonType.Dark, _rules.TypeChart.Types[^1]);
    }

    [Theory]
    [InlineData(PokemonType.Ghost, PokemonType.Psychic, 2)]
    [InlineData(PokemonType.Ghost, PokemonType.Normal, 0)]
    [InlineData(PokemonType.Ghost, PokemonType.Steel, 0.5)]
    [InlineData(PokemonType.Dark, PokemonType.Steel, 0.5)]
    [InlineData(PokemonType.Poison, PokemonType.Steel, 0)]
    public void ChartPinsGenerationSpecificInteractions(
        PokemonType attackingType,
        PokemonType defendingType,
        double expected)
    {
        Assert.Equal(expected, _rules.TypeChart.GetMultiplier(attackingType, defendingType));
    }

    [Fact]
    public void DualTypesMultiplyBothComponents()
    {
        Assert.Equal(
            4,
            _rules.TypeChart.GetMultiplier(
                PokemonType.Ice, PokemonType.Grass, PokemonType.Flying));
        Assert.Equal(
            0.25,
            _rules.TypeChart.GetMultiplier(
                PokemonType.Grass, PokemonType.Fire, PokemonType.Flying));
    }

    [Theory]
    [InlineData(1, PokemonType.Normal, MoveCategory.Physical)] // Pound
    [InlineData(242, PokemonType.Dark, MoveCategory.Special)] // Crunch
    [InlineData(57, PokemonType.Water, MoveCategory.Special)] // Surf
    [InlineData(43, PokemonType.Normal, MoveCategory.Status)] // Leer
    [InlineData(0, PokemonType.None, MoveCategory.Status)]
    public void MoveCategoryUsesPowerThenTheGen3TypeSplit(
        int moveId,
        PokemonType moveType,
        MoveCategory expected)
    {
        Assert.Equal(expected, _rules.GetMoveCategory(moveId, moveType));
    }

    [Theory]
    [InlineData(67, true)] // Low Kick: variable power, ordinary type effectiveness
    [InlineData(237, true)] // Hidden Power: variable power, ordinary type effectiveness
    [InlineData(69, false)] // Seismic Toss: level-based fixed damage
    [InlineData(90, false)] // Fissure: one-hit knockout
    [InlineData(329, false)] // Sheer Cold: one-hit knockout
    [InlineData(43, false)] // Leer: status move
    public void OffensiveCoverageExcludesMovesWithoutSuperEffectiveDamage(
        int moveId,
        bool expected)
    {
        Assert.Equal(expected, _rules.CanProvideSuperEffectiveCoverage(moveId));
    }

    [Theory]
    [InlineData(Gen3AbilityIds.Levitate, PokemonType.Ground)]
    [InlineData(Gen3AbilityIds.FlashFire, PokemonType.Fire)]
    [InlineData(Gen3AbilityIds.VoltAbsorb, PokemonType.Electric)]
    [InlineData(Gen3AbilityIds.WaterAbsorb, PokemonType.Water)]
    public void AbsorbingAbilitiesCreateImmunities(int abilityId, PokemonType attackingType)
    {
        Assert.Equal(
            0,
            _rules.GetDefensiveMultiplier(
                attackingType, PokemonType.Normal, PokemonType.None, abilityId));
    }

    [Fact]
    public void ThickFatHalvesFireAndIceAfterTyping()
    {
        Assert.Equal(
            0.25,
            _rules.GetDefensiveMultiplier(
                PokemonType.Fire,
                PokemonType.Water,
                PokemonType.None,
                Gen3AbilityIds.ThickFat));
    }

    [Fact]
    public void WonderGuardAllowsOnlyNetSuperEffectiveTypes()
    {
        Assert.Equal(
            0,
            _rules.GetDefensiveMultiplier(
                PokemonType.Fire,
                PokemonType.Bug,
                PokemonType.Water,
                Gen3AbilityIds.WonderGuard));
        Assert.Equal(
            2,
            _rules.GetDefensiveMultiplier(
                PokemonType.Fire,
                PokemonType.Bug,
                PokemonType.None,
                Gen3AbilityIds.WonderGuard));
    }

    [Fact]
    public void ResolverDoesNotPretendUnsupportedGenerationsUseGen3Rules()
    {
        Assert.Same(_rules, GenerationRulesResolver.Default.Resolve(PokemonGeneration.Gen3));
        Assert.Null(GenerationRulesResolver.Default.Resolve(PokemonGeneration.Gen6));
    }
}
