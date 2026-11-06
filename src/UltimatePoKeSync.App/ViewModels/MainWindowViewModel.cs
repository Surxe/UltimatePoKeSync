using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimatePoKeSync.Analysis;
using UltimatePoKeSync.App.Services;
using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;
using UltimatePoKeSync.GameData.Learnsets;
using UltimatePoKeSync.GameData.Sprites;

namespace UltimatePoKeSync.App.ViewModels;

/// <summary>
/// The dashboard. Holds the live party, the team facts and the current selection.
/// </summary>
/// <remarks>
/// It formats and selects; it never decides. Coverage, roles, strength and builds all come
/// from the analysis layer, so the window shows what the CLI shows. See D-028.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILiveTeamSource _live;
    private readonly Action<Action> _post;
    private readonly TeamAnalyzer _teamAnalyzer = new();
    private readonly TeamStrengthAnalyzer _strengthAnalyzer = new(PKHeXSources.All);
    private readonly PokemonRecommendationEngine _engine =
        PokemonRecommendationEngine.CreateDefault(PKHeXSources.All);

    /// <summary>
    /// What the next few levels bring. Cheap enough to run for every member on every
    /// snapshot: it reads the same tables the engine already loaded. See D-037.
    /// </summary>
    private readonly PokemonProgressAnalyzer _progressAnalyzer = new(
        PKHeXSources.Learnsets,
        PKHeXSources.Evolutions);

    private readonly TeamHintAnalyzer _teamHintAnalyzer = new(PKHeXSources.All);

    private readonly IEncounterCatalog _teamHintCatalog;

    private readonly RomSpriteSource? _sprites;

    /// <summary>The player's own sprite folder, when they have one. See D-045.</summary>
    private SpritePackSource _pack = new();

    private readonly Lazy<HttpClient> _http = new(() => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    });

    /// <summary>Stops the running animations when the window closes.</summary>
    private readonly CancellationTokenSource _animations = new();

    private Task? _bridgeMonitor;

    private Task? _teamHintProgressRead;

    private CancellationTokenSource? _teamHintProgressCancellation;

    private PartySnapshot? _party;

    private bool _isUpdatingTeamHintProgress;

    private string? _teamHintGameCode;

    private int _teamHintProgressReadVersion;

    private bool _isDisposed;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Which of the seven screens is showing. See D-047.</summary>
    [ObservableProperty]
    private DashboardTab _selectedTab = DashboardTab.Pokemon;

    [ObservableProperty]
    private bool _isDownloadingSprites;

    /// <summary>0 to 1 while sprites are arriving, for the bar.</summary>
    [ObservableProperty]
    private double _spriteProgress;

    [ObservableProperty]
    private string _spriteStatus = string.Empty;

    [ObservableProperty]
    private string _connectionText = "Waiting for mGBA…";

    [ObservableProperty]
    private string _gameText = string.Empty;

    [ObservableProperty]
    private PokemonSlotViewModel? _selectedSlot;

    [ObservableProperty]
    private bool _isCompetitive;

    [ObservableProperty]
    private int _strengthScore;

    [ObservableProperty]
    private string _strengthHeadline = string.Empty;

    [ObservableProperty]
    private string _analysisError = string.Empty;

    [ObservableProperty]
    private StoryMilestone? _selectedTeamHintMilestone;

    [ObservableProperty]
    private bool _useAutomaticTeamHintProgress = true;

    [ObservableProperty]
    private bool _teamHintsSupported;

    [ObservableProperty]
    private string _teamHintProgressText = "Waiting for a supported game…";

    [ObservableProperty]
    private string _teamHintStatusText = string.Empty;

    [ObservableProperty]
    private string _teamHintSoonText = string.Empty;

    /// <summary>
    /// Whether a party has ever arrived, empty or not. Without this an empty party looks
    /// exactly like a bridge that is not talking: the setup steps come back and the header
    /// says Connected, and the two contradict each other. See issue #16.
    /// </summary>
    [ObservableProperty]
    private bool _hasReceivedParty;

    /// <summary>The banner's line, empty when there is nothing to say. See D-056.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string _updateText = string.Empty;

    private AvailableUpdate? _update;


    public MainWindowViewModel()
        : this(new LiveTeamService(), action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <param name="post">
    /// How to get back onto the UI thread. Injected so a test can run the pipeline
    /// synchronously without an Avalonia application behind it.
    /// </param>
    public MainWindowViewModel(ILiveTeamSource live, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(post);

        _live = live;
        _post = post;
        _teamHintCatalog = PKHeXSources.Encounters;

        SetupSteps = SetupGuide.Steps(live.Port);
        DsSetupSteps =
        [
            .. SetupGuide.DsSteps().Select((step, index) => new NumberedStep(index + 1, step)),
        ];
        DsRomNote = SetupGuide.DsRomNote;
        ScriptPath = SetupGuide.ScriptPath;
        PlatformName = SetupGuide.PlatformName;
        PortHelp = SetupGuide.PortHelp(live.Port);
        RevealButtonText = SetupGuide.RevealButtonText;
        IsTranslocated = SetupGuide.IsTranslocated;
        TranslocationWarning = SetupGuide.TranslocationWarning;

        _sprites = live.MemoryReader is null ? null : new RomSpriteSource(live.MemoryReader);

        // The profile is a choice about how the app should advise, not about one session,
        // so it outlives the run that made it. See D-038.
        IsCompetitive = AppSettings.Load().CompetitiveProfile;

        _live.StateChanged += (_, state) => _post(() => OnStateChanged(state));
        _live.PartyChanged += (_, party) => _post(() => OnPartyChanged(party));

        CheckForUpdate();
    }

    public bool HasUpdate => UpdateText.Length > 0;

    /// <summary>Which DS cartridges are readable, shown beside the melonDS steps.</summary>
    public string DsRomNote { get; }

    public ObservableCollection<PokemonSlotViewModel> Slots { get; } = [];

    /// <summary>
    /// The slots the party has not filled, rendered as placeholders. Six greyed boxes say
    /// "you are carrying two Pokémon" faster than a number does, and party size is one of
    /// the things the strength score counts.
    /// </summary>
    public ObservableCollection<int> EmptySlots { get; } = [];

    public ObservableCollection<TypeChip> TeamWeaknesses { get; } = [];

    public ObservableCollection<TypeChip> TeamNeutral { get; } = [];

    public ObservableCollection<TypeChip> TeamResisted { get; } = [];

    /// <summary>
    /// Kept apart from the resisted list, as it already is in the per-Pokémon view. Taking a
    /// quarter of a hit and taking none of it are different plans, and until now the two
    /// views of the same fact disagreed with each other. See D-038.
    /// </summary>
    public ObservableCollection<TypeChip> TeamImmune { get; } = [];

    public ObservableCollection<TypeChip> TeamSuperEffective { get; } = [];

    public ObservableCollection<TypeChip> TeamUnanswered { get; } = [];

    public ObservableCollection<CoverageTileRow> TeamCoverageTiles { get; } = [];

    public ObservableCollection<StoryMilestone> TeamHintMilestones { get; } = [];

    public ObservableCollection<TeamHintPlanRow> TeamHintPlans { get; } = [];

    public bool HasTeamHintPlans => TeamHintPlans.Count > 0;

    public bool HasTeamHintSoon => TeamHintSoonText.Length > 0;

    public int TeamGapCount { get; private set; }

    public string TeamGapBadgeText => TeamGapCount == 1 ? "1 GAP" : $"{TeamGapCount} GAPS";

    public string TeamCoverageNote { get; private set; } = string.Empty;

    public ObservableCollection<StrengthRow> StrengthFactors { get; } = [];

    public IReadOnlyList<string> SetupSteps { get; }

    /// <summary>The other emulator's steps, for the DS games.</summary>
    public IReadOnlyList<NumberedStep> DsSetupSteps { get; }

    /// <summary>Which emulator is feeding the window, once one is.</summary>
    public string ConnectedVia => _live.ActiveEmulator ?? string.Empty;

    /// <summary>"N / 6" beside the rail's heading.</summary>
    public string PartyCountText => $"{Slots.Count} / 6";

    /// <summary>Out of what. Held rather than assumed, so a rule change shows up here.</summary>
    public int StrengthMaximum { get; private set; } = 100;

    public string StrengthMaximumText => $"/ {StrengthMaximum}";

    /// <summary>The three factors costing the most, which is what the rail prints.</summary>
    public ObservableCollection<StrengthRow> WeakestRailFactors { get; } = [];

    public GridLength StrengthFilled =>
        new(Math.Clamp(StrengthScore, 0, StrengthMaximum), GridUnitType.Star);

    public GridLength StrengthRemaining =>
        new(Math.Clamp(StrengthMaximum - StrengthScore, 0, StrengthMaximum), GridUnitType.Star);

    public bool IsPokemonTab => SelectedTab == DashboardTab.Pokemon;

    /// <summary>
    /// The screens still waiting to be rebuilt keep showing the previous dashboard, so the
    /// app stays usable between one screen and the next.
    /// </summary>
    public bool ShowsOldPanels => false;

    public bool IsStatsTab => SelectedTab == DashboardTab.Stats;

    public bool IsBuildTab => SelectedTab == DashboardTab.Build;

    public bool IsLearnsetTab => SelectedTab == DashboardTab.Learnset;

    public bool IsTeamTab => SelectedTab == DashboardTab.Team;

    public bool IsTeamHintsTab => SelectedTab == DashboardTab.TeamHints;

    public bool IsBridgeTab => SelectedTab == DashboardTab.Bridge;

    public bool ShowsConnectedEmptyState => IsConnected && !HasTeam && !IsBridgeTab;

    public string BridgeHeading => IsConnected ? "Bridge is live" : "Waiting for the bridge";

    public string BridgeSourceName => ConnectedVia.Length > 0 ? ConnectedVia : "mGBA";

    public string BridgeSocketAddress => $"127.0.0.1:{_live.Port}";

    public string BridgeGameIdentity => _party is null
        ? "No game detected"
        : $"{_party.Game.Title} · {_party.Game.GameCode}";

    public string BridgeLastPacket
    {
        get
        {
            if (_party is null)
            {
                return "No packet yet";
            }

            TimeSpan age = DateTimeOffset.UtcNow - _party.CapturedAt;
            if (age.TotalSeconds < 1)
            {
                return $"{Math.Max(0, (int)age.TotalMilliseconds)} ms ago";
            }

            if (age.TotalMinutes < 1)
            {
                return $"{Math.Max(1, (int)age.TotalSeconds)} s ago";
            }

            return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        }
    }

    public string BridgePartyPayload
    {
        get
        {
            if (_party is null)
            {
                return "No party bytes yet";
            }

            int bytes = _party.Game.Generation switch
            {
                PokemonGeneration.Gen5 => 220 * 6,
                PokemonGeneration.Gen4 => 236 * 6,
                _ => 100 * 6,
            };
            return $"{bytes} · signature {ComputePartySignature(_party)}";
        }
    }

    /// <summary>
    /// The line under the app name: which game, and which emulator it came through. Both
    /// are read from the cartridge rather than configured, so this is also the proof that
    /// the bridge found something real.
    /// </summary>
    public string IdentityLine
    {
        get
        {
            if (_party is null)
            {
                return "NO GAME YET";
            }

            string via = ConnectedVia.Length > 0 ? $" · {ConnectedVia.ToUpperInvariant()}" : string.Empty;
            return $"{_party.Game.Title.ToUpperInvariant()} · {_party.Game.GameCode}{via}";
        }
    }

    /// <summary>
    /// Whether the sprite folder has anything in it. Everything works without it, so this
    /// only decides whether to offer the download. See D-046.
    /// </summary>
    public bool HasSprites => _pack.Exists;

    public bool CanOfferSprites => !HasSprites && !IsDownloadingSprites;

    public string ScriptPath { get; }

    public string PlatformName { get; }

    public string PortHelp { get; }

    public string RevealButtonText { get; }

    /// <summary>macOS is running us from a randomised throwaway copy. See D-029.</summary>
    public bool IsTranslocated { get; }

    public string TranslocationWarning { get; }

    public string ProfileText => IsCompetitive ? "Competitive" : "Playthrough";


    /// <summary>Green once bytes are actually arriving, amber while they are not.</summary>
    public IBrush ConnectionBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(0x4E, 0x9A, 0x3F))
        : new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));

    public bool HasTeam => Slots.Count > 0;

    /// <summary>Connected and read, and there is genuinely nothing in the party.</summary>
    public bool HasEmptyParty => HasReceivedParty && Slots.Count == 0;

    /// <summary>The setup steps belong on screen only until the bridge answers.</summary>
    public bool ShowSetup => !HasReceivedParty;

    public bool HasEmptySlots => EmptySlots.Count > 0;

    /// <summary>
    /// One line instead of five cards. The free slots are worth stating (a party of one is
    /// a party of one), but they were taking as much of the strip as the Pokémon in it.
    /// See D-038.
    /// </summary>
    public string EmptySlotsText => EmptySlots.Count == 1
        ? "1 SLOT FREE"
        : $"{EmptySlots.Count} SLOTS FREE";

    public bool HasSuperEffective => TeamSuperEffective.Count > 0;

    /// <summary>A team with no immunity at all should not be shown an empty heading.</summary>
    public bool HasTeamImmunities => TeamImmune.Count > 0;

    public bool HasUnanswered => TeamUnanswered.Count > 0;

    public bool ShowTeamPanel => SelectedSlot is null;

    /// <summary>
    /// The Pokémon screen always has a subject. Keeping the explicit selection nullable
    /// preserves the existing whole-team state while the first rail card supplies the
    /// default detail view.
    /// </summary>
    public PokemonSlotViewModel? CurrentSlot => SelectedSlot ?? Slots.FirstOrDefault();

    public bool HasAnalysisError => AnalysisError.Length > 0;

    public void Start()
    {
        _live.Start();
        _bridgeMonitor ??= MonitorBridgeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // The animations outlive nothing: a timer still swapping frames into a closed
        // window is a leak with a picture attached.
        _isDisposed = true;
        _teamHintProgressReadVersion++;
        CancelTeamHintProgressRead();
        await _animations.CancelAsync().ConfigureAwait(false);

        if (_bridgeMonitor is not null)
        {
            try
            {
                await _bridgeMonitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The window closed between bridge refreshes.
            }
        }

        if (_teamHintProgressRead is not null)
        {
            try
            {
                await _teamHintProgressRead.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The window closed while the live-save checkpoint was being read.
            }
        }

        _animations.Dispose();
        _teamHintProgressCancellation?.Dispose();

        await _live.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Asks once, in the background, and says nothing unless there is something to say.
    /// </summary>
    /// <remarks>
    /// Nothing waits on this and nothing fails because of it. A build that is not a release,
    /// a machine with no network, a rate limit: all of them leave the banner empty and the
    /// app exactly as it was. See D-056.
    /// </remarks>
    private void CheckForUpdate()
    {
        Version? running = UpdateCheck.Running;
        if (running is null)
        {
            return;
        }

        string? declined = AppSettings.Load().DeclinedUpdate;

        _ = Task.Run(async () =>
        {
            AvailableUpdate? found = await new UpdateCheck(_http.Value)
                .FindAsync(running, declined, _animations.Token)
                .ConfigureAwait(false);

            if (found is null || _animations.Token.IsCancellationRequested)
            {
                return;
            }

            _post(() =>
            {
                _update = found;
                UpdateText = $"Version {found.Version} is out. You have {running}.";
            });
        }, _animations.Token);
    }

    [RelayCommand]
    private void DownloadUpdate()
    {
        if (_update is AvailableUpdate update)
        {
            UpdateCheck.Open(update.Url);
        }
    }

    /// <summary>
    /// Turns the notice down, and remembers it. The old version keeps working; that is the
    /// whole point of offering rather than insisting.
    /// </summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (_update is AvailableUpdate update)
        {
            (AppSettings.Load() with { DeclinedUpdate = update.Version }).Save();
        }

        UpdateText = string.Empty;
        _update = null;
    }

    /// <summary>Returns to the whole-team view. Selection itself is the list box's job.</summary>
    [RelayCommand]
    private void ClearSelection() => SelectedSlot = null;

    /// <summary>Switches screen. The rail and the header do not move. See D-047.</summary>
    [RelayCommand]
    private void ShowTab(DashboardTab tab) => SelectedTab = tab;

    /// <summary>
    /// Picking a card selects that member and shows the Pokémon screen, because that is
    /// what somebody clicking a Pokémon is asking for.
    /// </summary>
    [RelayCommand]
    private void SelectSlot(PokemonSlotViewModel slot)
    {
        SelectedSlot = slot;
        SelectedTab = DashboardTab.Pokemon;
    }

    partial void OnSelectedTabChanged(DashboardTab value)
    {
        foreach (string name in new[]
                 {
                     nameof(IsPokemonTab), nameof(IsStatsTab), nameof(IsBuildTab),
                     nameof(IsLearnsetTab), nameof(IsTeamTab), nameof(IsTeamHintsTab),
                     nameof(IsBridgeTab),
                     nameof(ShowsOldPanels), nameof(ShowsConnectedEmptyState),
                 })
        {
            OnPropertyChanged(name);
        }

        if (value == DashboardTab.TeamHints &&
            _party is not null &&
            TeamHintsSupported &&
            UseAutomaticTeamHintProgress)
        {
            BeginAutomaticTeamHintProgress(_party);
        }
    }

    /// <summary>
    /// Opens the file manager on the script. Typing that path into mGBA's dialog by hand
    /// is the one step most likely to go wrong.
    /// </summary>
    [RelayCommand]
    private static void RevealScript() => SetupGuide.RevealScript();

    /// <summary>
    /// Fetches the sprite folder. The app's only network call, made because somebody pressed
    /// this, and nothing waits on it: without sprites the team shows coloured tiles and
    /// everything else is identical. See D-046.
    /// </summary>
    [RelayCommand]
    private async Task DownloadSprites()
    {
        if (IsDownloadingSprites)
        {
            return;
        }

        IsDownloadingSprites = true;
        SpriteProgress = 0;
        SpriteStatus = "Starting…";
        RaiseSpriteState();

        var progress = new Progress<SpriteDownloader.Progress>(step => _post(() =>
        {
            SpriteProgress = step.Fraction;
            SpriteStatus = $"{step.Done} of {step.Total}";
        }));

        try
        {
            var downloader = new SpriteDownloader(_http.Value);
            SpriteDownloader.Result result = await downloader
                .DownloadAsync(progress, _animations.Token)
                .ConfigureAwait(false);

            _post(() =>
            {
                // A fresh source, because the old one cached the absence of every sprite
                // it was asked for before the folder existed.
                _pack = new SpritePackSource();

                SpriteStatus = result.AnyArrived
                    ? $"{result.Fetched} sprites ready"
                    : "Could not reach the sprite archive. The team still works without it.";

                IsDownloadingSprites = false;
                RaiseSpriteState();

                // Draw the team that is already on screen, rather than waiting for it to
                // change on its own.
                if (_party is not null)
                {
                    _ = LoadSpritesAsync(_party.Game, [.. Slots]);
                }
            });
        }
        catch (OperationCanceledException)
        {
            _post(() =>
            {
                IsDownloadingSprites = false;
                SpriteStatus = string.Empty;
                RaiseSpriteState();
            });
        }
    }

    private void RaiseSpriteState()
    {
        OnPropertyChanged(nameof(HasSprites));
        OnPropertyChanged(nameof(CanOfferSprites));
    }

    partial void OnSelectedSlotChanged(PokemonSlotViewModel? value)
    {
        OnPropertyChanged(nameof(ShowTeamPanel));
        OnPropertyChanged(nameof(CurrentSlot));

        // The rail badge follows the selection, so each card has to be told.
        foreach (PokemonSlotViewModel slot in Slots)
        {
            slot.IsSelected = ReferenceEquals(slot, CurrentSlot);
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        foreach (string name in new[]
                 {
                     nameof(ConnectionBrush), nameof(BridgeHeading),
                     nameof(ShowsConnectedEmptyState),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    [RelayCommand]
    private void ChooseProfile(string? profile) =>
        IsCompetitive = string.Equals(profile, "competitive", StringComparison.OrdinalIgnoreCase);

    partial void OnIsCompetitiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ProfileText));
        if (_party is not null)
        {
            OnPartyChanged(_party);
        }
    }

    partial void OnAnalysisErrorChanged(string value) => OnPropertyChanged(nameof(HasAnalysisError));

    partial void OnTeamHintSoonTextChanged(string value) =>
        OnPropertyChanged(nameof(HasTeamHintSoon));

    partial void OnSelectedTeamHintMilestoneChanged(StoryMilestone? value)
    {
        if (_isUpdatingTeamHintProgress || value is null || _party is null || !TeamHintsSupported)
        {
            return;
        }

        if (!UseAutomaticTeamHintProgress)
        {
            SetManualTeamHintProgress(value);
        }

        RebuildTeamHints(_party, value);
    }

    partial void OnUseAutomaticTeamHintProgressChanged(bool value)
    {
        if (_isUpdatingTeamHintProgress || _party is null || !TeamHintsSupported)
        {
            return;
        }

        if (value)
        {
            BeginAutomaticTeamHintProgress(_party);
            return;
        }

        _teamHintProgressReadVersion++;
        CancelTeamHintProgressRead();
        if (SelectedTeamHintMilestone is StoryMilestone milestone)
        {
            SetManualTeamHintProgress(milestone);
            RebuildTeamHints(_party, milestone);
        }
    }

    partial void OnHasReceivedPartyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSetup));
        OnPropertyChanged(nameof(HasEmptyParty));
    }

    private void OnStateChanged(EmulatorConnectionState state)
    {
        OnPropertyChanged(nameof(ConnectedVia));
        OnPropertyChanged(nameof(BridgeSourceName));
        IsConnected = state == EmulatorConnectionState.Streaming;
        ConnectionText = state switch
        {
            EmulatorConnectionState.Connecting => "Looking for mGBA and melonDS…",
            // The name is only known once a party has arrived, so the plain word has to
            // stand on its own until then rather than trailing a "via " with nothing after it.
            EmulatorConnectionState.Streaming => ConnectedVia.Length > 0
                ? $"Connected via {ConnectedVia}"
                : "Connected",
            EmulatorConnectionState.Reconnecting => "Connection lost, retrying…",
            EmulatorConnectionState.Faulted => "Stopped after an unrecoverable error",
            _ => "Waiting for an emulator…",
        };
    }

    private void OnPartyChanged(PartySnapshot party)
    {
        _party = party;
        HasReceivedParty = true;
        GameText = $"{party.Game.Title} [{party.Game.GameCode}]";
        OnPropertyChanged(nameof(IdentityLine));
        OnPropertyChanged(nameof(BridgeGameIdentity));
        OnPropertyChanged(nameof(BridgeLastPacket));
        OnPropertyChanged(nameof(BridgePartyPayload));

        TeamAnalysis analysis;
        TeamStrength strength;

        try
        {
            analysis = _teamAnalyzer.Analyze(party);
            strength = _strengthAnalyzer.Evaluate(analysis);
        }
        catch (NotSupportedException ex)
        {
            // A game the analysis layer does not cover must not blank the party display.
            AnalysisError = ex.Message;
            ShowPartyWithoutAnalysis(party);
            return;
        }

        // Recommendations are attempted separately, and their absence costs only
        // themselves. A generation with no reference sets, Gen 5 today, used to take the
        // whole analysis down with it: the window showed a team with no matchups and a
        // strength of zero, while the console showed 32 out of 100 for the same party.
        TeamRecommendation? recommendation = null;
        string missing = string.Empty;

        if (!party.IsEmpty)
        {
            try
            {
                recommendation = _engine.Recommend(
                    party,
                    IsCompetitive
                        ? RecommendationProfileKind.Competitive
                        : RecommendationProfileKind.Playthrough);
            }
            catch (NotSupportedException ex)
            {
                missing = $"{ex.Message} Type coverage and team strength are still shown.";
            }
        }

        AnalysisError = missing;

        int selectedIndex = SelectedSlot?.SlotIndex ?? -1;

        Slots.Clear();
        foreach (PokemonSnapshot member in party.Members)
        {
            Slots.Add(new PokemonSlotViewModel(
                member,
                analysis,
                recommendation?.Members.FirstOrDefault(
                    entry => entry.Member.SlotIndex == member.SlotIndex),
                _progressAnalyzer.Analyze(party.Game, member)));
        }

        UpdateEmptySlots(party.Count);
        OnPropertyChanged(nameof(HasTeam));
        OnPropertyChanged(nameof(ShowsConnectedEmptyState));

        // The rail heading counts the cards, and the cards are rebuilt on every snapshot,
        // so the count has to be re-read rather than left at whatever it was at startup.
        OnPropertyChanged(nameof(PartyCountText));
        OnPropertyChanged(nameof(HasEmptyParty));
        UpdateCoverage(analysis, recommendation);
        UpdateStrength(strength);
        SelectedSlot = Slots.FirstOrDefault(slot => slot.SlotIndex == selectedIndex);
        OnPropertyChanged(nameof(CurrentSlot));
        foreach (PokemonSlotViewModel slot in Slots)
        {
            slot.IsSelected = ReferenceEquals(slot, CurrentSlot);
        }

        // PartyTracker emits only meaningful changes, so these tiny progress reads do not
        // poll the emulator continuously. Re-reading here is what lets a badge earned later
        // in the same session unlock its safe checkpoint without restarting the app.
        ConfigureTeamHints(party, refreshAutomaticProgress: true);

        // Sprites arrive later and are worth waiting for, not worth waiting on: the party
        // is on screen already, and each tile swaps its coloured box for the real thing as
        // the bytes come back.
        _ = LoadSpritesAsync(party.Game, [.. Slots]);
    }

    private void ShowPartyWithoutAnalysis(PartySnapshot party)
    {
        var empty = new TeamAnalysis(party, [], []);

        Slots.Clear();
        foreach (PokemonSnapshot member in party.Members)
        {
            Slots.Add(new PokemonSlotViewModel(member, empty, null));
        }

        TeamWeaknesses.Clear();
        TeamNeutral.Clear();
        TeamResisted.Clear();
        TeamImmune.Clear();
        TeamSuperEffective.Clear();
        TeamUnanswered.Clear();
        TeamCoverageTiles.Clear();
        TeamGapCount = 0;
        TeamCoverageNote = string.Empty;
        StrengthFactors.Clear();

        UpdateEmptySlots(party.Count);
        OnPropertyChanged(nameof(HasTeam));
        OnPropertyChanged(nameof(ShowsConnectedEmptyState));
        OnPropertyChanged(nameof(HasEmptyParty));
        OnPropertyChanged(nameof(TeamGapBadgeText));
        OnPropertyChanged(nameof(TeamCoverageNote));
        SelectedSlot = null;
        ConfigureTeamHints(party, refreshAutomaticProgress: true);
    }

    /// <summary>
    /// Connects one party snapshot to the explicit Black encounter timeline. A game switch
    /// rebuilds the selector; ordinary party changes keep the player's manual checkpoint.
    /// </summary>
    private void ConfigureTeamHints(PartySnapshot party, bool refreshAutomaticProgress)
    {
        bool supported = _teamHintCatalog.Supports(party.Game);
        TeamHintsSupported = supported;

        if (!supported)
        {
            _teamHintProgressReadVersion++;
            CancelTeamHintProgressRead();
            _teamHintGameCode = party.Game.GameCode;
            TeamHintMilestones.Clear();
            TeamHintPlans.Clear();
            TeamHintProgressText = "This game's encounter timeline is not mapped yet.";
            TeamHintStatusText = string.Empty;
            TeamHintSoonText = string.Empty;
            OnPropertyChanged(nameof(HasTeamHintPlans));
            return;
        }

        bool gameChanged = !string.Equals(
            _teamHintGameCode,
            party.Game.GameCode,
            StringComparison.Ordinal);
        if (gameChanged || TeamHintMilestones.Count == 0)
        {
            _teamHintGameCode = party.Game.GameCode;
            _isUpdatingTeamHintProgress = true;
            try
            {
                TeamHintMilestones.Clear();
                foreach (StoryMilestone milestone in _teamHintCatalog
                    .FindMilestones(party.Game)
                    .OrderBy(milestone => milestone.Order))
                {
                    TeamHintMilestones.Add(milestone);
                }

                SelectedTeamHintMilestone = TeamHintMilestones.FirstOrDefault();
                UseAutomaticTeamHintProgress = CanDetectTeamHintProgress(party.Game);
            }
            finally
            {
                _isUpdatingTeamHintProgress = false;
            }
            refreshAutomaticProgress = true;
        }

        if (UseAutomaticTeamHintProgress && refreshAutomaticProgress)
        {
            BeginAutomaticTeamHintProgress(party);
        }
        else if (UseAutomaticTeamHintProgress && SelectedTeamHintMilestone is StoryMilestone automatic)
        {
            RebuildTeamHints(party, automatic);
        }
        else if (SelectedTeamHintMilestone is StoryMilestone manual)
        {
            SetManualTeamHintProgress(manual);
            RebuildTeamHints(party, manual);
        }
    }

    private bool CanDetectTeamHintProgress(GameIdentity game)
    {
        if (_live.MemoryReader is not IEmulatorMemoryReader { CanRead: true } memory)
        {
            return false;
        }

        // The reader knows this ROM, but its two offsets have not survived a live check, so
        // it is not allowed to drive which routes are open. See D-053 and the remarks on
        // IrbiStoryProgressReader.OffsetsVerifiedLive.
        return IrbiStoryProgressReader.OffsetsVerifiedLive &&
            new IrbiStoryProgressReader(memory).Supports(game);
    }

    private void BeginAutomaticTeamHintProgress(PartySnapshot party)
    {
        if (_live.MemoryReader is not IEmulatorMemoryReader { CanRead: true } memory)
        {
            FallBackToManualProgress(
                party,
                "Automatic detection needs a live melonDS connection. Choose the latest place you can reach.");
            return;
        }

        var reader = new IrbiStoryProgressReader(memory);
        if (!IrbiStoryProgressReader.OffsetsVerifiedLive || !reader.Supports(party.Game))
        {
            FallBackToManualProgress(
                party,
                "Reading progress from the save is not proven yet, so it is switched off rather than guessed. Choose the latest place you can reach.");
            return;
        }

        Task? previousRead = _teamHintProgressRead;
        CancellationTokenSource? previousCancellation = _teamHintProgressCancellation;
        previousCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_animations.Token);
        _teamHintProgressCancellation = cancellation;
        int version = ++_teamHintProgressReadVersion;
        TeamHintProgressText = "Reading story progress from the live save…";
        TeamHintStatusText = "Future routes stay locked until the reading is confirmed.";
        TeamHintPlans.Clear();
        TeamHintSoonText = string.Empty;
        OnPropertyChanged(nameof(HasTeamHintPlans));
        _teamHintProgressRead = DetectTeamHintProgressAfterAsync(
            previousRead,
            previousCancellation,
            reader,
            party,
            version,
            cancellation.Token);
    }

    private async Task DetectTeamHintProgressAfterAsync(
        Task? previousRead,
        CancellationTokenSource? previousCancellation,
        IStoryProgressReader reader,
        PartySnapshot party,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            if (previousRead is not null)
            {
                await previousRead.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer snapshot superseded the previous read.
        }
        finally
        {
            previousCancellation?.Dispose();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await DetectTeamHintProgressAsync(
            reader,
            party,
            version,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DetectTeamHintProgressAsync(
        IStoryProgressReader reader,
        PartySnapshot party,
        int version,
        CancellationToken cancellationToken)
    {
        DetectedStoryProgress? detected;
        try
        {
            detected = await reader
                .ReadAsync(party.Game, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            detected = null;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _post(() => ApplyDetectedTeamHintProgress(party, version, detected));
        }
    }

    private void ApplyDetectedTeamHintProgress(
        PartySnapshot party,
        int version,
        DetectedStoryProgress? detected)
    {
        if (_isDisposed ||
            version != _teamHintProgressReadVersion ||
            _party is null ||
            !string.Equals(_party.Game.GameCode, party.Game.GameCode, StringComparison.Ordinal))
        {
            return;
        }

        if (detected is null)
        {
            FallBackToManualProgress(
                _party,
                "The live save did not return a safe progress reading. Choose your progress manually.");
            return;
        }

        StoryMilestone? milestone = _teamHintCatalog.Supports(_party.Game)
            ? _teamHintCatalog.FindConservativeMilestone(_party.Game, detected.BadgeCount)
            : ConservativeMilestone(detected.BadgeCount);
        if (milestone is null)
        {
            FallBackToManualProgress(
                _party,
                "No safe checkpoint matched the live save. Choose your progress manually.");
            return;
        }

        _isUpdatingTeamHintProgress = true;
        try
        {
            SelectedTeamHintMilestone = milestone;
        }
        finally
        {
            _isUpdatingTeamHintProgress = false;
        }

        string badges = detected.BadgeCount == 1 ? "1 badge" : $"{detected.BadgeCount} badges";
        TeamHintProgressText = $"Detected {badges}. Safe checkpoint: {milestone.Name}.";
        TeamHintStatusText = detected.MapId is int mapId
            ? $"Live map {mapId} read too; badge gates remain the conservative limit. Turn off detection to choose an exact route."
            : "Badge gates set the conservative limit. Turn off detection to choose an exact route.";
        RebuildTeamHints(_party, milestone);
    }

    private StoryMilestone? ConservativeMilestone(int badgeCount)
    {
        StoryMilestone? guaranteed = TeamHintMilestones
            .Where(milestone => milestone.GuaranteedWhenBadgesAtLeast is int badges &&
                badges <= badgeCount)
            .MaxBy(milestone => milestone.Order);

        // With no badge, Route 1 is the only useful promise that cannot expose a future
        // route. The exact live map is still shown as evidence, not used as an unlock.
        return guaranteed ?? TeamHintMilestones.FirstOrDefault();
    }

    private void FallBackToManualProgress(PartySnapshot party, string reason)
    {
        _teamHintProgressReadVersion++;
        CancelTeamHintProgressRead();
        _isUpdatingTeamHintProgress = true;
        try
        {
            UseAutomaticTeamHintProgress = false;
        }
        finally
        {
            _isUpdatingTeamHintProgress = false;
        }

        TeamHintStatusText = reason;
        if (SelectedTeamHintMilestone is StoryMilestone milestone)
        {
            TeamHintProgressText = $"Manual checkpoint: {milestone.Name}.";
            RebuildTeamHints(party, milestone);
        }
    }

    private void SetManualTeamHintProgress(StoryMilestone milestone)
    {
        TeamHintProgressText = $"Manual checkpoint: {milestone.Name}.";
        TeamHintStatusText = milestone.ReachedWhen;
    }

    private void CancelTeamHintProgressRead()
    {
        _teamHintProgressCancellation?.Cancel();
    }

    private void RebuildTeamHints(PartySnapshot party, StoryMilestone milestone)
    {
        TeamHintPlans.Clear();

        try
        {
            TeamHintAnalysis analysis = _teamHintAnalyzer.Analyze(
                party,
                milestone,
                _teamHintCatalog.FindEncounters(party.Game));
            foreach ((TeamHintPlan plan, int index) in analysis.Plans.Select((plan, index) =>
                (plan, index)))
            {
                TeamHintPlans.Add(ToTeamHintRow(plan, index));
            }
        }
        catch (NotSupportedException ex)
        {
            TeamHintStatusText = ex.Message;
        }

        UpdateTeamHintSoon(party, milestone);
        OnPropertyChanged(nameof(HasTeamHintPlans));
        LoadTeamHintSprites();
    }

    private void UpdateTeamHintSoon(PartySnapshot party, StoryMilestone milestone)
    {
        EncounterCandidate[] future =
        [
            .. _teamHintCatalog.FindEncounters(party.Game)
                .Where(encounter => encounter.EarliestMilestone.Order > milestone.Order)
                .OrderBy(encounter => encounter.EarliestMilestone.Order)
                .ThenBy(encounter => encounter.SpeciesName, StringComparer.Ordinal),
        ];
        if (future.Length == 0)
        {
            TeamHintSoonText = string.Empty;
            return;
        }

        int nextOrder = future[0].EarliestMilestone.Order;
        EncounterCandidate[] next =
        [
            .. future
                .Where(encounter => encounter.EarliestMilestone.Order == nextOrder)
                .DistinctBy(encounter => encounter.SpeciesId)
                .Take(4),
        ];
        string species = string.Join(", ", next.Select(encounter => encounter.SpeciesName));
        TeamHintSoonText = $"At {future[0].EarliestMilestone.Name}: {species}. "
            + "These are previews, not part of the available-now plans.";
    }

    private static TeamHintPlanRow ToTeamHintRow(TeamHintPlan plan, int index)
    {
        string gapText = string.Join(" ", plan.Factors
            .Where(factor => factor.Kind is TeamHintScoreKind.DefensiveCoverage
                or TeamHintScoreKind.OffensiveCoverage)
            .Select(factor => factor.Explanation));

        return new TeamHintPlanRow(
            $"OPTION {(char)('A' + index)}",
            plan.Summary,
            $"{plan.Score:+#;-#;0} PTS",
            gapText + " Only realistic level-up attacks count as coverage.",
            [.. plan.Additions.Select(candidate => ToTeamHintPokemonRow(candidate, plan))]);
    }

    private static TeamHintPokemonRow ToTeamHintPokemonRow(
        TeamHintCandidate candidate,
        TeamHintPlan plan)
    {
        PokemonType[] types = candidate.SecondaryType is PokemonType.None ||
            candidate.SecondaryType == candidate.PrimaryType
            ? [candidate.PrimaryType]
            : [candidate.PrimaryType, candidate.SecondaryType];

        string encounterLevel = candidate.MinimumEncounterLevel == candidate.MaximumEncounterLevel
            ? $"Lv.{candidate.MinimumEncounterLevel}"
            : $"Lv.{candidate.MinimumEncounterLevel}–{candidate.MaximumEncounterLevel}";
        string rate = candidate.EncounterRatePercent is int percent
            ? $" · {percent}%"
            : string.Empty;
        string requirement = string.IsNullOrWhiteSpace(candidate.Requirement)
            ? string.Empty
            : $" · {candidate.Requirement}";
        string limited = candidate.IsLimited
            ? " · limited: check that it is still available in this save"
            : string.Empty;

        var impact = new List<string>();
        if (candidate.CoveredTypes.Count > 0)
        {
            // Potentially, and the word is doing real work. These types come from the
            // damaging moves the species can learn along its near-term level-up line, not
            // from moves it has: a Patrat in the grass knows Tackle. Saying it is
            // super-effective into something would promise a Pokémon that does not exist
            // yet, which is the false certainty D-025 exists to keep out. See D-053.
            impact.Add($"Potentially super-effective into {TypeList(candidate.CoveredTypes)}");
        }

        if (candidate.DefensiveAnswers.Count > 0)
        {
            // No hedge here: a resistance follows from the species' own typing and is true
            // the moment it is caught.
            impact.Add($"Resists {TypeList(candidate.DefensiveAnswers)}");
        }

        string evolution = candidate.ProjectedEvolution is { } final &&
            final.SpeciesId != candidate.SpeciesId
            ? $"Long term: {final.SpeciesName} · {final.Requirements}"
            : string.Empty;
        string replacement = plan.Replacement is null
            ? string.Empty
            : $"REPLACES {plan.Replacement.SpeciesName.ToUpperInvariant()}";

        return new TeamHintPokemonRow(
            candidate.SpeciesId,
            candidate.SpeciesName,
            evolution,
            [.. types.Select(type => TypeChip.For(type, "Type when obtained"))],
            $"{Describe(candidate.Method)} · {candidate.Location}{rate}{requirement}{limited}",
            $"Found at {encounterLevel} · plan toward Lv.{candidate.RecommendedLevel}",
            string.Join(" · ", impact),
            candidate.Reason,
            replacement);
    }

    private static string TypeList(IEnumerable<PokemonType> types) =>
        string.Join(", ", types.Select(type => type.ToString()).Order(StringComparer.Ordinal));

    private static string Describe(EncounterMethod method) => method switch
    {
        EncounterMethod.DarkGrass => "Dark grass",
        EncounterMethod.ShakingGrass => "Shaking grass",
        EncounterMethod.DustCloud => "Dust cloud",
        EncounterMethod.RipplingWater => "Rippling water",
        EncounterMethod.InGameTrade => "In-game trade",
        EncounterMethod.BridgeShadow => "Bridge shadow",
        _ => method.ToString(),
    };

    /// <summary>
    /// Draws the Pokémon a plan suggests, from the player's own sprite folder.
    /// </summary>
    /// <remarks>
    /// Off the UI thread, because reading and decoding a GIF is disk work and this runs
    /// whenever the story point changes. Only the first frame is used: these are small
    /// reference pictures in a list, not the hero sprite, and nine of them animating at once
    /// would pull the eye away from the reading. Cached by the pack, so every rebuild after
    /// the first costs nothing. A missing folder leaves the initials in place. See D-045.
    /// </remarks>
    private void LoadTeamHintSprites()
    {
        TeamHintPokemonRow[] rows =
        [
            .. TeamHintPlans.SelectMany(plan => plan.Pokemon).Where(row => row.Sprite is null),
        ];

        if (rows.Length == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (TeamHintPokemonRow row in rows)
            {
                if (_animations.Token.IsCancellationRequested)
                {
                    return;
                }

                if (_pack.Find(row.SpeciesId, shiny: false) is not AnimatedSprite sprite)
                {
                    continue;
                }

                Bitmap firstFrame = SpriteImage.From(sprite, sprite.Frames[0]);
                _post(() => row.Sprite = firstFrame);
            }
        }, _animations.Token);
    }

    private async Task LoadSpritesAsync(
        GameIdentity game,
        IReadOnlyList<PokemonSlotViewModel> slots)
    {
        // The player's own sprite folder first, when they have one: it covers every
        // generation in one style, and it is the only thing that can draw a DS game at all,
        // whose ROM is not mapped into memory. See D-045.
        foreach (PokemonSlotViewModel slot in slots)
        {
            // Drawing the species inside an egg would give away the one thing the game
            // keeps from the player until it hatches. See D-036.
            if (slot.IsEgg)
            {
                continue;
            }

            if (_pack.Find(slot.Member.SpeciesId, slot.Member.IsShiny) is AnimatedSprite animated)
            {
                _post(() => slot.Play(animated, _post, _animations.Token));
            }

            foreach (EvolutionStageRow stage in slot.EvolutionStages.Where(stage => !stage.IsCurrent))
            {
                if (_pack.Find(stage.SpeciesId, shiny: false) is AnimatedSprite evolutionSprite)
                {
                    Bitmap firstFrame = SpriteImage.From(evolutionSprite, evolutionSprite.Frames[0]);
                    _post(() => stage.Sprite = firstFrame);
                }
            }
        }

        // The cartridge is the fallback, and only a GBA one can be read this way (D-033).
        // On melonDS the channel moves 15 KB a second (D-039), so hunting for tables that
        // are not there would spend half a minute finding nothing.
        if (_sprites is null || game.Generation != PokemonGeneration.Gen3)
        {
            return;
        }

        foreach (PokemonSlotViewModel slot in slots)
        {
            if (slot.IsEgg)
            {
                continue;
            }

            if (!slot.HasSprite)
            {
                DecodedSprite? decoded;
                try
                {
                    decoded = await _sprites.TryGetAsync(game, slot.Member.SpeciesId).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A sprite is decoration. Losing one must never take the party down with it.
                    return;
                }

                if (decoded is not null)
                {
                    _post(() => slot.Sprite = SpriteImage.From(decoded));
                }
            }


            foreach (EvolutionStageRow stage in slot.EvolutionStages
                .Where(stage => !stage.IsCurrent && !stage.HasSprite))
            {
                try
                {
                    DecodedSprite? evolution = await _sprites
                        .TryGetAsync(game, stage.SpeciesId)
                        .ConfigureAwait(false);
                    if (evolution is not null)
                    {
                        _post(() => stage.Sprite = SpriteImage.From(evolution));
                    }
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    private void UpdateEmptySlots(int filled)
    {
        const int fullParty = 6;

        EmptySlots.Clear();
        for (int slot = filled; slot < fullParty; slot++)
        {
            EmptySlots.Add(slot);
        }

        OnPropertyChanged(nameof(HasEmptySlots));
        OnPropertyChanged(nameof(EmptySlotsText));
    }

    /// <summary>
    /// Keeps "last packet" honest. It is an age, so it grows on its own between snapshots
    /// and has to be re-read even when nothing arrives, which is exactly when a player is
    /// looking at it.
    /// </summary>
    private async Task MonitorBridgeAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _animations.Token).ConfigureAwait(false);
            _post(() => OnPropertyChanged(nameof(BridgeLastPacket)));
        }
    }

    /// <summary>
    /// A short stable fingerprint over the same non-volatile facts that drive analysis.
    /// It is diagnostic, not cryptographic: a changed party should simply look changed.
    /// </summary>
    private static string ComputePartySignature(PartySnapshot party)
    {
        uint hash = 2166136261;

        void Mix(uint value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= 16777619;
            }
        }

        foreach (char character in party.Game.GameCode)
        {
            Mix(character);
        }

        foreach (PokemonSnapshot member in party.Members)
        {
            Mix((uint)member.SlotIndex);
            Mix((uint)member.SpeciesId);
            Mix(member.PersonalityValue);
            Mix((uint)member.Level);
            Mix((uint)member.NatureId);
            Mix((uint)member.AbilityId);
            Mix((uint)member.HeldItemId);
            Mix(member.IsEgg ? 1u : 0u);

            foreach (Stat stat in Enum.GetValues<Stat>())
            {
                Mix((uint)member.IndividualValues[stat]);
                Mix((uint)member.EffortValues[stat]);
            }

            foreach (MoveSlot move in member.Moves)
            {
                Mix((uint)move.MoveId);
            }
        }

        return (hash & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A weakness reads differently depending on whether anything can come in for it, and
    /// an immunity is worth naming separately from a resistance even here.
    /// </summary>
    private static string DescribeWeakness(DefensiveTypeCoverage entry)
    {
        if (entry.IsGap)
        {
            return $"{entry.WeakCount} weak, nothing resists";
        }

        string safe = (entry.ImmuneCount, entry.ResistantCount) switch
        {
            (0, int resist) => $"{resist} resist",
            (int immune, 0) => $"{immune} immune",
            (int immune, int resist) => $"{immune} immune, {resist} resist",
        };

        return $"{entry.WeakCount} weak, {safe}";
    }

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private void UpdateCoverage(TeamAnalysis analysis, TeamRecommendation? recommendation)
    {
        TeamWeaknesses.Clear();
        TeamNeutral.Clear();
        TeamResisted.Clear();
        TeamImmune.Clear();
        TeamSuperEffective.Clear();
        TeamUnanswered.Clear();
        TeamCoverageTiles.Clear();

        foreach (DefensiveTypeCoverage entry in analysis.DefensiveCoverage)
        {
            if (entry.WeakCount > 0)
            {
                TeamWeaknesses.Add(TypeChip.For(entry.AttackingType, DescribeWeakness(entry)));
            }
            else if (entry.ImmuneCount > 0)
            {
                // An immunity is the better switch-in, so the type is filed under it even
                // when others merely resist. The detail still counts both.
                string detail = entry.ResistantCount > 0
                    ? $"{entry.ImmuneCount} take nothing, {entry.ResistantCount} resist"
                    : Plural(entry.ImmuneCount, "takes", "take") + " nothing";
                TeamImmune.Add(TypeChip.For(entry.AttackingType, detail));
            }
            else if (entry.ResistantCount > 0)
            {
                TeamResisted.Add(TypeChip.For(
                    entry.AttackingType,
                    Plural(entry.ResistantCount, "resists", "resist")));
            }
            else
            {
                TeamNeutral.Add(TypeChip.For(entry.AttackingType, "no weakness, no resistance"));
            }
        }

        foreach (OffensiveTypeCoverage entry in analysis.OffensiveCoverage)
        {
            if (entry.IsCovered)
            {
                OffensiveAnswer best = entry.Answers.MaxBy(answer => answer.Multiplier)!;
                TeamSuperEffective.Add(TypeChip.For(
                    entry.DefendingType,
                    $"{best.Move.Name} ×{best.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}"));
            }
            else
            {
                TeamUnanswered.Add(TypeChip.For(entry.DefendingType, "no super-effective move"));
            }
        }

        HashSet<PokemonType> defensiveGaps = [.. analysis.DefensiveGaps];
        HashSet<PokemonType> offensiveAnswers =
        [
            .. analysis.OffensiveCoverage
                .Where(entry => entry.IsCovered)
                .Select(entry => entry.DefendingType),
        ];

        foreach (PokemonType type in analysis.OffensiveCoverage
            .Select(entry => entry.DefendingType)
            .Where(type => type is not PokemonType.None and not PokemonType.Fairy))
        {
            if (offensiveAnswers.Contains(type))
            {
                TeamCoverageTiles.Add(new CoverageTileRow(
                    type.ToString().ToUpperInvariant(),
                    "2×",
                    new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x28)),
                    new SolidColorBrush(Color.FromRgb(0xA6, 0xFF, 0xBC))));
            }
            else if (defensiveGaps.Contains(type))
            {
                TeamCoverageTiles.Add(new CoverageTileRow(
                    type.ToString().ToUpperInvariant(),
                    "gap",
                    new SolidColorBrush(Color.FromRgb(0x5A, 0x1F, 0x1A)),
                    new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xAA))));
            }
            else
            {
                TeamCoverageTiles.Add(new CoverageTileRow(
                    type.ToString().ToUpperInvariant(),
                    "ok",
                    new SolidColorBrush(Color.FromRgb(0x2C, 0x27, 0x40)),
                    new SolidColorBrush(Color.FromRgb(0xCF, 0xC7, 0xE6))));
            }
        }

        HashSet<PokemonType> uncoveredGaps =
        [
            .. defensiveGaps.Where(type => !offensiveAnswers.Contains(type)),
        ];
        TeamGapCount = uncoveredGaps.Count;
        BuildSlot? cheapest = recommendation?.Members
            .SelectMany(member => member.Build.Slots)
            .FirstOrDefault(slot => slot.Role is BuildSlotRole.Coverage or BuildSlotRole.TeamSupport);
        string gaps = uncoveredGaps.Count == 0
            ? "No shared defensive gaps."
            : $"Gaps: {string.Join(", ", uncoveredGaps)}.";
        TeamCoverageNote = cheapest is null
            ? gaps
            : $"{gaps} Cheapest fix in the current recommendations: "
                + $"{cheapest.Move.Move.Name} — {cheapest.Reason}";

        OnPropertyChanged(nameof(HasSuperEffective));
        OnPropertyChanged(nameof(HasUnanswered));
        OnPropertyChanged(nameof(HasTeamImmunities));
        OnPropertyChanged(nameof(TeamGapCount));
        OnPropertyChanged(nameof(TeamGapBadgeText));
        OnPropertyChanged(nameof(TeamCoverageNote));
    }

    private void UpdateStrength(TeamStrength strength)
    {
        StrengthScore = strength.Score;
        StrengthMaximum = strength.MaximumScore;
        StrengthFactors.Clear();
        WeakestRailFactors.Clear();

        foreach (TeamStrengthFactor factor in strength.WeakestFactors.Take(3))
        {
            WeakestRailFactors.Add(new StrengthRow(
                Humanise(factor.Kind), factor.Points, factor.MaximumPoints, factor.Explanation));
        }

        foreach (string name in new[]
                 {
                     nameof(StrengthMaximum), nameof(StrengthMaximumText),
                     nameof(StrengthFilled), nameof(StrengthRemaining),
                 })
        {
            OnPropertyChanged(name);
        }


        foreach (TeamStrengthFactor factor in strength.Factors)
        {
            StrengthFactors.Add(new StrengthRow(
                Humanise(factor.Kind),
                factor.Points,
                factor.MaximumPoints,
                factor.Explanation));
        }

        // Name the area and what it costs. Repeating the factor's own explanation here
        // would print the same sentence twice, once above the list and once inside it.
        TeamStrengthFactor? worst = strength.WeakestFactors.FirstOrDefault();
        StrengthHeadline = worst is null
            ? "Nothing is holding this party back."
            : $"{Humanise(worst.Kind)} is costing the most: "
                + $"{worst.MaximumPoints - worst.Points} of {worst.MaximumPoints} points.";
    }

    private static string Humanise(TeamStrengthKind kind) => kind switch
    {
        TeamStrengthKind.PartySize => "Party size",
        TeamStrengthKind.LevelCohesion => "Level cohesion",
        TeamStrengthKind.DefensiveCoverage => "Defensive coverage",
        TeamStrengthKind.OffensiveCoverage => "Offensive coverage",
        TeamStrengthKind.NatureFit => "Nature fit",
        _ => "Effort value fit",
    };
}
