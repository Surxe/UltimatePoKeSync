using PKHeX.Core;
using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;

namespace UltimatePoKeSync.GameData.Learnsets;

/// <summary>
/// The Gen 4 evolution table, read from PKHeX, for HeartGold and SoulSilver. See D-071.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="PKHeXGen4EvolutionSource"/>, with the triggers Gen 4 introduced
/// described rather than left to the fallback: holding an item by day or by night (Gligar,
/// Sneasel, Happiny), knowing a move (Yanmega, Ambipom, Mamoswine and others), and having a
/// Remoraid in the party (Mantyke).
/// </para>
/// <para>
/// PKHeX keeps one table for the whole generation, but the location evolutions in it,
/// Magnezone, Probopass, Leafeon and Glaceon, need places that exist only in Sinnoh. So this
/// answers for HeartGold and SoulSilver only, where it can say plainly that those cannot
/// happen, instead of promising them to a player who has nowhere to go.
/// </para>
/// </remarks>
public sealed class PKHeXGen4EvolutionSource : IEvolutionSource
{
    private const int FirstGen4Species = 1;
    private const int LastGen4Species = 493;

    private static readonly Lazy<PKHeXGen4EvolutionSource> LazyInstance = new(() => new());

    private readonly GameStrings _strings = GameInfo.GetStrings("en");
    private readonly string[] _gen4Items;

    public PKHeXGen4EvolutionSource() =>
        // Item indices in the evolution table are Gen 4 ones, read from Gen 4's own list for
        // the reason the Gen 5 source gives.
        _gen4Items = _strings.GetItemStrings(EntityContext.Gen4, GameVersion.SS);

    public static PKHeXGen4EvolutionSource Instance => LazyInstance.Value;

    public string SourceName => "PKHeX.Core";

    public bool Supports(GameIdentity game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Generation == PokemonGeneration.Gen4 && IsJohto(game.GameCode);
    }

