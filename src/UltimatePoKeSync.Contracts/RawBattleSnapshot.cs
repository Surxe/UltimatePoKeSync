namespace UltimatePoKeSync.Contracts;

/// <summary>
/// The battle fields an emulator provider read, before any species data is attached. See
/// D-075 and D-076.
/// </summary>
/// <remarks>
/// The provider reads a battler's record in one request, because the game rewrites it between
/// frames (D-072), and copies only these fields out of it. The opponent's moves, stats, item and
/// ability are in those bytes and are dropped there, so nothing past the provider has them.
/// </remarks>
/// <param name="Game">Source ROM.</param>
/// <param name="InBattle">A battle is running; everything below is empty when not.</param>
/// <param name="BattleTypeFlags">The game's battle type bits (trainer, doubles, safari...).</param>
/// <param name="Player">The player's battler on the field.</param>
/// <param name="Opponent">The opponent's battler on the field.</param>
/// <param name="CapturedAt">When the bytes were read.</param>
public sealed record RawBattleSnapshot(
    GameIdentity Game,
    bool InBattle,
    uint BattleTypeFlags,
    RawBattler? Player,
    RawBattler? Opponent,
    DateTimeOffset CapturedAt);

/// <param name="SpeciesId">National dex number, 0 when the slot is empty.</param>
/// <param name="Form">Form index.</param>
/// <param name="Level">Level.</param>
/// <param name="CurrentHp">Hit points left.</param>
/// <param name="MaxHp">Maximum hit points.</param>
/// <param name="StatusFlags">The status byte layout shared by Gen 3 to 5.</param>
/// <param name="Personality">Personality value, to match the player's battler to the party.</param>
public sealed record RawBattler(
    ushort SpeciesId,
    int Form,
    int Level,
    int CurrentHp,
    int MaxHp,
    uint StatusFlags,
    uint Personality);

/// <summary>
/// A provider that can read the battle on the field, on demand. Separate from
/// <see cref="IEmulatorProvider"/> for the reason of D-033: not every emulator or game can,
/// and nothing that only wants the party should care.
/// </summary>
public interface IBattleReader
{
    /// <summary>
    /// The battle on the field, with <see cref="RawBattleSnapshot.InBattle"/> false outside one;
    /// <see langword="null"/> when the game has no battle map or the emulator could not answer,
    /// which is not the same as "no battle" and should not clear what is on screen.
    /// </summary>
    Task<RawBattleSnapshot?> ReadBattleAsync(CancellationToken cancellationToken = default);
}
