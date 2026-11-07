namespace UltimatePoKeSync.Contracts;

/// <summary>
/// What a battle shows on screen, read live. See D-075.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. It carries what the player can see in the battle (the opponent's
/// species, level, HP bar and status) and what a Pokédex would say about that species
/// (types, catch rate), and nothing else. The opponent's moves, item, ability, stats and the
/// rest of a trainer's party are not here, so nothing past the decoder can show them. That
/// is the fairness rule of D-075, enforced by the shape of this record rather than by the UI.
/// </para>
/// <para>
/// <see cref="None"/> is the normal state outside a battle.
/// </para>
/// </remarks>
/// <param name="Game">Source ROM.</param>
/// <param name="Kind">Whether a battle is running, and how many fight on each side.</param>
/// <param name="IsWild">A wild battle, where a ball can be thrown.</param>
/// <param name="Opponent">The opponent on the field, or null when there is none.</param>
/// <param name="PlayerActivePersonality">
/// The personality value of the player's Pokémon on the field, to find it in the party;
/// slot order is not trusted, because a switch in battle reorders the battle's copy of it.
/// </param>
/// <param name="CapturedAt">When the bytes were read.</param>
public sealed record BattleSnapshot(
    GameIdentity Game,
    BattleKind Kind,
    bool IsWild,
    OpponentSnapshot? Opponent,
    uint? PlayerActivePersonality,
    DateTimeOffset CapturedAt)
{
    public static BattleSnapshot None { get; } =
        new(GameIdentity.Unknown, BattleKind.None, false, null, null, DateTimeOffset.MinValue);

    public bool IsRunning => Kind != BattleKind.None;
}

public enum BattleKind
{
    /// <summary>No battle running.</summary>
    None = 0,

    /// <summary>One Pokémon on each side.</summary>
    Single = 1,

    /// <summary>Two on each side. Recognised but not read yet; see D-075.</summary>
    Double = 2,
}

/// <summary>
/// The opponent's Pokémon on the field: only what the screen and a Pokédex show. See D-075.
/// </summary>
/// <param name="SpeciesId">National dex number.</param>
/// <param name="SpeciesName">Species name, as the game spells it.</param>
/// <param name="Form">Form index (Unown letter, Rotom appliance, ...), 0 for most.</param>
/// <param name="Level">Shown next to its name.</param>
/// <param name="PrimaryType">Pokédex type.</param>
/// <param name="SecondaryType">Second Pokédex type, or <see cref="PokemonType.None"/>.</param>
/// <param name="CatchRate">The species' catch rate, 3 to 255 (Pokédex data).</param>
/// <param name="CurrentHp">
/// Hit points left. The screen shows them only as a bar; the exact value is used for the catch
/// chance, and the UI shows a bar.
/// </param>
/// <param name="MaxHp">Its maximum hit points.</param>
/// <param name="Status">Shown as a badge next to its HP bar.</param>
public sealed record OpponentSnapshot(
    ushort SpeciesId,
    string SpeciesName,
    int Form,
    int Level,
    PokemonType PrimaryType,
    PokemonType SecondaryType,
    int CatchRate,
    int CurrentHp,
    int MaxHp,
    StatusCondition Status)
{
    public bool IsFainted => CurrentHp <= 0;

    public double HpFraction => MaxHp <= 0 ? 0 : Math.Clamp((double)CurrentHp / MaxHp, 0, 1);
}
