using PKHeX.Core;
using UltimatePoKeSync.Contracts;
using UltimatePoKeSync.GameData;

namespace UltimatePoKeSync.GameData.Learnsets;

/// <summary>
/// Base stats for any species of one generation, read from PKHeX's personal tables.
/// </summary>
/// <remarks>
/// <para>
/// Most species are keyed by generation rather than by game, unlike the learnsets of D-027.
/// Gen 3 Deoxys is the exception: each version gives it a different stat form, so the game
/// code selects the same personal table the parser uses. Known final forms in both supported
/// generations are pinned by tests so a table or field-order mistake cannot silently change
/// the recommendation target.
/// </para>
/// <para>
/// Form 0 only. A party member's form is not carried through the snapshot, and every species
/// this answers for in Gen 3 and Gen 5 has a base form whose stats are the ones a role should
/// be judged on.
/// </para>
/// </remarks>
public sealed class PKHeXSpeciesBaseStatsSource : ISpeciesBaseStatsSource
{
    private readonly PokemonGeneration _generation;
    private readonly IPersonalTable _table;
    private readonly int _lastSpecies;

    private PKHeXSpeciesBaseStatsSource(
        PokemonGeneration generation,
        IPersonalTable table,
        int lastSpecies)
    {
        _generation = generation;
        _table = table;
        _lastSpecies = lastSpecies;
    }

    /// <summary>Gen 3: 386 species; Deoxys is selected per game below.</summary>
    public static PKHeXSpeciesBaseStatsSource Gen3 { get; } =
        new(PokemonGeneration.Gen3, PersonalTable.E, 386);

    /// <summary>Gen 4: 493 species, from the HeartGold/SoulSilver table. See D-071.</summary>
    public static PKHeXSpeciesBaseStatsSource Gen4 { get; } =
        new(PokemonGeneration.Gen4, PersonalTable.HGSS, 493);

    /// <summary>Gen 5: 649 species, and BW and B2W2 agree about all of them.</summary>
    public static PKHeXSpeciesBaseStatsSource Gen5 { get; } =
        new(PokemonGeneration.Gen5, PersonalTable.BW, 649);

    public string SourceName => "PKHeX.Core";

    public bool Supports(GameIdentity game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Generation == _generation;
    }

    public StatBlock? FindBaseStats(GameIdentity game, int speciesId)
        => FindProfile(game, speciesId)?.BaseStats;

    public SpeciesBattleProfile? FindProfile(GameIdentity game, int speciesId)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (!Supports(game) || speciesId < 1 || speciesId > _lastSpecies)
        {
            return null;
        }

        IPersonalTable table = _generation == PokemonGeneration.Gen3
            ? Gen3Table(game)
            : _table;
        PersonalInfo info = table.GetFormEntry((ushort)speciesId, 0);
        PokemonType primary = (PokemonType)info.Type1;
        PokemonType secondary = info.Type2 == info.Type1
            ? PokemonType.None
            : (PokemonType)info.Type2;

        return new SpeciesBattleProfile(
            primary,
            secondary,
            new StatBlock(info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE));
    }

    private static IPersonalTable Gen3Table(GameIdentity game) => game.GameCode switch
    {
        "BPRE" or "BPRI" => PersonalTable.FR,
        "BPGE" or "BPGI" => PersonalTable.LG,
        "AXVE" or "AXPE" or "AXVI" or "AXPI" => PersonalTable.RS,
        _ => PersonalTable.E,
    };
}
