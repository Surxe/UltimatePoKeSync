namespace UltimatePoKeSync.Providers.MelonDs;

/// <summary>
/// Where a Gen 4 game keeps the battle on the field, per game code. See D-076.
/// </summary>
/// <remarks>
/// <para>
/// The battle lives on the heap and moves between boots, so it is reached through the game's
/// VBlank callback slot: a function pointer and the argument passed to it, at a fixed address.
/// While a battle runs, the function is the battle's own VBlank handler and the argument is the
/// <c>BattleSystem</c>; anywhere else the slot holds another screen's handler and data. So the
/// handler's address is both the "is a battle running" test and the key to the pointer.
/// </para>
/// <para>
/// The structure offsets are pret/pokeheartgold's (<c>include/battle/battle.h</c>), confirmed
/// against a running cartridge: <c>BattleSystem.battleType</c> at 0x2C and <c>.ctx</c> at 0x30,
/// the four <c>BattleMon</c> records 0xC0 apart at <c>ctx + 0x2D40</c>, battler 0 the player's
/// and battler 1 the opponent's in a single battle.
/// </para>
/// </remarks>
public sealed record Gen4BattleMap(string GameCode, uint VBlankSlot, uint BattleVBlankHandler)
{
    public const int BattleTypeOffset = 0x2C;
    public const int ContextOffset = 0x30;
    public const int BattleMonsOffset = 0x2D40;
    public const int BattleMonSize = 0xC0;

    /// <summary>Bytes of a <c>BattleMon</c> read: up to and including <c>status</c>.</summary>
    public const int BattleMonReadLength = 0x70;

    public const int SpeciesOffset = 0x00;
    public const int FormOffset = 0x26;
    public const int LevelOffset = 0x34;
    public const int HpOffset = 0x4C;
    public const int MaxHpOffset = 0x50;
    public const int PersonalityOffset = 0x68;
    public const int StatusOffset = 0x6C;

    private static readonly Dictionary<string, Gen4BattleMap> Known = new(StringComparer.Ordinal)
    {
        // Verified live on 2026-11-06 across fresh boots of the same save: the BattleSystem
        // moved between boots, the handler did not, and the pointer led to it each time.
        // Out of battle the slot held the field's handler (0x021E5901). See D-076.
        ["IPGE"] = new("IPGE", 0x021D110C, 0x02239731),
    };

    public static Gen4BattleMap? For(string gameCode) =>
        Known.TryGetValue(gameCode, out Gen4BattleMap? map) ? map : null;
}
