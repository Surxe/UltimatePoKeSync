using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using Xunit;

namespace UltimatePoKeSync.Analysis.Tests;

public sealed class TeamAnalyzerTests
{
    private readonly TeamAnalyzer _analyzer = new();

    [Fact]
    public void ProducesOneDefensiveAndOffensiveEntryPerGen3Type()
    {
        TeamAnalysis analysis = _analyzer.Analyze(AnalysisTestData.Party());

        Assert.Equal(17, analysis.DefensiveCoverage.Count);
        Assert.Equal(17, analysis.OffensiveCoverage.Count);
        Assert.Equal(PokemonType.Normal, analysis.DefensiveCoverage[0].AttackingType);
        Assert.Equal(PokemonType.Dark, analysis.OffensiveCoverage[^1].DefendingType);
        Assert.DoesNotContain(
            analysis.DefensiveCoverage,
            entry => entry.AttackingType == PokemonType.Fairy);
    }

    [Fact]
    public void DefensiveCoverageCombinesDualTypesAndAbilitiesPerMember()
    {
        PokemonSnapshot grassSteel = AnalysisTestData.Member(
            slot: 0,
            primaryType: PokemonType.Grass,
            secondaryType: PokemonType.Steel);
        PokemonSnapshot thickFatWater = AnalysisTestData.Member(
            slot: 1,
            primaryType: PokemonType.Water,
            abilityId: Gen3AbilityIds.ThickFat);

        TeamAnalysis analysis = _analyzer.Analyze(
            AnalysisTestData.Party(grassSteel, thickFatWater));
        DefensiveTypeCoverage fire = FindDefense(analysis, PokemonType.Fire);

        Assert.Equal(4, fire.Matchups[0].Multiplier);
        Assert.Equal(0.25, fire.Matchups[1].Multiplier);
        Assert.Equal(1, fire.WeakCount);
        Assert.Equal(1, fire.ResistantCount);
        Assert.True(fire.HasDefensiveAnswer);
        Assert.False(fire.IsGap);
    }

    [Fact]
    public void DefensiveGapRequiresAWeaknessAndNoResistantSwitchIn()
    {
        TeamAnalysis analysis = _analyzer.Analyze(
            AnalysisTestData.Party(
                AnalysisTestData.Member(primaryType: PokemonType.Water)));

        Assert.Contains(PokemonType.Electric, analysis.DefensiveGaps);
        Assert.Contains(PokemonType.Grass, analysis.DefensiveGaps);
        Assert.DoesNotContain(PokemonType.Normal, analysis.DefensiveGaps);

        DefensiveTypeCoverage electric = FindDefense(analysis, PokemonType.Electric);
        Assert.Equal(1, electric.WeakCount);
        Assert.Equal(0, electric.ResistantCount);
        Assert.Equal(0, electric.ImmuneCount);
        Assert.True(electric.IsGap);
    }

    [Fact]
    public void AbilityImmunityCountsAsADefensiveAnswer()
    {
        TeamAnalysis analysis = _analyzer.Analyze(
            AnalysisTestData.Party(
                AnalysisTestData.Member(
                    slot: 0,
                    primaryType: PokemonType.Fire,
                    secondaryType: PokemonType.Rock),
                AnalysisTestData.Member(
                    slot: 1,
                    primaryType: PokemonType.Normal,
                    abilityId: Gen3AbilityIds.WaterAbsorb)));

        DefensiveTypeCoverage water = FindDefense(analysis, PokemonType.Water);

        Assert.Equal(1, water.WeakCount);
        Assert.Equal(1, water.ImmuneCount);
        Assert.True(water.HasDefensiveAnswer);
        Assert.False(water.IsGap);
    }

    [Fact]
    public void OffensiveCoverageUsesKnownDamagingMovesIncludingNonStabMoves()
    {
        PokemonSnapshot normalMember = AnalysisTestData.Member(
            moves:
            [
                AnalysisTestData.Move(57, "Surf", PokemonType.Water),
                AnalysisTestData.Move(58, "Ice Beam", PokemonType.Ice),
            ]);

        TeamAnalysis analysis = _analyzer.Analyze(AnalysisTestData.Party(normalMember));
        OffensiveTypeCoverage rock = FindOffense(analysis, PokemonType.Rock);
        OffensiveTypeCoverage dragon = FindOffense(analysis, PokemonType.Dragon);

        OffensiveAnswer surf = Assert.Single(rock.Answers);
        Assert.Equal("Surf", surf.Move.Name);
        Assert.Equal(MoveCategory.Special, surf.Category);
        Assert.Equal(2, surf.Multiplier);

        OffensiveAnswer iceBeam = Assert.Single(dragon.Answers);
        Assert.Equal("Ice Beam", iceBeam.Move.Name);
        Assert.Equal(MoveCategory.Special, iceBeam.Category);
    }

    [Fact]
    public void StatusMovesDoNotCreateFalseOffensiveCoverage()
    {
        PokemonSnapshot member = AnalysisTestData.Member(
            moves: [AnalysisTestData.Move(28, "Sand Attack", PokemonType.Ground)]);

        TeamAnalysis analysis = _analyzer.Analyze(AnalysisTestData.Party(member));

        Assert.False(FindOffense(analysis, PokemonType.Fire).IsCovered);
        Assert.Contains(PokemonType.Fire, analysis.OffensiveGaps);
    }

    [Fact]
    public void FixedDamageMovesDoNotCreateFalseOffensiveCoverage()
    {
        PokemonSnapshot member = AnalysisTestData.Member(
            moves: [AnalysisTestData.Move(69, "Seismic Toss", PokemonType.Fighting)]);

        TeamAnalysis analysis = _analyzer.Analyze(AnalysisTestData.Party(member));

        Assert.False(FindOffense(analysis, PokemonType.Normal).IsCovered);
        Assert.Contains(PokemonType.Normal, analysis.OffensiveGaps);
    }

    [Fact]
    public void EmptyPartyHasNoDefensiveWeaknessButNoOffensiveCoverage()
    {
        TeamAnalysis analysis = _analyzer.Analyze(AnalysisTestData.Party());

        Assert.Empty(analysis.DefensiveGaps);
        Assert.Equal(17, analysis.OffensiveGaps.Count);
    }

    [Fact]
    public void UnsupportedGenerationFailsInsteadOfUsingTheWrongRules()
    {
        var game = new GameIdentity("TEST", "TEST", 0, PokemonGeneration.Gen6);
        var party = new PartySnapshot(game, [], DateTimeOffset.UnixEpoch, 1, []);

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => _analyzer.Analyze(party));

        Assert.Contains("Gen6", error.Message, StringComparison.Ordinal);
    }

    private static DefensiveTypeCoverage FindDefense(
        TeamAnalysis analysis,
        PokemonType attackingType) => Assert.Single(
            analysis.DefensiveCoverage,
            entry => entry.AttackingType == attackingType);

    private static OffensiveTypeCoverage FindOffense(
        TeamAnalysis analysis,
        PokemonType defendingType) => Assert.Single(
            analysis.OffensiveCoverage,
            entry => entry.DefendingType == defendingType);
}
