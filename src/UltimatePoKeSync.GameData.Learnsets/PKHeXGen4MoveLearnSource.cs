using PKHeX.Core;
using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;

namespace UltimatePoKeSync.GameData.Learnsets;

/// <summary>
/// Every HeartGold and SoulSilver move source (level up, machines and tutors), read from
/// PKHeX. See D-071.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="PKHeXGen4MoveLearnSource"/>, per game for the reason D-027 gives:
/// Diamond, Pearl and Platinum teach at levels of their own, so only HeartGold and SoulSilver
/// are answered here, and the Sinnoh games are refused rather than given Johto's table.
/// </para>
/// <para>
/// Egg moves are absent, as in every generation: a Pokémon already in the party cannot
/// acquire one (D-030). Tutors include the HeartGold/SoulSilver Battle Frontier tutors, which
/// PKHeX lists with the rest.
/// </para>
/// </remarks>
public sealed class PKHeXGen4MoveLearnSource : IMoveLearnSource
{
    private const int FirstGen4Species = 1;
    private const int LastGen4Species = 493;

    /// <summary>Enough room for every move index PKHeX may flag.</summary>
    private const int MoveFlagCount = 600;

    private static readonly Lazy<PKHeXGen4MoveLearnSource> LazyInstance =
        new(() => new PKHeXGen4MoveLearnSource(ShowdownGen4MoveCatalog.Instance));

    private readonly IMoveReferenceCatalog _moveCatalog;

    public PKHeXGen4MoveLearnSource(IMoveReferenceCatalog moveCatalog)
    {
        ArgumentNullException.ThrowIfNull(moveCatalog);
        if (moveCatalog.Generation != PokemonGeneration.Gen4)
        {
            throw new ArgumentException(
                $"A Gen 4 move source needs a Gen 4 move catalog, got {moveCatalog.Generation}.",
                nameof(moveCatalog));
        }

        _moveCatalog = moveCatalog;
    }

    public static PKHeXGen4MoveLearnSource Instance => LazyInstance.Value;

    public string SourceName => "PKHeX.Core";

    public bool Supports(GameIdentity game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Generation == PokemonGeneration.Gen4 && ResolveSource(game.GameCode) is not null;
    }

    public IReadOnlyList<LearnableMove> FindLearnableMoves(
        GameIdentity game,
        int speciesId,
        int maximumLevel)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (maximumLevel is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLevel));
        }

        ILearnSource? source = ResolveSource(game.GameCode);
        if (source is null)
        {
            throw new NotSupportedException($"No Gen 4 move source is available for {game}.");
        }

        if (speciesId is < FirstGen4Species or > LastGen4Species)
        {
            return [];
        }

        var species = (ushort)speciesId;
        var seen = new HashSet<int>();
        var result = new List<LearnableMove>();

        AddLevelUpMoves(source, species, maximumLevel, seen, result);
        AddFlagged(source, species, maximumLevel, MoveSourceType.Machine, MoveLearnMethod.Machine, seen, result);
        AddFlagged(source, species, maximumLevel, MoveSourceType.AllTutors, MoveLearnMethod.Tutor, seen, result);

        return result;
    }

    /// <summary>
    /// Level-up moves come from the learnset rather than the flag scan, because it is the
    /// only source that also carries the level, and the level is what a player needs.
    /// </summary>
    private void AddLevelUpMoves(
        ILearnSource source,
        ushort species,
        int maximumLevel,
        HashSet<int> seen,
        List<LearnableMove> result)
    {
        Learnset learnset = GetLearnset(source, species);
        ReadOnlySpan<ushort> moves = learnset.GetAllMoves();
        ReadOnlySpan<byte> levels = learnset.GetAllLevels();

        for (int index = 0; index < moves.Length; index++)
        {
            if (levels[index] > maximumLevel || !seen.Add(moves[index]))
            {
                continue;
            }

            MoveReference? move = _moveCatalog.Find(moves[index]);
            if (move is not null)
            {
                result.Add(new LearnableMove(move, MoveLearnMethod.LevelUp, levels[index]));
            }
        }
    }

    private void AddFlagged(
        ILearnSource source,
        ushort species,
        int maximumLevel,
        MoveSourceType sourceType,
        MoveLearnMethod method,
        HashSet<int> seen,
        List<LearnableMove> result)
    {
        var pokemon = new PK4 { Species = species, CurrentLevel = (byte)maximumLevel };
        var evolution = new EvoCriteria
        {
            Species = species,
            Form = 0,
            LevelMin = 1,
            LevelMax = (byte)maximumLevel,
        };

        Span<bool> flags = stackalloc bool[MoveFlagCount];
        source.GetAllMoves(flags, pokemon, evolution, sourceType);

        for (int moveId = 0; moveId < flags.Length; moveId++)
        {
            if (!flags[moveId] || !seen.Add(moveId))
            {
                continue;
            }

            MoveReference? move = _moveCatalog.Find(moveId);
            if (move is not null)
            {
                result.Add(new LearnableMove(move, method, null));
            }
        }
    }

    private static Learnset GetLearnset(ILearnSource source, ushort species) => source switch
    {
        LearnSource4HGSS johto => johto.GetLearnset(species, 0),
        _ => throw new NotSupportedException($"Unhandled learn source {source.GetType().Name}."),
    };

    /// <summary>
    /// Language does not change a learnset, so every localisation of HeartGold and SoulSilver
    /// shares one source, but each code is listed rather than matched by prefix, because an
    /// unrecognised game must be refused rather than guessed at (D-005).
    /// </summary>
    private static ILearnSource? ResolveSource(string gameCode) => gameCode switch
    {
        // HeartGold, then SoulSilver.
        "IPKE" or "IPKP" or "IPKJ" or "IPKD" or "IPKF" or "IPKI" or "IPKS" or "IPKK" or
        "IPGE" or "IPGP" or "IPGJ" or "IPGD" or "IPGF" or "IPGI" or "IPGS" or "IPGK" =>
            LearnSource4HGSS.Instance,

        _ => null,
    };
}
