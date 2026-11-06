namespace UltimatePoKeSync.Providers.MelonDs;

/// <summary>
/// Where a Gen 4 game keeps its party, per game code. See D-067.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="Gen5MemoryMap"/>, and different from it in the two ways D-067
/// measured. The pointer leads to the game's general save block rather than to the party,
/// so the party sits at a fixed offset past where it points. And a Gen 4 party slot is 236
/// bytes, the <c>PK4</c> party form, rather than 220.
/// </para>
/// <para>
/// Following the pointer is required here, not a courtesy as it is for Black: SoulSilver's
/// party moved by a few bytes on every boot it was measured across, and the pointer led to it
/// each time. The cartridge header, the party header of capacity and count, and the rule that
/// an unknown code is refused rather than guessed at are all shared with Gen 5.
/// </para>
/// </remarks>
public sealed record Gen4MemoryMap(string GameCode, string Name, uint SaveBlockPointer, uint PartyOffset)
{
    /// <summary>The <c>PK4</c> party form: 136 stored bytes and 100 of battle stats.</summary>
    public const int SlotSize = 236;

    /// <summary>Only the codes verified against a running cartridge, as for Gen 5.</summary>
    private static readonly Dictionary<string, Gen4MemoryMap> Known = new(StringComparer.Ordinal)
    {
        // Verified live on 2026-11-06 across three boots of the same save, with the party
        // head at a different address each time and the pointer leading to it every time.
        // See D-067.
        ["IPGE"] = new("IPGE", "SoulSilver (English)", 0x0211186C, 0xD080),
    };

    public static Gen4MemoryMap? For(string gameCode) =>
        Known.TryGetValue(gameCode, out Gen4MemoryMap? map) ? map : null;

    /// <summary>Every Gen 4 cartridge that has been measured, named for the setup screen.</summary>
    public static IReadOnlyList<string> Mapped { get; } =
    [
        .. Known.Values.Select(map => map.Name).Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Whether a code belongs to Gen 4 at all: Diamond, Pearl, Platinum, HeartGold or
    /// SoulSilver, in any region. Used to name the generation of a cartridge the app cannot
    /// read yet, rather than calling it Gen 5.
    /// </summary>
    public static bool IsGen4(string gameCode) =>
        gameCode.Length == 4
        && gameCode[..3] is "ADA" or "APA" or "CPU" or "IPK" or "IPG";
}
