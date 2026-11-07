using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Analysis;

/// <summary>
/// The type facts of one moment of a battle, for the Battle tab. See D-077.
/// </summary>
/// <remarks>
/// Built on two assumptions the screen states: the opponent attacks only with moves of its own
/// types (its real moves are never read, D-075), and abilities are ignored on its side. The
/// player's own abilities still count on incoming damage, as on the Team tab, because the
/// player knows them.
/// </remarks>
/// <param name="Opponent">The opponent on the field.</param>
/// <param name="Active">
/// The player's Pokémon on the field, found in the party by personality value; null when it
/// could not be matched (for example, between a faint and the next send-out).
/// </param>
/// <param name="ActiveMoves">The active Pokémon's moves against the opponent, in slot order.</param>
/// <param name="Incoming">The opponent's own types against the active Pokémon.</param>
/// <param name="SwitchOptions">Every party battler, in party slot order (D-078).</param>
/// <param name="Weaknesses">Attacking types grouped by multiplier against the opponent, x1 left out.</param>
/// <param name="CatchChances">Per standard ball, in a wild battle; empty otherwise.</param>
public sealed record BattleMatchup(
    OpponentSnapshot Opponent,
    PokemonSnapshot? Active,
    IReadOnlyList<MoveMatchup> ActiveMoves,
    IReadOnlyList<IncomingHit> Incoming,
    IReadOnlyList<SwitchOption> SwitchOptions,
    IReadOnlyList<TypeBucket> Weaknesses,
    IReadOnlyList<CatchChance> CatchChances)
{
    /// <summary>The opponent's types, the ones it is assumed to attack with.</summary>
    public IReadOnlyList<PokemonType> OpponentTypes { get; } =
        Opponent.SecondaryType == PokemonType.None
            ? [Opponent.PrimaryType]
            : [Opponent.PrimaryType, Opponent.SecondaryType];
}

public enum MoveEffect
{
    /// <summary>Damage scaled by the type chart.</summary>
    Damaging = 0,

    /// <summary>No damage: the multiplier does not apply.</summary>
    Status = 1,

    /// <summary>Damage that ignores the chart's scaling (Seismic Toss, Dragon Rage, OHKO moves).</summary>
    Fixed = 2,
}

/// <param name="Move">The move.</param>
/// <param name="Effect">Whether the multiplier means anything for it.</param>
/// <param name="Multiplier">Type multiplier against the opponent; 1 for status and fixed moves.</param>
/// <param name="IsStab">The move shares a type with the user.</param>
/// <param name="Factors">
/// The chart entry for the move's type against each of the opponent's types: the branches of
/// the Battle tab's move tree (D-081). Empty for status moves.
/// </param>
public sealed record MoveMatchup(
    MoveSlot Move,
    MoveEffect Effect,
    double Multiplier,
    bool IsStab,
    IReadOnlyList<TypeFactor> Factors);

/// <param name="AttackingType">One of the opponent's types.</param>
/// <param name="Multiplier">What it does to the defender, ability included.</param>
/// <param name="Factors">The chart entry against each of the defender's types: the branches of its tree (D-081).</param>
/// <param name="AbilityAdjusted">
/// The defender's ability changed the result, so <paramref name="Multiplier"/> is not the
/// product of <paramref name="Factors"/> (Levitate against Ground, for example).
/// </param>
public sealed record IncomingHit(
    PokemonType AttackingType,
    double Multiplier,
    IReadOnlyList<TypeFactor> Factors,
    bool AbilityAdjusted);

/// <summary>One cell of the type chart: an attacking type against one defending type.</summary>
/// <param name="DefendingType">One of the defender's types.</param>
/// <param name="Multiplier">x2, x1, x1/2 or x0.</param>
public sealed record TypeFactor(PokemonType DefendingType, double Multiplier);

/// <param name="Member">A party battler.</param>
/// <param name="IsActive">It is the one on the field.</param>
/// <param name="IsFainted">
/// It has no HP left, as far as the party says. In Gen 4 the party is a battle behind
/// (D-067), so a Pokémon that fainted in this battle still shows its old HP until it ends.
/// </param>
/// <param name="Incoming">Each of the opponent's types against it.</param>
/// <param name="BestMove">Its strongest damaging move by multiplier, or null when it has none.</param>
public sealed record SwitchOption(
    PokemonSnapshot Member,
    bool IsActive,
    bool IsFainted,
    IReadOnlyList<IncomingHit> Incoming,
    MoveMatchup? BestMove)
{
    /// <summary>The worst the opponent's types do to it.</summary>
    public double WorstIncoming => Incoming.Max(hit => hit.Multiplier);
}

/// <param name="Multiplier">x4, x2, x1/2, x1/4 or x0.</param>
/// <param name="Types">Attacking types that land with it, in chart order.</param>
public sealed record TypeBucket(double Multiplier, IReadOnlyList<PokemonType> Types);