    public IReadOnlyList<EvolutionStep> FindEvolutions(GameIdentity game, int speciesId)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Supports(game))
        {
            throw new NotSupportedException($"No Gen 4 evolution data is available for {game}.");
        }

        if (speciesId is < FirstGen4Species or > LastGen4Species)
        {
            return [];
        }

        ReadOnlyMemory<EvolutionMethod> forward =
            EvolutionTree.Evolves4.Forward.GetForward((ushort)speciesId, 0);

        var steps = new List<EvolutionStep>(forward.Length);
        foreach (EvolutionMethod method in forward.Span)
        {
            if (method.Species is < FirstGen4Species or > LastGen4Species)
            {
                continue;
            }

            steps.Add(Describe(method));
        }

        return steps;
    }

    private EvolutionStep Describe(EvolutionMethod method)
    {
        string into = SpeciesName(method.Species);
        (EvolutionTrigger trigger, string requirement) = method.Method switch
        {
            EvolutionType.LevelUp =>
                (EvolutionTrigger.Level, $"at Lv.{method.Level}"),

            EvolutionType.UseItem =>
                (EvolutionTrigger.Item, $"with a {ItemName(method.Argument)}"),

            EvolutionType.Trade =>
                (EvolutionTrigger.Trade, "when traded"),
            EvolutionType.TradeHeldItem =>
                (EvolutionTrigger.Trade, $"when traded holding a {ItemName(method.Argument)}"),

            EvolutionType.LevelUpFriendship =>
                (EvolutionTrigger.Friendship, "on levelling up with high friendship"),
            EvolutionType.LevelUpFriendshipMorning =>
                (EvolutionTrigger.Friendship, "on levelling up with high friendship, in the morning"),
            EvolutionType.LevelUpFriendshipNight =>
                (EvolutionTrigger.Friendship, "on levelling up with high friendship, at night"),

            EvolutionType.LevelUpMale =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, if male"),
            EvolutionType.LevelUpFemale =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, if female"),

            EvolutionType.UseItemMale =>
                (EvolutionTrigger.Item, $"with a {ItemName(method.Argument)}, if male"),
            EvolutionType.UseItemFemale =>
                (EvolutionTrigger.Item, $"with a {ItemName(method.Argument)}, if female"),

            EvolutionType.LevelUpHeldItemDay =>
                (EvolutionTrigger.Condition,
                    $"on levelling up holding a {ItemName(method.Argument)}, during the day"),
            EvolutionType.LevelUpHeldItemNight =>
                (EvolutionTrigger.Condition,
                    $"on levelling up holding a {ItemName(method.Argument)}, at night"),
            EvolutionType.LevelUpKnowMove =>
                (EvolutionTrigger.Condition,
                    $"on levelling up knowing {MoveName(method.Argument)}"),
            EvolutionType.LevelUpWithTeammate =>
                (EvolutionTrigger.Condition,
                    $"on levelling up with a {SpeciesName(method.Argument)} in the party"),

            // These need a magnetic field, a mossy rock or an icy rock, and all three are in
            // Sinnoh. There is none in Johto or Kanto, so in these games it does not happen.
            EvolutionType.LevelUpElectric or EvolutionType.LevelUpForest or EvolutionType.LevelUpCold =>
                (EvolutionTrigger.Condition,
                    "only at a place in Sinnoh, so not in HeartGold or SoulSilver"),

            // Tyrogue splits three ways on the stats it happens to have at level 20.
            EvolutionType.LevelUpATK =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, with Attack above Defense"),
            EvolutionType.LevelUpDEF =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, with Defense above Attack"),
            EvolutionType.LevelUpAeqD =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, with Attack equal to Defense"),

            // Wurmple's fork is decided by the personality value, before it ever hatched.
            EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5 =>
                (EvolutionTrigger.Condition, $"at Lv.{method.Level}, if its personality says so"),

            // Ninjask is a plain level evolution; Shedinja is an extra creature on the side,
            // and the one that can fail to appear.
            EvolutionType.LevelUpNinjask =>
                (EvolutionTrigger.Level, $"at Lv.{method.Level}"),
            EvolutionType.LevelUpShedinja =>
                (EvolutionTrigger.Condition,
                    $"at Lv.{method.Level}, as a second one, with a free party slot and a spare Poké Ball"),

            EvolutionType.LevelUpBeauty =>
                (EvolutionTrigger.Condition, "on levelling up with very high beauty"),

            _ => (EvolutionTrigger.Condition, "under a condition this app does not know"),
        };

        // A level is recorded only where reaching it is genuinely enough. Elsewhere the
        // column holds a copy of the argument or nothing at all, and promising a level that
        // is not sufficient on its own is worse than promising nothing. Shedinja is the
        // exception among the level triggers: Lv.20 is when it can appear, not when it will.
        bool certainAtLevel = method.Method is
            EvolutionType.LevelUp or
            EvolutionType.LevelUpNinjask or
            EvolutionType.LevelUpMale or EvolutionType.LevelUpFemale or
            EvolutionType.LevelUpATK or EvolutionType.LevelUpDEF or EvolutionType.LevelUpAeqD or
            EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5;
        int? level = certainAtLevel && method.Level > 0 ? method.Level : null;

        PokemonGender? requiredGender = method.Method switch
        {
            EvolutionType.LevelUpMale or EvolutionType.UseItemMale => PokemonGender.Male,
            EvolutionType.LevelUpFemale or EvolutionType.UseItemFemale => PokemonGender.Female,
            _ => null,
        };

        return new EvolutionStep(
            method.Species,
            into,
            trigger,
            level,
            requirement,
            method.Method == EvolutionType.LevelUpShedinja,
            requiredGender);
    }

    private string SpeciesName(ushort species) =>
        species < _strings.Species.Count ? _strings.Species[species] : $"#{species}";

    private string ItemName(ushort item) =>
        item < _gen4Items.Length ? _gen4Items[item] : $"item #{item}";

    private string MoveName(ushort move) =>
        move < _strings.Move.Count ? _strings.Move[move] : $"move #{move}";

    private static bool IsJohto(string gameCode) =>
        gameCode.Length == 4 && gameCode[..3] is "IPK" or "IPG";
}
