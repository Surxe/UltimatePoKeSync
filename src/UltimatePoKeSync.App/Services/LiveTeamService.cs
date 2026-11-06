using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.Parsing;
using UltimatePoKeSync.Providers.MelonDs;
using UltimatePoKeSync.Providers.MGba;
using UltimatePoKeSync.Session;

namespace UltimatePoKeSync.App.Services;

/// <summary>
/// Owns the live pipeline for the UI: providers, parser and change tracking.
/// </summary>
/// <remarks>
/// <para>
/// Both emulators are watched at once and whichever answers wins. Asking someone to pick
/// mGBA or melonDS from a menu would be asking them a question they have already answered by
/// opening one of them, and the promise this app was built on is that you open it and it
/// works. See D-042.
/// </para>
/// <para>
/// Events are raised on a background thread. The view model marshals them, so this class
/// stays free of any dependency on a UI framework.
/// </para>
/// </remarks>
public sealed class LiveTeamService : ILiveTeamSource
{
    private readonly List<Watched> _watched = [];
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();

    private readonly Dictionary<Watched, EmulatorConnectionState> _states = [];

    private Watched? _active;
    private EmulatorConnectionState _reported = EmulatorConnectionState.Idle;
    private Task? _loops;

    /// <summary>
    /// Names the melonDS GDB stub port to use instead of the default ARM7 one. For a
    /// second melonDS running beside the player's own, as a contributor measuring a cartridge
    /// does, so the app reads the instance it was meant to. See D-066.
    /// </summary>
    public const string MelonDsPortVariable = "UPKS_MELONDS_PORT";

    public LiveTeamService()
        : this(
            new MGbaProviderOptions(),
            ParseMelonDsPort(Environment.GetEnvironmentVariable(MelonDsPortVariable)))
    {
    }

    public LiveTeamService(MGbaProviderOptions options, int? melonDsPort = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;

        var mgba = new MGbaProvider(options);
        Add(mgba, mgba, "mGBA");

        var melon = melonDsPort is int port
            ? new MelonDsProvider(port: port)
            : new MelonDsProvider();
        Add(melon, melon, "melonDS");
    }

    /// <summary>
    /// A port from the environment, or null to keep the defaults. Anything that is not a
    /// usable TCP port is ignored rather than fatal: a typo should cost the override, not
    /// the app.
    /// </summary>
    internal static int? ParseMelonDsPort(string? value) =>
        int.TryParse(value, out int port) && port is > 0 and <= 65535 ? port : null;

    public event EventHandler<EmulatorConnectionState>? StateChanged;

    public event EventHandler<PartySnapshot>? PartyChanged;

    public MGbaProviderOptions Options { get; }

    public int Port => Options.Port;

    /// <summary>
    /// The reader belonging to whichever emulator last sent a party, so a sprite request
    /// goes to the machine the team came from rather than to the one that happens to be
    /// listed first. Null until something has arrived. See D-033.
    /// </summary>
    public IEmulatorMemoryReader? MemoryReader
    {
        get
        {
            lock (_gate)
            {
                return _active?.Reader;
            }
        }
    }

    /// <summary>The emulator currently feeding the window, for the UI to name.</summary>
    public string? ActiveEmulator
    {
        get
        {
            lock (_gate)
            {
                return _active?.Name;
            }
        }
    }

    public void Start() =>
        _loops ??= Task.WhenAll([.. _watched.Select(RunAsync)]);

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();

        if (_loops is not null)
        {
            try
            {
                await _loops;
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        foreach (Watched watched in _watched)
        {
            await watched.Provider.DisposeAsync();
        }

        _cancellation.Dispose();
    }

    private void Add(IEmulatorProvider provider, IEmulatorMemoryReader reader, string name)
    {
        var watched = new Watched(
            provider,
            reader,
            name,
            new PartyTracker(provider, PartyParserResolver.CreateDefault()));

        provider.StateChanged += (_, state) => Report(watched, state);

        _watched.Add(watched);
    }

    /// <summary>
    /// One connection state out of two emulators, and the best one wins.
    /// </summary>
    /// <remarks>
    /// The emulator that is not open never stops trying, so it announces that it is
    /// reconnecting for ever. Passing every state straight through means that chatter lands
    /// on top of the one that is working, and the window says the connection was lost while
    /// a party is arriving on it every second. Reported once when the summary changes, so a
    /// dead provider's retries are silent while a live one is streaming.
    /// </remarks>
    private void Report(Watched watched, EmulatorConnectionState state)
    {
        EmulatorConnectionState summary;

        lock (_gate)
        {
            _states[watched] = state;
            summary = _states.Values.MinBy(Rank);

            if (summary == _reported)
            {
                return;
            }

            _reported = summary;
        }

        StateChanged?.Invoke(this, summary);
    }

    /// <summary>
    /// How good a state is, best first. Internal so a test can check this ordering rather
    /// than a copy of it. The enum is declared in lifecycle order rather than
    /// in this one, so ordering by its value would rank "never started" above "streaming".
    /// </summary>
    internal static int Rank(EmulatorConnectionState state) => state switch
    {
        EmulatorConnectionState.Streaming => 0,
        EmulatorConnectionState.ConnectedNoGame => 1,
        EmulatorConnectionState.Connecting => 2,
        EmulatorConnectionState.Reconnecting => 3,
        EmulatorConnectionState.Idle => 4,
        _ => 5,
    };

    private async Task RunAsync(Watched watched)
    {
        try
        {
            await foreach (PartySnapshot party in watched.Tracker.TrackAsync(_cancellation.Token))
            {
                lock (_gate)
                {
                    // The most recent party wins, so swapping emulators mid-session works
                    // without anybody having to say so.
                    _active = watched;
                }

                PartyChanged?.Invoke(this, party);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }

    private sealed record Watched(
        IEmulatorProvider Provider,
        IEmulatorMemoryReader Reader,
        string Name,
        PartyTracker Tracker);
}
