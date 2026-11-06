using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.GameData;

/// <summary>English Gen 4 move metadata, pinned at the same Showdown revision. See D-070.</summary>
/// <remarks>
/// Move identity only: number, name and type. What a move does, its power and whether it
/// is physical or special, lives in <see cref="Gen4Rules"/>, because that is a battle rule
/// and this is a name table. The learnsets need it even with no recommendations for Gen 4:
/// a move a Pokémon can learn is named and typed through here.
/// </remarks>
public sealed class ShowdownGen4MoveCatalog : IMoveReferenceCatalog
{
    private static readonly Lazy<ShowdownGen4MoveCatalog> LazyInstance =
        new(() => new ShowdownGen4MoveCatalog());

    private readonly IReadOnlyDictionary<string, MoveReference> _moves;
    private readonly IReadOnlyDictionary<int, MoveReference> _movesByNumber;

    private ShowdownGen4MoveCatalog()
    {
        MoveDataRoot moveData = EmbeddedJson.Load<MoveDataRoot>("gen4-showdown-moves.json");

        Validate(moveData);
        _moves = moveData.Moves.ToDictionary(
            move => move.Id,
            move => new MoveReference(
                move.Number,
                move.Id,
                move.Name,
                Enum.Parse<PokemonType>(move.Type)));
        _movesByNumber = _moves.Values.ToDictionary(move => move.MoveId);
    }

    public static ShowdownGen4MoveCatalog Instance => LazyInstance.Value;

    public PokemonGeneration Generation => PokemonGeneration.Gen4;

    /// <summary>The same pinned Showdown commit as Gen 3, so both describe one snapshot.</summary>
    public string SourceRevision => ShowdownGen3PresetCatalog.Revision;

    public int MoveCount => _moves.Count;

    public MoveReference? Find(string moveNameOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moveNameOrId);

        string key = ShowdownIdentifier.Normalize(moveNameOrId);
        if (_moves.TryGetValue(key, out MoveReference? move))
        {
            return move;
        }

        const string hiddenPower = "hiddenpower";
        if (!key.StartsWith(hiddenPower, StringComparison.Ordinal) ||
            !_moves.TryGetValue(hiddenPower, out MoveReference? baseMove))
        {
            return null;
        }

        string typeName = key[hiddenPower.Length..];
        PokemonType type = Gen3Rules.Instance.TypeChart.Types.SingleOrDefault(
            candidate => ShowdownIdentifier.Normalize(candidate.ToString()) == typeName);
        return type == default
            ? null
            : baseMove with
            {
                ReferenceId = key,
                Name = $"Hidden Power {type}",
                Type = type,
            };
    }

    public MoveReference? Find(int moveId) =>
        _movesByNumber.GetValueOrDefault(moveId);

    private static void Validate(MoveDataRoot moveData)
    {
        // Gen 4 ends at Shadow Force, and every number in between must be present: a gap
        // would mean a move nothing can name.
        const int expectedMoves = 467;

        if (moveData.Generation != 4 ||
            moveData.Revision != ShowdownGen3PresetCatalog.Revision ||
            moveData.Moves.Length != expectedMoves)
        {
            throw new InvalidOperationException("The embedded Showdown Gen 4 move data is malformed.");
        }

        var moveIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < moveData.Moves.Length; index++)
        {
            MoveData move = moveData.Moves[index];
            if (move.Number != index + 1 || move.Id != ShowdownIdentifier.Normalize(move.Id) ||
                string.IsNullOrWhiteSpace(move.Name) ||
                !Enum.TryParse(move.Type, out PokemonType type) ||
                !Gen4Rules.Instance.TypeChart.Types.Contains(type) ||
                !moveIds.Add(move.Id))
            {
                throw new InvalidOperationException(
                    $"Invalid Showdown Gen 4 move entry: {move.Id}.");
            }
        }
    }

    private sealed record MoveDataRoot(int Generation, string Revision, MoveData[] Moves);

    private sealed record MoveData(int Number, string Id, string Name, string Type);
}
