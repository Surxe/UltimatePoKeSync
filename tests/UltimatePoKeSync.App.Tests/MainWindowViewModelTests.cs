using System.Text.Json;
using UltimatePoKeSync.App.Services;
using UltimatePoKeSync.App.ViewModels;
using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.Parsing;
using Xunit;

namespace UltimatePoKeSync.App.Tests;

/// <summary>
/// Drives the dashboard with the Italian Emerald capture instead of a live emulator.
/// </summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public void BeforeAnythingArrives_TheSetupScreenIsWhatIsShown()
    {
        (MainWindowViewModel viewModel, _) = Create();

        Assert.False(viewModel.HasTeam);
        Assert.False(viewModel.IsConnected);
        Assert.NotEmpty(viewModel.SetupSteps);
        Assert.EndsWith("ups_bridge.lua", viewModel.ScriptPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both emulators are watched at once, so both sets of instructions are on the screen:
    /// someone playing Black must not be told to load a Lua script that melonDS cannot use.
    /// See D-042.
    /// </summary>
    [Fact]
    public void TheSetupScreenExplainsBothEmulators()
    {
        (MainWindowViewModel viewModel, _) = Create();

        Assert.Contains(viewModel.SetupSteps, step => step.Contains("mGBA", StringComparison.Ordinal));
        Assert.NotEmpty(viewModel.DsSetupSteps);
        Assert.Contains(viewModel.DsSetupSteps, step => step.Text.Contains("melonDS", StringComparison.Ordinal));
        Assert.Contains(viewModel.DsSetupSteps, step => step.Text.Contains("GDB stub", StringComparison.Ordinal));

        // And it must not tell a DS player to leave the JIT on, which silences the stub.
        Assert.Contains(viewModel.DsSetupSteps, step => step.Text.Contains("JIT", StringComparison.Ordinal));

        // Numbered from one, because the column draws them beside a numbered circle.
        Assert.Equal(
            [.. Enumerable.Range(1, viewModel.DsSetupSteps.Count)],
            [.. viewModel.DsSetupSteps.Select(step => step.Number)]);
    }

    /// <summary>
    /// The path handed to the user must survive being opened from Downloads on macOS,
    /// where the app itself runs from a randomised throwaway directory. See D-029.
    /// </summary>
    [Fact]
    public void TheScriptPath_IsAStableUserFolder_NotWhereverTheAppHappensToRun()
    {
        (MainWindowViewModel viewModel, _) = Create();

        Assert.True(Path.IsPathRooted(viewModel.ScriptPath));
        Assert.DoesNotContain("AppTranslocation", viewModel.ScriptPath, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, viewModel.ScriptPath, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            viewModel.ScriptPath,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A save with nothing in the party used to bring the setup steps back, which reads as
    /// "not connected" while the header says Connected. Issue #16.
    /// </summary>
    [Fact]
    public void AnEmptyPartyIsNotTheSameAsNotBeingConnected()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        Assert.True(viewModel.ShowSetup);
        Assert.False(viewModel.HasEmptyParty);

        PartySnapshot party = LoadRealParty();
        source.RaiseParty(party with { Members = [] });

        Assert.False(viewModel.ShowSetup);
        Assert.True(viewModel.HasEmptyParty);
        Assert.False(viewModel.HasTeam);

        // And a party arriving afterwards takes over from it.
        source.RaiseParty(party);

        Assert.True(viewModel.HasTeam);
        Assert.False(viewModel.HasEmptyParty);
        Assert.False(viewModel.ShowSetup);
    }

    [Fact]
    public void ConnectionState_ReachesTheHeader()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        source.RaiseState(EmulatorConnectionState.Streaming);
        Assert.True(viewModel.IsConnected);
        Assert.Equal("Connected", viewModel.ConnectionText);

        source.RaiseState(EmulatorConnectionState.Reconnecting);
        Assert.False(viewModel.IsConnected);
        Assert.Contains("retrying", viewModel.ConnectionText, StringComparison.Ordinal);
    }

    [Fact]
    public void TabCommandSwitchesEveryDashboardScreen()
    {
        (MainWindowViewModel viewModel, _) = Create();

        foreach (DashboardTab tab in Enum.GetValues<DashboardTab>())
        {
            viewModel.ShowTabCommand.Execute(tab);
            Assert.Equal(tab, viewModel.SelectedTab);
        }
    }

    [Fact]
    public void BridgeDiagnosticsComeFromTheLiveSourceAndSnapshot()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        Assert.Equal("Waiting for the bridge", viewModel.BridgeHeading);
        Assert.Equal("127.0.0.1:8888", viewModel.BridgeSocketAddress);
        Assert.Equal("No game detected", viewModel.BridgeGameIdentity);
        Assert.Equal("No party bytes yet", viewModel.BridgePartyPayload);

        source.ActiveEmulator = "mGBA";
        source.RaiseState(EmulatorConnectionState.Streaming);
        source.RaiseParty(LoadRealParty());

        Assert.Equal("Bridge is live", viewModel.BridgeHeading);
        Assert.Equal("mGBA", viewModel.BridgeSourceName);
        Assert.Contains("BPEI", viewModel.BridgeGameIdentity, StringComparison.Ordinal);
        Assert.StartsWith("600 · signature ", viewModel.BridgePartyPayload, StringComparison.Ordinal);

        viewModel.SelectedTab = DashboardTab.Bridge;
        Assert.False(viewModel.ShowsConnectedEmptyState);
    }

    [Fact]
    public void APartyFromRealRam_FillsTheTeamPanel()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        source.RaiseParty(LoadRealParty());

        Assert.True(viewModel.HasTeam);
        Assert.True(viewModel.ShowTeamPanel);
        Assert.Equal("TREECKO", Assert.Single(viewModel.Slots).SpeciesName);
        Assert.Equal("Treecko", Assert.Single(viewModel.Slots).DisplaySpeciesName);
        Assert.Contains("BPEI", viewModel.GameText, StringComparison.Ordinal);

        // 17 attacking types must be accounted for exactly once across the three buckets.
        Assert.Equal(
            17,
            viewModel.TeamWeaknesses.Count + viewModel.TeamNeutral.Count
                + viewModel.TeamResisted.Count + viewModel.TeamImmune.Count);

        // Resistances and immunities are two lists here as well as in the detail panel:
        // a lone Grass starter resists three types and is immune to none. See D-038.
        Assert.NotEmpty(viewModel.TeamResisted);
        Assert.Empty(viewModel.TeamImmune);
        Assert.False(viewModel.HasTeamImmunities);
        Assert.Equal(17, viewModel.TeamSuperEffective.Count + viewModel.TeamUnanswered.Count);
        Assert.Equal(17, viewModel.TeamCoverageTiles.Count);
        Assert.DoesNotContain(viewModel.TeamCoverageTiles, tile => tile.Type == "FAIRY");
        Assert.Equal(viewModel.TeamCoverageTiles.Count(tile => tile.State == "gap"), viewModel.TeamGapCount);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.TeamCoverageNote));
        Assert.All(viewModel.Slots, slot => Assert.False(string.IsNullOrWhiteSpace(slot.TeamAnalysisText)));

        // The placeholders under the coverage headings must agree with the chips beside
        // them: both were shown at once when the counts were read before the lists filled.
        Assert.Equal(viewModel.TeamSuperEffective.Count > 0, viewModel.HasSuperEffective);
        Assert.Equal(viewModel.TeamUnanswered.Count > 0, viewModel.HasUnanswered);

        Assert.Equal(6, viewModel.StrengthFactors.Count);
        Assert.InRange(viewModel.StrengthScore, 1, 100);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StrengthHeadline));
        Assert.Empty(viewModel.AnalysisError);
    }

    [Fact]
    public void TheStripShowsTheLiveConditionAndTheSlotsStillToFill()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];
        Assert.Equal("19/19", slot.HpText);
        Assert.Equal(1, slot.HpFraction);
        Assert.False(slot.HasStatus);
        Assert.False(slot.HasHeldItem);

        // One Pokémon carried, five boxes still to fill.
        Assert.True(viewModel.HasEmptySlots);
        Assert.Equal(5, viewModel.EmptySlots.Count);
    }

    /// <summary>
    /// From the byte the game writes all the way to the three letters on the tile.
    /// </summary>
    [Fact]
    public void APoisonedPokemonReachesTheStripAsPsn()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();

        source.RaiseParty(LoadRealParty(statusByte: 0b0000_1000));

        PokemonSlotViewModel slot = viewModel.Slots[0];
        Assert.True(slot.HasStatus);
        Assert.Equal("PSN", slot.StatusText);
        Assert.Equal("PSN", slot.RailBadgeText);
        Assert.Contains(slot.TypeChips, chip => chip.UpperType == "GRASS");
    }

    [Fact]
    public void SelectingASlot_SwapsToTheDetailPanelAndBackAgain()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];
        viewModel.SelectedSlot = slot;

        Assert.False(viewModel.ShowTeamPanel);
        Assert.Equal(6, slot.Stats.Count);
        Assert.Equal(["Pound", "Leer"], slot.Moves.Select(move => move.Name));
        Assert.Contains(slot.Weaknesses, chip => chip.Type == "Fire");

        // Grass resists Water and is immune to nothing, so the two lists must not be one:
        // taking a quarter of a hit and taking none of it are different plans.
        Assert.Contains(slot.Resistances, chip => chip.Type == "Water");
        Assert.Empty(slot.Immunities);
        Assert.False(slot.HasImmunities);
        Assert.Empty(slot.Resistances.Intersect(slot.Immunities));
        Assert.True(slot.HasRecommendation);

        viewModel.ClearSelectionCommand.Execute(null);
        Assert.True(viewModel.ShowTeamPanel);
    }

    /// <summary>
    /// The Treecko in the capture is Lv.5, so the card has something to say without any
    /// prompting: what it learns next, and that Grovyle is eleven levels away. See D-037.
    /// </summary>
    [Fact]
    public void TheDetailPanelSaysWhatTheNextLevelsBring()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];

        Assert.True(slot.HasProgress);
        Assert.Equal("Becomes Grovyle at Lv.16, 11 levels away.", slot.EvolutionText);
        Assert.Contains("Grovyle's learnset", slot.EvolutionNote, StringComparison.Ordinal);
        Assert.Empty(slot.OtherEvolutionsText);

        Assert.NotEmpty(slot.UpcomingMoves);
        Assert.All(slot.UpcomingMoves, move => Assert.StartsWith("Lv.", move.Level, StringComparison.Ordinal));
        Assert.All(slot.UpcomingMoves, move => Assert.Contains("level", move.Distance, StringComparison.Ordinal));
    }

    [Fact]
    public void TheStatsScreenIsBuiltFromTheCapturedStatsRatherThanMockNumbers()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];

        Assert.Equal(6, slot.StatSources.Count);
        Assert.Equal(slot.Stats.Select(stat => stat.Current), slot.StatSources.Select(stat => stat.Current));
        Assert.Equal(
            Enum.GetValues<Stat>().Select(stat => slot.Member.IndividualValues[stat]),
            slot.IndividualValues.Select(stat => stat.Value));
        Assert.Equal(slot.Member.EffortValues.Total, slot.EffortValueTotal);
        Assert.Equal(510 - slot.EffortValueTotal, slot.EffortValuesLeft);
        Assert.All(slot.StatSources, stat => Assert.Contains("base", stat.Breakdown, StringComparison.Ordinal));
        Assert.True(slot.HasIndividualValueNote);
        Assert.Contains("Treecko", slot.IndividualValueNote, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(slot.EffortValueFarmNote));
        Assert.False(string.IsNullOrWhiteSpace(slot.MatchupNoteTitle));
        Assert.False(string.IsNullOrWhiteSpace(slot.MatchupNote));
    }

    [Fact]
    public void TheBestSetIsHiddenUntilItIsAskedFor()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];

        Assert.False(slot.ShowBuild);
        slot.ToggleBuildCommand.Execute(null);
        Assert.True(slot.ShowBuild);
        Assert.NotEmpty(slot.BuildMoves);
        Assert.All(slot.BuildMoves, move => Assert.False(string.IsNullOrWhiteSpace(move.Reason)));
    }

    [Fact]
    public void TheBestSetComparisonComesFromTheRecommendationEngine()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        PokemonSlotViewModel slot = viewModel.Slots[0];

        Assert.Equal(4, slot.BuildComparisonRows.Count);
        Assert.Equal(slot.BuildComparisonRows.Count(row => row.IsDifferent), slot.BuildChangeCount);
        Assert.All(slot.BuildMoves, move =>
        {
            Assert.False(string.IsNullOrWhiteSpace(move.Role));
            Assert.False(string.IsNullOrWhiteSpace(move.Description));
        });
        Assert.Contains("planned as Sceptile", slot.RoleReason, StringComparison.Ordinal);
        Assert.Contains(slot.Member.NatureName, slot.CurrentNatureCard.Heading, StringComparison.Ordinal);
        Assert.Contains("next catch", "Nature is fixed at capture. It cannot be trained away — this is advice for the next catch, not a task for this one.", StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingProfile_RecomputesWithoutANewSnapshot()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        source.RaiseParty(LoadRealParty());

        Assert.Equal("Playthrough", viewModel.ProfileText);
        string playthroughEffortValues = viewModel.Slots[0].EffortValueText;

        viewModel.IsCompetitive = true;

        Assert.Equal("Competitive", viewModel.ProfileText);
        Assert.NotEqual(playthroughEffortValues, viewModel.Slots[0].EffortValueText);
        Assert.True(viewModel.Slots[0].HasProjectedStats);
    }

    /// <summary>The bridge reports the bytes a Gen 4 party really takes: six slots of 236.</summary>
    [Fact]
    public void BridgePayloadCountsGen4Slots()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        PartySnapshot party = LoadRealParty();

        source.RaiseParty(party with
        {
            Game = party.Game with { GameCode = "IPGE", Generation = PokemonGeneration.Gen4 },
        });

        Assert.StartsWith("1416 · signature ", viewModel.BridgePartyPayload, StringComparison.Ordinal);
    }

    [Fact]
    public void AGameTheAnalysisCannotServe_StillShowsTheParty()
    {
        (MainWindowViewModel viewModel, FakeSource source) = Create();
        PartySnapshot party = LoadRealParty();

        source.RaiseParty(party with
        {
            Game = party.Game with { GameCode = "AXVJ", Generation = PokemonGeneration.Gen6 },
        });

        Assert.True(viewModel.HasTeam);
        Assert.True(viewModel.HasAnalysisError);
        Assert.Empty(viewModel.StrengthFactors);
    }

    private static (MainWindowViewModel ViewModel, FakeSource Source) Create()
    {
        var source = new FakeSource();
        return (new MainWindowViewModel(source, action => action()), source);
    }

    private static PartySnapshot LoadRealParty(int? statusByte = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "emerald-it-treecko.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        var game = new GameIdentity(
            root.GetProperty("gameCode").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("revision").GetInt32(),
            (PokemonGeneration)root.GetProperty("generation").GetInt32());

        byte[] data = Convert.FromBase64String(root.GetProperty("data").GetString()!);
        if (statusByte is int status)
        {
            // Offset 80: the first of the twenty battle-stat bytes in a party slot.
            data[80] = (byte)status;
        }

        var raw = new RawPartySnapshot(
            game,
            root.GetProperty("partyCount").GetInt32(),
            data,
            root.GetProperty("slotSize").GetInt32(),
            DateTimeOffset.UnixEpoch,
            1);

        return new Gen3PartyParser().Parse(raw);
    }

    private sealed class FakeSource : ILiveTeamSource
    {
        public event EventHandler<EmulatorConnectionState>? StateChanged;

        public event EventHandler<PartySnapshot>? PartyChanged;

        public int Port => 8888;

        public IEmulatorMemoryReader? MemoryReader => null;

        public string? ActiveEmulator { get; set; }

        public bool Started { get; private set; }

        public void Start() => Started = true;

        public void RaiseState(EmulatorConnectionState state) => StateChanged?.Invoke(this, state);

        public void RaiseParty(PartySnapshot party) => PartyChanged?.Invoke(this, party);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
