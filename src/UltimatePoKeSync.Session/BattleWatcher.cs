using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Session;

/// <summary>
/// Polls the battle on the field and reports when what it shows changes. See D-076.
/// </summary>
/// <remarks>
/// <para>
/// Pulled on demand through <see cref="IBattleReader"/> rather than pushed with the party,
/// so the provider contract of D-006 stays as thin as it was.
/// </para>
/// <para>
/// A read the decoder refuses, or one the emulator could not answer, changes nothing: the
/// last good battle stays on screen, as <see cref="PartyTracker"/> does for a torn party slot
/// (D-072). The end of a battle is believed only when two reads in a row say so, so a single
/// odd read in the middle of a fight does not blank the tab.
/// </para>
/// </remarks>
public sealed class BattleWatcher
{
    /// <summary>
    /// Twice a second. A turn takes seconds, and each read costs the emulator a frame (D-065):
    /// one read a poll outside a battle, four inside one.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private const int ReadsToBelieveTheEnd = 2;

    private readonly IBattleReader _reader;
    private readonly Func<RawBattleSnapshot, BattleSnapshot?> _decode;
    private readonly TimeSpan _interval;

    private int _endReads;

    public BattleWatcher(
        IBattleReader reader,
        Func<RawBattleSnapshot, BattleSnapshot?> decode,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(decode);

        _reader = reader;
        _decode = decode;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Raised, on the polling thread, whenever the battle shown should change.</summary>
    public event EventHandler<BattleSnapshot>? BattleChanged;

    public BattleSnapshot Current { get; private set; } = BattleSnapshot.None;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);

        do
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One read. Public so tests can drive the watcher without a clock.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        RawBattleSnapshot? raw = await _reader.ReadBattleAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null || _decode(raw) is not BattleSnapshot battle)
        {
            return;
        }

        if (!battle.IsRunning && Current.IsRunning && ++_endReads < ReadsToBelieveTheEnd)
        {
            return;
        }

        _endReads = 0;

        if (SameOnScreen(battle, Current))
        {
            return;
        }

        Current = battle;
        BattleChanged?.Invoke(this, battle);
    }

    /// <summary>Equal apart from when it was read, which changes on every poll.</summary>
    private static bool SameOnScreen(BattleSnapshot left, BattleSnapshot right) =>
        left with { CapturedAt = default } == right with { CapturedAt = default };
}
