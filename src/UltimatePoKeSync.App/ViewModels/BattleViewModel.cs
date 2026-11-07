using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimatePoKeSync.Analysis;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.App.ViewModels;

/// <summary>
/// The Battle tab: the opponent on the field and how the party lines up against it. See D-077.
/// </summary>
/// <remarks>
/// Formats only. The multipliers, the switch order and the catch chances come from
/// <see cref="BattleMatchupAnalyzer"/>, as everything else on the dashboard comes from the
/// analysis layer (D-028). Empty until a battle starts, and empty again when it ends.
/// </remarks>
public sealed partial class BattleViewModel : ObservableObject
{
    private static readonly IBrush Strong = new SolidColorBrush(Color.Parse("#7FF09A"));
    private static readonly IBrush Weak = new SolidColorBrush(Color.Parse("#FF5B4A"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#9A93B0"));
    private static readonly IBrush Nothing = new SolidColorBrush(Color.Parse("#6F6889"));

    private readonly BattleMatchupAnalyzer _analyzer = new();

    private PartySnapshot? _party;
    private BattleSnapshot _battle = BattleSnapshot.None;

    [ObservableProperty]
    private bool _hasMatchup;

    [ObservableProperty]
    private string _placeholder = "No battle running. This tab fills in when one starts.";

    [ObservableProperty]
    private string _opponentName = string.Empty;

    [ObservableProperty]
    private string _opponentLevel = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TypeChip> _opponentTypes = [];

    [ObservableProperty]
    private double _opponentHpPercent;

    [ObservableProperty]
    private IBrush _opponentHpBrush = Strong;

    [ObservableProperty]
    private string _opponentStatus = string.Empty;

    [ObservableProperty]
    private string _assumption = string.Empty;

    [ObservableProperty]
    private string _activeName = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BattleMoveRow> _moves = [];

    [ObservableProperty]
    private IReadOnlyList<IncomingRow> _incoming = [];

    [ObservableProperty]
    private string _verdict = string.Empty;

    [ObservableProperty]
    private IBrush _verdictBrush = Neutral;

    [ObservableProperty]
    private IReadOnlyList<SwitchRow> _switches = [];

    [ObservableProperty]
    private IReadOnlyList<WeaknessRow> _weaknesses = [];

    [ObservableProperty]
    private IReadOnlyList<CatchRow> _catches = [];

    [ObservableProperty]
    private bool _hasCatches;

    public bool HasActive => Moves.Count > 0 || Incoming.Count > 0;

    public void UpdateParty(PartySnapshot party)
    {
        _party = party;
        Rebuild();
    }

    public void UpdateBattle(BattleSnapshot battle)
    {
        _battle = battle;
        Rebuild();
    }

    /// <summary>"x4", "x1/2", "x0": the way the games' own guides write them.</summary>
    public static string Format(double multiplier) => multiplier switch
    {
        0 => "x0",
        0.25 => "x1/4",
        0.5 => "x1/2",
        _ => "x" + multiplier.ToString("0.##", CultureInfo.InvariantCulture),
    };

    private void Rebuild()
    {
        BattleMatchup? matchup = _party is null ? null : _analyzer.Analyze(_party, _battle);

        if (matchup is null)
        {
            Placeholder = _battle.Kind switch
            {
                BattleKind.Double => "Double battles are not read yet. This tab fills in again for the next single battle.",
                BattleKind.Single => "Battle running, waiting for a clean read of the field.",
                _ => "No battle running. This tab fills in when one starts.",
            };
            HasMatchup = false;
            return;
        }

        OpponentSnapshot opponent = matchup.Opponent;
        OpponentName = Title(opponent.SpeciesName);
        OpponentLevel = $"Lv {opponent.Level}";
        OpponentTypes = [.. matchup.OpponentTypes.Select(type => TypeChip.For(type, "Pokédex type"))];
        OpponentHpPercent = opponent.HpFraction * 100;
        OpponentHpBrush = opponent.HpFraction switch
        {
            > 0.5 => Strong,
            > 0.2 => new SolidColorBrush(Color.Parse("#FFD23F")),
            _ => Weak,
        };
        OpponentStatus = opponent.Status == StatusCondition.None ? string.Empty : StatusLabel(opponent.Status);
        Assumption = $"Assumes it attacks with {string.Join(" / ", matchup.OpponentTypes)} moves. Its real moves are never read.";

        ActiveName = matchup.Active is PokemonSnapshot active ? Name(active) : "Your Pokémon";

        Moves = [.. matchup.ActiveMoves.Select(MoveRowFor)];
        Incoming =
        [
            .. matchup.Incoming.Select(hit => new IncomingRow(
                TypeChip.For(hit.AttackingType, "its type"),
                Format(hit.Multiplier),
                IncomingBrush(hit.Multiplier))),
        ];

        (Verdict, VerdictBrush) = matchup.WorstIncoming switch
        {
            null => (string.Empty, Neutral),
            >= 4 => ($"Worst case {Format(4)}: switch out.", Weak),
            >= 2 => ($"Worst case {Format(2)}: consider switching.", Weak),
            double worst when worst >= 1 => ($"Worst case {Format(worst)}: fine to stay.", Neutral),
            double worst => ($"Worst case {Format(worst)}: it resists both.", Strong),
        };

        Switches =
        [
            .. matchup.SwitchOptions.Select(option => new SwitchRow(
                Name(option.Member) + (option.IsActive ? "  (in)" : string.Empty),
                Format(option.WorstIncoming),
                IncomingBrush(option.WorstIncoming),
                option.BestMove is MoveMatchup best ? Format(best.Multiplier) : "-",
                option.BestMove is MoveMatchup move ? OutgoingBrush(move.Multiplier) : Nothing,
                option.BestMove?.Move.Name ?? "no damaging move",
                option.IsFainted ? 0.45 : 1)),
        ];

        Weaknesses =
        [
            .. matchup.Weaknesses.Select(bucket => new WeaknessRow(
                Format(bucket.Multiplier),
                OutgoingBrush(bucket.Multiplier),
                [.. bucket.Types.Select(type => TypeChip.For(type, $"{Format(bucket.Multiplier)} against it"))])),
        ];

        Catches =
        [
            .. matchup.CatchChances.Select(chance => new CatchRow(
                chance.Ball switch
                {
                    StandardBall.PokeBall => "Poké Ball",
                    StandardBall.GreatBall => "Great Ball",
                    _ => "Ultra Ball",
                },
                chance.Chance >= 1 ? "100%" : (chance.Chance * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                chance.Chance)),
        ];
        HasCatches = Catches.Count > 0;

        HasMatchup = true;
        OnPropertyChanged(nameof(HasActive));
    }

    private static BattleMoveRow MoveRowFor(MoveMatchup move) => move.Effect switch
    {
        MoveEffect.Status => new BattleMoveRow(move.Move.Name, TypeChip.For(move.Move.Type, "move type"), "status", Nothing, string.Empty),
        MoveEffect.Fixed => new BattleMoveRow(
            move.Move.Name, TypeChip.For(move.Move.Type, "move type"),
            move.Multiplier == 0 ? Format(0) : "fixed", move.Multiplier == 0 ? Nothing : Neutral, string.Empty),
        _ => new BattleMoveRow(
            move.Move.Name, TypeChip.For(move.Move.Type, "move type"),
            Format(move.Multiplier), OutgoingBrush(move.Multiplier), move.IsStab ? "STAB" : string.Empty),
    };

    /// <summary>Damage the player deals: more is better.</summary>
    private static IBrush OutgoingBrush(double multiplier) => multiplier switch
    {
        0 => Nothing,
        > 1 => Strong,
        < 1 => Weak,
        _ => Neutral,
    };

    /// <summary>Damage the player takes: less is better.</summary>
    private static IBrush IncomingBrush(double multiplier) => multiplier switch
    {
        0 => Strong,
        > 1 => Weak,
        < 1 => Strong,
        _ => Neutral,
    };

    private static string Name(PokemonSnapshot member) =>
        string.IsNullOrWhiteSpace(member.Nickname) ? Title(member.SpeciesName) : member.Nickname;

    /// <summary>Gen 4 spells species in capitals; the rest of the window title-cases them.</summary>
    private static string Title(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());

    private static string StatusLabel(StatusCondition status) => status switch
    {
        StatusCondition.Sleep => "SLP",
        StatusCondition.Poison => "PSN",
        StatusCondition.BadPoison => "PSN",
        StatusCondition.Burn => "BRN",
        StatusCondition.Freeze => "FRZ",
        StatusCondition.Paralysis => "PAR",
        _ => string.Empty,
    };
}

public sealed record BattleMoveRow(string Name, TypeChip Type, string Multiplier, IBrush MultiplierBrush, string Stab);

public sealed record IncomingRow(TypeChip Type, string Multiplier, IBrush MultiplierBrush);

public sealed record SwitchRow(
    string Name,
    string Takes,
    IBrush TakesBrush,
    string Hits,
    IBrush HitsBrush,
    string Move,
    double Opacity);

public sealed record WeaknessRow(string Multiplier, IBrush MultiplierBrush, IReadOnlyList<TypeChip> Types);

public sealed record CatchRow(string Ball, string Chance, double Fraction)
{
    public double Percent => Math.Clamp(Fraction * 100, 0, 100);
}
