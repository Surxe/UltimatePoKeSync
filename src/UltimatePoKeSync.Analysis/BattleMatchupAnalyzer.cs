using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;

namespace UltimatePoKeSync.Analysis;

/// <summary>Turns a live battle and the party into the Battle tab's facts. See D-077.</summary>
public sealed class BattleMatchupAnalyzer
{
    private static readonly double[] BucketOrder = [4, 2, 0.5, 0.25, 0];

    private readonly IGenerationRulesResolver _rulesResolver;

    public BattleMatchupAnalyzer()
        : this(GenerationRulesResolver.Default)
    {
    }

    public BattleMatchupAnalyzer(IGenerationRulesResolver rulesResolver)
    {
        ArgumentNullException.ThrowIfNull(rulesResolver);
        _rulesResolver = rulesResolver;
    }

    /// <summary>
    /// The matchup, or null when there is nothing to show: no battle, a double battle (not read
    /// yet), no opponent on the field, or a generation without rules.
    /// </summary>
    public BattleMatchup? Analyze(PartySnapshot party, BattleSnapshot battle)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(battle);

        if (battle.Kind != BattleKind.Single || battle.Opponent is not OpponentSnapshot opponent)
        {
            return null;
        }

        if (_rulesResolver.Resolve(battle.Game.Generation) is not IGenerationRules rules)
        {
            return null;
        }

        PokemonType[] opponentTypes = opponent.SecondaryType == PokemonType.None
            ? [opponent.PrimaryType]
            : [opponent.PrimaryType, opponent.SecondaryType];

        PokemonSnapshot? active = battle.PlayerActivePersonality is uint personality
            ? party.Battlers.FirstOrDefault(member => member.PersonalityValue == personality)
            : null;

        IReadOnlyList<MoveMatchup> activeMoves = active is null ? [] : Moves(active, opponent, rules);
        IReadOnlyList<IncomingHit> incoming = active is null ? [] : Incoming(active, opponentTypes, rules);

        SwitchOption[] options =
        [
            .. party.Battlers
                .Select(member =>
                {
                    MoveMatchup? best = Moves(member, opponent, rules)
                        .Where(move => move.Effect == MoveEffect.Damaging)
                        .OrderByDescending(move => move.Multiplier)
                        .ThenByDescending(move => move.IsStab)
                        .ThenByDescending(move => rules.GetMoveBasePower(move.Move.MoveId))
                        .FirstOrDefault();

                    return new SwitchOption(
                        member,
                        IsActive: ReferenceEquals(member, active),
                        IsFainted: member.CurrentHp <= 0,
                        WorstIncoming: Incoming(member, opponentTypes, rules).Max(hit => hit.Multiplier),
                        BestMove: best);
                })
                .OrderBy(option => option.IsFainted)
                .ThenBy(option => option.WorstIncoming)
                .ThenByDescending(option => option.BestMove?.Multiplier ?? -1)
                .ThenByDescending(option => option.BestMove?.IsStab ?? false)
                .ThenBy(option => option.Member.SlotIndex),
        ];

        IReadOnlyList<CatchChance> catchChances = battle.IsWild
            ? CatchChanceCalculator.ForStandardBalls(opponent)
            : [];

        return new BattleMatchup(
            opponent,
            active,
            activeMoves,
            incoming,
            options,
            Weaknesses(opponent, rules),
            catchChances);
    }

    private static IReadOnlyList<MoveMatchup> Moves(
        PokemonSnapshot member,
        OpponentSnapshot opponent,
        IGenerationRules rules) =>
        [
            .. member.Moves
                .Where(move => !move.IsEmpty)
                .Select(move =>
                {
                    bool isStab = move.Type != PokemonType.None
                        && (move.Type == member.PrimaryType || move.Type == member.SecondaryType);

                    if (rules.GetMoveCategory(move.MoveId, move.Type) == MoveCategory.Status)
                    {
                        return new MoveMatchup(move, MoveEffect.Status, 1, isStab);
                    }

                    double multiplier = rules.TypeChart.GetMultiplier(
                        move.Type, opponent.PrimaryType, opponent.SecondaryType);

                    // A fixed-damage move still cannot touch an immune type: Seismic Toss does
                    // nothing to a Ghost. Only the scaling is lost, not the immunity.
                    if (!rules.CanProvideSuperEffectiveCoverage(move.MoveId))
                    {
                        return new MoveMatchup(move, MoveEffect.Fixed, multiplier == 0 ? 0 : 1, isStab);
                    }

                    return new MoveMatchup(move, MoveEffect.Damaging, multiplier, isStab);
                }),
        ];

    private static IReadOnlyList<IncomingHit> Incoming(
        PokemonSnapshot member,
        IReadOnlyList<PokemonType> opponentTypes,
        IGenerationRules rules) =>
        [
            .. opponentTypes.Select(type => new IncomingHit(
                type,
                rules.GetDefensiveMultiplier(type, member.PrimaryType, member.SecondaryType, member.AbilityId))),
        ];

    private static IReadOnlyList<TypeBucket> Weaknesses(OpponentSnapshot opponent, IGenerationRules rules)
    {
        var byMultiplier = rules.TypeChart.Types
            .Select(type => (Type: type, Multiplier: rules.TypeChart.GetMultiplier(
                type, opponent.PrimaryType, opponent.SecondaryType)))
            .Where(entry => entry.Multiplier != 1)
            .GroupBy(entry => entry.Multiplier)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PokemonType>)[.. group.Select(entry => entry.Type)]);

        return
        [
            .. BucketOrder
                .Where(byMultiplier.ContainsKey)
                .Select(multiplier => new TypeBucket(multiplier, byMultiplier[multiplier])),
        ];
    }
}
