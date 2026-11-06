using UltimatePoKeSync.GameData;

namespace UltimatePoKeSync.GameData.Learnsets;

/// <summary>
/// Every generation PKHeX answers for, behind one source each. See D-043.
/// </summary>
/// <remarks>
/// One place to add a generation, so nothing above has to be told there is a new one. What
/// is not here is not supported, and the layers above find that out by asking rather than by
/// reading garbage.
/// </remarks>
public static class PKHeXSources
{
    public static IMoveLearnSource Learnsets { get; } = new CompositeMoveLearnSource(
        PKHeXGen3MoveLearnSource.Instance,
        PKHeXGen4MoveLearnSource.Instance,
        PKHeXGen5MoveLearnSource.Instance);

    public static IEvolutionSource Evolutions { get; } = new CompositeEvolutionSource(
        PKHeXGen3EvolutionSource.Instance,
        PKHeXGen4EvolutionSource.Instance,
        PKHeXGen5EvolutionSource.Instance);

    public static ISpeciesBaseStatsSource BaseStats { get; } = new CompositeSpeciesBaseStatsSource(
        PKHeXSpeciesBaseStatsSource.Gen3,
        PKHeXSpeciesBaseStatsSource.Gen4,
        PKHeXSpeciesBaseStatsSource.Gen5);

    /// <summary>
    /// Where a Pokémon can be caught, and when the story allows it. Black and White share a
    /// region and differ by a handful of exclusives, so they are one catalog each rather than
    /// one file each. See D-055.
    /// </summary>
    public static IEncounterCatalog Encounters { get; } = new CompositeEncounterCatalog(
        UnovaEncounterCatalog.Black,
        UnovaEncounterCatalog.White,
        HoennEncounterCatalog.Emerald,
        HoennEncounterCatalog.Ruby,
        HoennEncounterCatalog.Sapphire,
        KantoEncounterCatalog.FireRed,
        KantoEncounterCatalog.LeafGreen,
        UnovaSequelEncounterCatalog.BlackTwo,
        UnovaSequelEncounterCatalog.WhiteTwo);

    /// <summary>All of the above, for a composition root that wants the lot.</summary>
    public static GameDataSources All { get; } = new(Learnsets, Evolutions, BaseStats);
}
