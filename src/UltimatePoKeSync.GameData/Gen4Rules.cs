using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.GameData;

/// <summary>Pokémon HeartGold and SoulSilver battle rules. See D-070.</summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="Gen5Rules"/>, because Gen 4 is where the split it is built around
/// begins: <b>the physical/special split is per move</b>, so a Bite runs off Attack here as it
/// does in Black. What differs from Gen 5 is the move table, 467 moves with Gen 4's own powers
/// (Tackle 35, Giga Drain 60), and two abilities: Lightning Rod and Storm Drain only redirect
/// a move in Gen 4, so they are not immunities here.
/// </para>
/// <para>
/// The type chart, the natures and the stat formula are unchanged since Gen 3. They are
/// declared per generation rather than shared, as D-041 did for Gen 5, so a number can
/// always be traced to the generation it belongs to.
/// </para>
/// </remarks>
public sealed class Gen4Rules : IGenerationRules
{
    /// <summary>Gen 4 ends at Shadow Force.</summary>
    private const int LastMove = 467;

    /// <summary>
    /// Damaging moves whose battle scripts do not deal ordinary type-scaled damage, so they
    /// cover nothing however good the matchup looks. The Gen 3 list plus what Gen 4 added.
    /// </summary>
    private static readonly HashSet<int> NonCoverageMoveIds =
    [
        12,  // Guillotine
        32,  // Horn Drill
        49,  // Sonic Boom
        68,  // Counter
        69,  // Seismic Toss
        82,  // Dragon Rage
        90,  // Fissure
        101, // Night Shade
        117, // Bide
        149, // Psywave
        162, // Super Fang
        243, // Mirror Coat
        283, // Endeavor
        329, // Sheer Cold
        368, // Metal Burst ignores ordinary type-scaled damage
    ];

    private readonly int[] _basePowers;
    private readonly MoveCategory[] _categories;
    private readonly NatureInfo[] _natures;

    private Gen4Rules()
    {
        var chartData = EmbeddedJson.Load<TypeChartData>("gen4-type-chart.json");
        TypeChart = new TableTypeChart(chartData, PokemonGeneration.Gen4);

        var moveData = EmbeddedJson.Load<Gen4MoveData>("gen4-move-power.json");
        if (moveData.Generation != 4 ||
            moveData.BasePowers.Length != LastMove + 1 ||
            moveData.Categories.Length != LastMove + 1 ||
            moveData.BasePowers.Any(power => power is < 0 or > 255))
        {
            throw new InvalidOperationException("The embedded Gen 4 move data is malformed.");
        }

        _basePowers = moveData.BasePowers;
        _categories = [.. moveData.Categories.Select(ParseCategory)];

        var natureData = EmbeddedJson.Load<NatureData>("gen4-natures.json");
        _natures = NatureTable.Parse(natureData, PokemonGeneration.Gen4);
        Natures = Array.AsReadOnly(_natures);
    }

    public static Gen4Rules Instance { get; } = new();

    public PokemonGeneration Generation => PokemonGeneration.Gen4;

    public ITypeChart TypeChart { get; }

    public IReadOnlyList<NatureInfo> Natures { get; }

    public NatureInfo GetNature(int natureId)
    {
        if ((uint)natureId >= _natures.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(natureId));
        }

        return _natures[natureId];
    }

    /// <summary>
    /// The move's own category, read from the table. The type is accepted for the shape of
    /// the interface and checked, but it does not decide anything here, which is the whole
    /// difference between this generation and Gen 3.
    /// </summary>
    public MoveCategory GetMoveCategory(int moveId, PokemonType moveType)
    {
        // The id is settled first, because an empty move slot carries no type at all and
        // asking whether None is a Gen 4 type is the wrong question to answer with an
        // exception. Gen 3 orders it the same way, for the same reason.
        if (moveId <= 0 || moveId > LastMove)
        {
            return MoveCategory.Status;
        }

        if (!TypeChart.Types.Contains(moveType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveType), moveType, "The move type is not available in Gen 4.");
        }

        return _categories[moveId];
    }

    public int GetMoveBasePower(int moveId) =>
        moveId > 0 && moveId <= LastMove ? _basePowers[moveId] : 0;

    public bool CanProvideSuperEffectiveCoverage(int moveId) =>
        moveId > 0 && moveId <= LastMove
        && _categories[moveId] != MoveCategory.Status
        && !NonCoverageMoveIds.Contains(moveId);

    public StatBlock CalculateStats(
        int level,
        StatBlock baseStats,
        StatBlock individualValues,
        StatBlock effortValues,
        int natureId) =>
        ModernStatFormula.Calculate(
            level, baseStats, individualValues, effortValues, GetNature(natureId));

    public double GetDefensiveMultiplier(
        PokemonType attackingType,
        PokemonType primaryType,
        PokemonType secondaryType,
        int abilityId)
    {
        double multiplier = TypeChart.GetMultiplier(attackingType, primaryType, secondaryType);

        // Gen 4 added Motor Drive and Dry Skin to the abilities that turn a hit into
        // nothing. Lightning Rod and Storm Drain are deliberately absent: here they only
        // redirect the move to their holder, which still takes it. They become immunities
        // in Gen 5, and this is the one place the two rule sets genuinely disagree. D-070.
        bool immune = abilityId switch
        {
            Gen4AbilityIds.Levitate => attackingType == PokemonType.Ground,
            Gen4AbilityIds.FlashFire => attackingType == PokemonType.Fire,
            Gen4AbilityIds.VoltAbsorb or Gen4AbilityIds.MotorDrive =>
                attackingType == PokemonType.Electric,
            Gen4AbilityIds.WaterAbsorb or Gen4AbilityIds.DrySkin =>
                attackingType == PokemonType.Water,
            _ => false,
        };

        if (immune)
        {
            return 0;
        }

        // Wonder Guard lets through only net super-effective damage: a 2x and a 0.5x
        // component cancel, and the game treats that as blocked.
        if (abilityId == Gen4AbilityIds.WonderGuard && multiplier <= 1)
        {
            return 0;
        }

        if (abilityId == Gen4AbilityIds.ThickFat &&
            attackingType is PokemonType.Fire or PokemonType.Ice)
        {
            multiplier *= 0.5;
        }

        if (abilityId == Gen4AbilityIds.Heatproof && attackingType == PokemonType.Fire)
        {
            multiplier *= 0.5;
        }

        // Dry Skin is the mirror of Thick Fat: it trades a Water immunity for taking more
        // from Fire.
        if (abilityId == Gen4AbilityIds.DrySkin && attackingType == PokemonType.Fire)
        {
            multiplier *= 1.25;
        }

        // Filter and Solid Rock blunt what is already super effective, and only that.
        if (abilityId is Gen4AbilityIds.Filter or Gen4AbilityIds.SolidRock && multiplier > 1)
        {
            multiplier *= 0.75;
        }

        return multiplier;
    }

    private static MoveCategory ParseCategory(string value) => value switch
    {
        "Physical" => MoveCategory.Physical,
        "Special" => MoveCategory.Special,
        "Status" => MoveCategory.Status,
        _ => throw new InvalidOperationException($"Unknown move category in the Gen 4 data: {value}"),
    };
}

internal sealed record Gen4MoveData(int Generation, int[] BasePowers, string[] Categories);
