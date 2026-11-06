using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using Xunit;

namespace UltimatePoKeSync.GameData.Learnsets.Tests;

/// <summary>
/// HeartGold and SoulSilver learnsets, evolutions and base stats, read from PKHeX. See D-071.
/// </summary>
public sealed class PKHeXGen4SourcesTests
{
    private static readonly GameIdentity SoulSilver =
        new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private static readonly GameIdentity Platinum =
        new("CPUE", "POKEMON PL", 0, PokemonGeneration.Gen4);

    [Fact]
    public void SandshrewLearnsByLevelAndByMachine()
    {
        IReadOnlyList<LearnableMove> moves =
            PKHeXGen4MoveLearnSource.Instance.FindLearnableMoves(SoulSilver, 27, 100);

        LearnableMove scratch = Assert.Single(moves, move => move.Move.Name == "Scratch");
        Assert.Equal(MoveLearnMethod.LevelUp, scratch.Method);
        Assert.NotNull(scratch.LearnedAtLevel);

        // Earthquake is TM26 in every Gen 4 game.
        Assert.Contains(moves, move => move.Move.Name == "Earthquake" && move.Method == MoveLearnMethod.Machine);

        // Nothing past Gen 4 can appear, because the catalog it is named through ends at 467.
        Assert.All(moves, move => Assert.InRange(move.Move.MoveId, 1, 467));
    }

    [Fact]
    public void ALowerLevelCapHidesLaterLevelUpMoves()
    {
        int early = PKHeXGen4MoveLearnSource.Instance.FindLearnableMoves(SoulSilver, 27, 10)
            .Count(move => move.Method == MoveLearnMethod.LevelUp);
        int late = PKHeXGen4MoveLearnSource.Instance.FindLearnableMoves(SoulSilver, 27, 100)
            .Count(move => move.Method == MoveLearnMethod.LevelUp);

        Assert.True(early < late);
    }

    /// <summary>
    /// Platinum teaches at levels of its own, so it is refused rather than given Johto's
    /// table, for the reason D-027 gives.
    /// </summary>
    [Fact]
    public void OnlyHeartGoldAndSoulSilverAreAnswered()
    {
        Assert.True(PKHeXGen4MoveLearnSource.Instance.Supports(SoulSilver));
        Assert.True(PKHeXGen4MoveLearnSource.Instance.Supports(SoulSilver with { GameCode = "IPKE" }));
        Assert.False(PKHeXGen4MoveLearnSource.Instance.Supports(Platinum));
        Assert.False(PKHeXGen4EvolutionSource.Instance.Supports(Platinum));
        Assert.False(PKHeXGen4MoveLearnSource.Instance.Supports(SoulSilver with { Generation = PokemonGeneration.Gen5 }));
    }

    [Fact]
    public void SpeciesBeyondGen4HaveNothingToLearn() =>
        Assert.Empty(PKHeXGen4MoveLearnSource.Instance.FindLearnableMoves(SoulSilver, 495, 100));

    [Fact]
    public void SandshrewBecomesSandslashAtTwentyTwo()
    {
        EvolutionStep step = Assert.Single(PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, 27));

        Assert.Equal(28, step.IntoSpeciesId);
        Assert.Equal(EvolutionTrigger.Level, step.Trigger);
        Assert.Equal(22, step.Level);
    }

    [Fact]
    public void GligarNeedsARazorFangAtNight()
    {
        EvolutionStep step = Assert.Single(PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, 207));

        Assert.Equal("Gliscor", step.IntoSpeciesName);
        Assert.Contains("Razor Fang", step.Requirement, StringComparison.Ordinal);
        Assert.Contains("at night", step.Requirement, StringComparison.Ordinal);
        Assert.Null(step.Level);
    }

    [Fact]
    public void PiloswineNeedsToKnowAncientPower()
    {
        EvolutionStep step = Assert.Single(PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, 221));

        Assert.Equal("Mamoswine", step.IntoSpeciesName);
        Assert.Contains("Ancient Power", step.Requirement, StringComparison.Ordinal);
    }

    [Fact]
    public void MantykeNeedsARemoraidInTheParty()
    {
        EvolutionStep step = Assert.Single(PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, 458));

        Assert.Equal("Mantine", step.IntoSpeciesName);
        Assert.Contains("Remoraid", step.Requirement, StringComparison.Ordinal);
    }

    /// <summary>
    /// Magnezone needs a magnetic field that exists only in Sinnoh. A player in Johto must
    /// be told it does not happen there, not promised it.
    /// </summary>
    [Fact]
    public void MagnetonCannotEvolveInJohto()
    {
        EvolutionStep step = Assert.Single(PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, 82));

        Assert.Equal("Magnezone", step.IntoSpeciesName);
        Assert.Contains("not in HeartGold or SoulSilver", step.Requirement, StringComparison.Ordinal);
        Assert.Null(step.Level);
    }

    [Fact]
    public void NoGen4StepFallsBackToTheUnknownCondition()
    {
        for (int species = 1; species <= 493; species++)
        {
            foreach (EvolutionStep step in PKHeXGen4EvolutionSource.Instance.FindEvolutions(SoulSilver, species))
            {
                Assert.DoesNotContain("this app does not know", step.Requirement, StringComparison.Ordinal);
                Assert.DoesNotContain("item #", step.Requirement, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DragoniteHasItsGen4BaseStats()
    {
        StatBlock? stats = PKHeXSpeciesBaseStatsSource.Gen4.FindBaseStats(SoulSilver, 149);

        Assert.Equal(new StatBlock(91, 134, 95, 100, 100, 80), stats);
    }

    [Fact]
    public void TheCompositeAnswersForSoulSilver()
    {
        Assert.True(PKHeXSources.Learnsets.Supports(SoulSilver));
        Assert.True(PKHeXSources.Evolutions.Supports(SoulSilver));
        Assert.True(PKHeXSources.BaseStats.Supports(SoulSilver));
    }
}
