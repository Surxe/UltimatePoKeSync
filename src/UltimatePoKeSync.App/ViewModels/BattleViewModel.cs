using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimatePoKeSync.Analysis;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.App.ViewModels;

/// <summary>
/// The Battle tab: the opponent on the field, the catch chance, and two tree diagrams for the
/// Pokémon on the field (D-081). Each of its moves branches to the opponent's types and joins in
/// the move's result; each of the opponent's types branches to its types the same way.
/// </summary>
/// <remarks>
/// Formats only. The multipliers, their per-type factors and the catch chances come from
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
    private bool _hasActive;

    [ObservableProperty]
    private string _activeName = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TypeChip> _activeTypes = [];

    [ObservableProperty]
    private IReadOnlyList<TreeRow> _offense = [];

    [ObservableProperty]
    private IReadOnlyList<TreeRow> _defense = [];

    [ObservableProperty]
    private IReadOnlyList<CatchRow> _catches = [];

    [ObservableProperty]
    private bool _hasCatches;

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

    /// <summary>
    /// The working behind a multiplier, for a tooltip: "FIRE → GRASS x2 · STEEL x2 = x4", and
    /// the ability when it overrode the chart.
    /// </summary>
    public static string Working(
        PokemonType attackingType,
        IReadOnlyList<TypeFactor> factors,
        double result,
        string? ability = null)
    {
        if (factors.Count == 0)
        {
            return string.Empty;
        }

        string chart = $"{Upper(attackingType)} → " + string.Join(
            " · ", factors.Select(factor => $"{Upper(factor.DefendingType)} {Format(factor.Multiplier)}"));
        double product = factors.Aggregate(1.0, (total, factor) => total * factor.Multiplier);

        if (factors.Count > 1)
        {
            chart += $" = {Format(product)}";
        }

        return product == result || ability is null
            ? chart
            : $"{chart}, then {ability} makes it {Format(result)}";
    }

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

        PokemonSnapshot? active = matchup.Active;
        HasActive = active is not null;
        ActiveName = active is null ? "Your Pokémon" : Name(active);
        ActiveTypes = active is null ? [] : TypesOf(active);

        Offense = [.. matchup.ActiveMoves.Select(MoveTree)];
        Defense = [.. matchup.Incoming.Select(hit => TypeTree(hit, active))];

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
    }

    /// <summary>One move: its type, a branch per opponent type, and the move's result.</summary>
    private static TreeRow MoveTree(MoveMatchup move)
    {
        TypeChip type = TypeChip.For(move.Move.Type, "move type");
        string stab = move.IsStab && move.Effect != MoveEffect.Status ? "STAB" : string.Empty;

        if (move.Effect == MoveEffect.Status)
        {
            return new TreeRow(
                move.Move.Name, type, stab, [], "status", Nothing,
                "No damage: the type chart does not apply.", string.Empty);
        }

        IReadOnlyList<TreeBranch> branches =
        [
            .. move.Factors.Select(factor => new TreeBranch(
                TypeChip.For(factor.DefendingType, "its type"),
                Format(factor.Multiplier),
                OutgoingBrush(factor.Multiplier))),
        ];
        string working = Working(move.Move.Type, move.Factors, move.Multiplier);

        if (move.Effect == MoveEffect.Fixed)
        {
            return move.Multiplier == 0
                ? new TreeRow(move.Move.Name, type, stab, branches, Format(0), Nothing,
                    "Fixed damage, but immunities still count.", working)
                : new TreeRow(move.Move.Name, type, stab, branches, "fixed", Neutral,
                    "Fixed damage: the multipliers do not scale it.", working);
        }

        return new TreeRow(
            move.Move.Name, type, stab, branches, Format(move.Multiplier), OutgoingBrush(move.Multiplier),
            string.Empty, working);
    }

    /// <summary>One of the opponent's types: a branch per type of yours, and its result.</summary>
    private static TreeRow TypeTree(IncomingHit hit, PokemonSnapshot? defender)
    {
        string? ability = hit.AbilityAdjusted
            ? string.IsNullOrWhiteSpace(defender?.AbilityName) ? "its ability" : defender.AbilityName
            : null;
        double chart = hit.Factors.Aggregate(1.0, (total, factor) => total * factor.Multiplier);

        return new TreeRow(
            string.Empty,
            TypeChip.For(hit.AttackingType, "its type"),
            string.Empty,
            [
                .. hit.Factors.Select(factor => new TreeBranch(
                    TypeChip.For(factor.DefendingType, "your type"),
                    Format(factor.Multiplier),
                    IncomingBrush(factor.Multiplier))),
            ],
            Format(hit.Multiplier) + (ability is null ? string.Empty : "*"),
            IncomingBrush(hit.Multiplier),
            ability is null ? string.Empty : $"* {ability}: the chart says {Format(chart)}",
            Working(hit.AttackingType, hit.Factors, hit.Multiplier, ability));
    }

    private static IReadOnlyList<TypeChip> TypesOf(PokemonSnapshot member) =>
        member.SecondaryType == PokemonType.None
            ? [TypeChip.For(member.PrimaryType, "your type")]
            : [TypeChip.For(member.PrimaryType, "your type"), TypeChip.For(member.SecondaryType, "your type")];

    private static string Upper(PokemonType type) => type.ToString().ToUpperInvariant();

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

/// <summary>One branch of a tree: a defending type and the chart's multiplier against it.</summary>
public sealed record TreeBranch(TypeChip Type, string Multiplier, IBrush Brush);

/// <summary>
/// One row of a tree diagram (D-081): the attacking side (a move, or one of the opponent's
/// types), a branch per defending type, and the combined result at the end.
/// </summary>
/// <param name="Label">The move's name; empty on the opponent's tree, where the type is the node.</param>
/// <param name="Type">The attacking type.</param>
/// <param name="Tag">"STAB", or empty.</param>
/// <param name="Branches">One per defending type; none for a status move.</param>
/// <param name="Result">The combined multiplier, "status" or "fixed"; "*" when an ability changed it.</param>
/// <param name="ResultBrush">Good or bad for the player.</param>
/// <param name="Note">Why the result is not just the product, when it is not.</param>
/// <param name="Working">The full working, for the tooltip.</param>
public sealed record TreeRow(
    string Label,
    TypeChip Type,
    string Tag,
    IReadOnlyList<TreeBranch> Branches,
    string Result,
    IBrush ResultBrush,
    string Note,
    string Working)
{
    public int BranchCount => Branches.Count;

    public bool HasBranches => Branches.Count > 0;

    public bool HasLabel => Label.Length > 0;

    public bool HasNote => Note.Length > 0;

    /// <summary>A multiplier reads large; a word ("status", "fixed") smaller, to fit the column.</summary>
    public double ResultSize => Result.StartsWith('x') ? 20 : 14;
}

public sealed record CatchRow(string Ball, string Chance, double Fraction)
{
    public double Percent => Math.Clamp(Fraction * 100, 0, 100);
}
