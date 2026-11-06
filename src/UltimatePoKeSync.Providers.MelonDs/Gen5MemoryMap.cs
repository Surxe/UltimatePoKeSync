using System.Text;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Providers.MelonDs;

/// <summary>
/// Where a Gen 5 game keeps the things we read, per game code. See D-040.
/// </summary>
/// <remarks>
/// <para>
/// The equivalent of the Lua script's game table (D-005), on the other side of the wire.
/// One entry per game code, and an unrecognised code is refused rather than guessed at.
/// The whole reason that rule exists is that D-035 found an Italian cartridge keeping its
/// party sixteen bytes away from where the American one does.
/// </para>
/// <para>
/// The party is not at a fixed address here: what is recorded is the entry in the game's own
/// table of save blocks, and the address is read from there. Every Gen 5 cartridge run so far
/// kept its party at the same place across restarts, so the pointer is not strictly needed
/// today: it is followed because it is the route the game itself takes, and it costs four
/// bytes.
/// </para>
/// </remarks>
public sealed record Gen5MemoryMap(string GameCode, string Name, uint PartyPointer)
{
    /// <summary>
    /// The cartridge header, which the DS firmware copies into main RAM. The console has
    /// 4 MB mirrored across the region, so the documented 0x027FFE00 is read here.
    /// </summary>
    public const uint CartridgeHeader = 0x023FFE00;

    /// <summary>Title at +0 (12 bytes), game code at +0x0C (4), revision at +0x1E.</summary>
    public const int HeaderLength = 32;

    /// <summary>A party is a header of two words and then six slots.</summary>
    public const int PartyCountOffset = 4;

    public const int FirstSlotOffset = 8;

    public const int SlotSize = 220;

    public const int SlotCapacity = 6;

    /// <summary>
    /// Only the codes verified against a running cartridge. Every other Gen 5 code is left
    /// out on purpose: the app can then say "this is White, and it is not mapped yet" instead
    /// of reading a plausible-looking address and inventing a team out of it.
    /// </summary>
    /// <remarks>
    /// Five cartridges, five addresses. The four originals sit within <c>0x120</c> of each
    /// other and look like they follow a rule: language moving the party by <c>0x100</c> and
    /// version by <c>0x20</c>. Black 2 then lands nowhere near them, pointer and head both,
    /// which is the argument against ever having used that rule. Each code is measured. See
    /// D-054 and D-061.
    /// </remarks>
    private static readonly Dictionary<string, Gen5MemoryMap> Known = new(StringComparer.Ordinal)
    {
        // Verified live against a real cartridge on 2026-08-12. See D-040.
        ["IRBI"] = new("IRBI", "Black (Italy)", 0x0224F88C),

        // Verified live on 2026-08-14, the same way: one pointer in all of main RAM leads
        // here, and the parser read the Tepig the player confirmed carrying. See D-054.
        ["IRAO"] = new("IRAO", "White (English)", 0x0224F9AC),

        // Verified live on 2026-08-14. Found in seconds rather than by scanning, because by
        // then the directory's neighbourhood was known: a Tepig at Lv.5, confirmed.
        ["IRAI"] = new("IRAI", "White (Italy)", 0x0224F8AC),

        // Verified live on 2026-08-14, an Oshawott at Lv.5. See D-054.
        ["IRBO"] = new("IRBO", "Black (English)", 0x0224F98C),

        // Verified live on 2026-08-15, a Snivy at Lv.5. The sequels lay their heap out
        // differently enough that the directory is not where the originals keep it, and this
        // one had to be scanned for rather than looked up. See D-061.
        ["IREO"] = new("IREO", "Black 2 (English)", 0x0223B4C4),

        // Verified live on 2026-08-15, an Oshawott at Lv.5. Sits 0x40 past its own version's
        // pointer, which is a smaller gap than any pair among the originals and still not a
        // rule: it is one more measurement. See D-064.
        ["IRDO"] = new("IRDO", "White 2 (English)", 0x0223B504),
    };

    public static Gen5MemoryMap? For(string gameCode) =>
        Known.TryGetValue(gameCode, out Gen5MemoryMap? map) ? map : null;

    /// <summary>
    /// Every cartridge that has been measured, named the way a player would name it. The
    /// setup screen builds its sentence from this rather than repeating it, so the list on
    /// screen cannot fall behind the list in the code. See D-063.
    /// </summary>
    public static IReadOnlyList<string> Mapped { get; } =
    [
        .. Known.Values.Select(map => map.Name).Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Whether a code belongs to Gen 5 at all. Used to tell "a DS Pokémon game we have not
    /// mapped" apart from "some other DS game entirely", which are different things to say
    /// to someone waiting for their team to appear.
    /// </summary>
    public static bool IsGen5(string gameCode) =>
        gameCode.Length == 4
        && gameCode.StartsWith("IR", StringComparison.Ordinal)
        && gameCode[2] is 'A' or 'B' or 'D' or 'E';

    /// <summary>Reads the identity out of the cartridge header sitting in main RAM.</summary>
    public static GameIdentity? ReadIdentity(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
        {
            return null;
        }

        string title = Encoding.ASCII.GetString(header[..12]).TrimEnd('\0', ' ');
        string code = Encoding.ASCII.GetString(header.Slice(0x0C, 4));

        // A header that is not ASCII is a header we did not find: the game has not booted
        // far enough yet, or the address is wrong.
        if (!code.All(char.IsLetterOrDigit))
        {
            return null;
        }

        // The header is where every DS game says who it is, so Gen 4 is told apart here too.
        // Anything else keeps the Gen 5 label it always had. See D-067.
        PokemonGeneration generation = Gen4MemoryMap.IsGen4(code)
            ? PokemonGeneration.Gen4
            : PokemonGeneration.Gen5;

        return new GameIdentity(code, title, header[0x1E], generation);
    }
}
