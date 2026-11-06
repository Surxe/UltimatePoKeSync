using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using Xunit;

namespace UltimatePoKeSync.Analysis.Tests;

/// <summary>
/// HeartGold and SoulSilver battle rules: Gen 5's shape, Gen 4's numbers, and the two
/// abilities that were not yet immunities. See D-070.
/// </summary>
public sealed class Gen4RulesTests
{
    private static Gen4Rules Rules => Gen4Rules.Instance;

    /// <summary>Gen 4 is where the split begins: category belongs to the move.</summary>
    [Theory]
    [InlineData(44, PokemonType.Dark, MoveCategory.Physical)]     // Bite
    [InlineData(399, PokemonType.Dark, MoveCategory.Special)]     // Dark Pulse
    [InlineData(7, PokemonType.Fire, MoveCategory.Physical)]      // Fire Punch
    [InlineData(53, PokemonType.Fire, MoveCategory.Special)]      // Flamethrower
    [InlineData(349, PokemonType.Dragon, MoveCategory.Status)]    // Dragon Dance
    public void EachMoveCarriesItsOwnCategory(int moveId, PokemonType type, MoveCategory expected) =>
        Assert.Equal(expected, Rules.GetMoveCategory(moveId, type));

    /// <summary>Powers that changed in Gen 5 must be Gen 4's here.</summary>
    [Theory]
    [InlineData(33, 35)]   // Tackle, 50 in Black
    [InlineData(202, 60)]  // Giga Drain, 75 in Black
    [InlineData(409, 60)]  // Drain Punch, 75 in Black
    [InlineData(434, 140)] // Draco Meteor
    public void BasePowerIsTheGenerationsOwn(int moveId, int expected) =>
        Assert.Equal(expected, Rules.GetMoveBasePower(moveId));

    [Fact]
    public void TackleIsWeakerThanInBlack() =>
        Assert.True(Rules.GetMoveBasePower(33) < Gen5Rules.Instance.GetMoveBasePower(33));

    [Fact]
    public void MovesBeyondTheGenerationAreNotInvented()
    {
        // Shadow Force is the last Gen 4 move; Hone Claws, the first of Gen 5, did not exist.
        Assert.True(Rules.GetMoveBasePower(467) > 0);
        Assert.Equal(0, Rules.GetMoveBasePower(468));
        Assert.False(Rules.CanProvideSuperEffectiveCoverage(468));
        Assert.Equal(MoveCategory.Status, Rules.GetMoveCategory(468, PokemonType.Normal));
    }

    [Fact]
    public void FixedDamageMovesCoverNothing()
    {
        Assert.False(Rules.CanProvideSuperEffectiveCoverage(69));  // Seismic Toss
        Assert.False(Rules.CanProvideSuperEffectiveCoverage(368)); // Metal Burst
        Assert.True(Rules.CanProvideSuperEffectiveCoverage(53));   // Flamethrower
    }

    /// <summary>
    /// The one real disagreement with Gen 5. A Lightning Rod or Storm Drain holder still
    /// takes the hit in SoulSilver, so treating it as immune would hide a weakness.
    /// </summary>
    [Theory]
    [InlineData(31, PokemonType.Electric)]  // Lightning Rod
    [InlineData(114, PokemonType.Water)]    // Storm Drain
    public void RedirectingAbilitiesAreNotImmunitiesYet(int abilityId, PokemonType attacking)
    {
        // A Water defender: weak to Electric and resisting Water. What matters is that the
        // ability does not zero the hit out in Gen 4 and does in Gen 5.
        double gen4 = Rules.GetDefensiveMultiplier(
            attacking, PokemonType.Water, PokemonType.None, abilityId);
        double gen5 = Gen5Rules.Instance.GetDefensiveMultiplier(
            attacking, PokemonType.Water, PokemonType.None, abilityId);

        Assert.NotEqual(0, gen4);
        Assert.Equal(0, gen5);
    }

    [Theory]
    [InlineData(Gen4AbilityIds.MotorDrive, PokemonType.Electric)]
    [InlineData(Gen4AbilityIds.VoltAbsorb, PokemonType.Electric)]
    [InlineData(Gen4AbilityIds.DrySkin, PokemonType.Water)]
    [InlineData(Gen4AbilityIds.WaterAbsorb, PokemonType.Water)]
    [InlineData(Gen4AbilityIds.FlashFire, PokemonType.Fire)]
    [InlineData(Gen4AbilityIds.Levitate, PokemonType.Ground)]
    public void Gen4ImmunitiesHold(int abilityId, PokemonType attacking) =>
        Assert.Equal(0, Rules.GetDefensiveMultiplier(
            attacking, PokemonType.Normal, PokemonType.None, abilityId));

    [Fact]
    public void SapSipperDoesNothingBecauseItDoesNotExistYet() =>
        Assert.Equal(1, Rules.GetDefensiveMultiplier(
            PokemonType.Grass, PokemonType.Normal, PokemonType.None, abilityId: 157));

    [Fact]
    public void TheTypeChartMatchesGenFiveEverywhere()
    {
        foreach (PokemonType attacking in Rules.TypeChart.Types)
        {
            foreach (PokemonType defending in Rules.TypeChart.Types)
            {
                Assert.Equal(
                    Gen5Rules.Instance.TypeChart.GetMultiplier(attacking, defending),
                    Rules.TypeChart.GetMultiplier(attacking, defending));
            }
        }

        Assert.Equal(PokemonGeneration.Gen4, Rules.TypeChart.Generation);
    }

    [Fact]
    public void TheStatFormulaAgreesWithGenFive()
    {
        var baseStats = new StatBlock(91, 134, 95, 100, 100, 80); // Dragonite
        var ivs = new StatBlock(11, 6, 29, 21, 26, 10);
        var evs = new StatBlock(65, 119, 41, 104, 26, 155);

        Assert.Equal(
            Gen5Rules.Instance.CalculateStats(58, baseStats, ivs, evs, natureId: 2),
            Rules.CalculateStats(58, baseStats, ivs, evs, natureId: 2));
    }

    [Fact]
    public void TheResolverKnowsGenFour()
    {
        IGenerationRules? rules = GenerationRulesResolver.Default.Resolve(PokemonGeneration.Gen4);

        Assert.Same(Rules, rules);
    }

    [Fact]
    public void TheMoveCatalogNamesEveryGen4Move()
    {
        ShowdownGen4MoveCatalog catalog = ShowdownGen4MoveCatalog.Instance;

        Assert.Equal(PokemonGeneration.Gen4, catalog.Generation);
        Assert.Equal(467, catalog.MoveCount);
        Assert.Equal("Draco Meteor", catalog.Find(434)?.Name);
        Assert.Equal(PokemonType.Dragon, catalog.Find("dracometeor")?.Type);
        Assert.Null(catalog.Find(468));
    }
}
