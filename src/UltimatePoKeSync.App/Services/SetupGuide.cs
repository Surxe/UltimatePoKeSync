using System.Diagnostics;

using UltimatePoKeSync.Providers.MelonDs;

namespace UltimatePoKeSync.App.Services;

/// <summary>
/// The setup instructions shown until the emulator connects.
/// </summary>
/// <remarks>
/// <para>
/// mGBA has no command-line option to load a script, so this step cannot be automated
/// away, only made short and unambiguous. See D-028.
/// </para>
/// <para>
/// The script is copied to a stable per-user folder rather than shown where the executable
/// happens to live. On macOS an app opened straight from Downloads runs from a randomised
/// read-only copy under <c>/private/var/folders/…/AppTranslocation/…</c>, and handing that
/// path to a user is useless. See D-029.
/// </para>
/// </remarks>
public static class SetupGuide
{
    private const string ScriptName = "ups_bridge.lua";

    public static string PlatformName =>
        OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : "this system";

    /// <summary>A folder that stays put across updates and is never translocated.</summary>
    public static string ScriptDirectory
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string root = OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support")
                : OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                        ? xdg
                        : Path.Combine(home, ".local", "share");

            return Path.Combine(root, "UltimatePoKeSync");
        }
    }

    public static string ScriptPath => Path.Combine(ScriptDirectory, ScriptName);

    public static bool ScriptExists => File.Exists(ScriptPath);

    /// <summary>
    /// macOS is running the app from a temporary copy because it was opened without ever
    /// being moved. Worth saying out loud: it also makes the app forget nothing, but every
    /// path it reports is a throwaway one.
    /// </summary>
    public static bool IsTranslocated =>
        AppContext.BaseDirectory.Contains("/AppTranslocation/", StringComparison.Ordinal);

    public static string RevealButtonText =>
        OperatingSystem.IsMacOS() ? "Show in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer"
        : "Open the folder";

    /// <summary>
    /// Puts the bridge script where the setup screen says it is. Overwrites an older copy,
    /// so updating the app updates the script.
    /// </summary>
    public static void EnsureScript()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "emulator-scripts", ScriptName);
        if (!File.Exists(shipped))
        {
            return;
        }

        Directory.CreateDirectory(ScriptDirectory);

        if (File.Exists(ScriptPath) &&
            File.ReadAllBytes(ScriptPath).AsSpan().SequenceEqual(File.ReadAllBytes(shipped)))
        {
            return;
        }

        File.Copy(shipped, ScriptPath, overwrite: true);
    }

    /// <summary>Opens the file manager with the script selected, or its folder shown.</summary>
    public static void RevealScript()
    {
        if (!ScriptExists)
        {
            return;
        }

        (string command, string arguments) = OperatingSystem.IsMacOS()
            ? ("open", $"-R \"{ScriptPath}\"")
            : OperatingSystem.IsWindows()
                ? ("explorer.exe", $"/select,\"{ScriptPath}\"")
                : ("xdg-open", $"\"{ScriptDirectory}\"");

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(command, arguments)
            {
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or FileNotFoundException or InvalidOperationException)
        {
            // No file manager, or none we know how to call. The path is on screen and
            // copyable, so this is a convenience, not the only way through.
        }
    }

    public static IReadOnlyList<string> Steps(int port) =>
    [
        OperatingSystem.IsMacOS()
            ? "Move UltimatePoKeSync to your Applications folder, then open it from there. "
                + "Opened straight from Downloads, macOS runs it from a temporary copy."
            : OperatingSystem.IsWindows()
                ? "Install mGBA 0.10.5 or later and open it from the Start menu."
                : "Install mGBA 0.10.5 or later from your package manager and open it.",
        OperatingSystem.IsMacOS()
            ? "Install mGBA 0.10.5 or later and open it from Applications."
            : "Load your Gen 3 ROM: File → Load ROM.",
        OperatingSystem.IsMacOS()
            ? "Load your Gen 3 ROM: File → Load ROM."
            : "Open Tools → Scripting… then File → Load script…",
        OperatingSystem.IsMacOS()
            ? "Open Tools → Scripting… → File → Load script…, then press ⇧⌘G and paste the "
                + "path below."
            : "Pick the script at the path below.",
        $"Keep playing. This window connects on port {port} by itself.",
    ];

    /// <summary>
    /// The other way in, for the DS games. melonDS has no scripting worth the name, so there
    /// is no file to load: it answers a debugger instead, and the app speaks that. See D-039.
    /// </summary>
    public static IReadOnlyList<string> DsSteps() =>
    [
        "Install melonDS 1.1 or later and open your Gen 5 ROM.",
        "Open Config → Emu settings and tick \"Enable GDB stub\".",
        "Leave the JIT recompiler off. melonDS ships that way, and the debugger only "
            + "answers when it is off.",
        "Restart the game so the setting takes effect, then keep playing. Nothing else to "
            + "load: this window connects on port 3334 by itself.",
    ];

    /// <summary>
    /// Which DS cartridges the app can actually read, and why it is a short list.
    /// </summary>
    /// <remarks>
    /// A Game Boy Advance game is found through addresses cross-checked against three
    /// independent sources (D-013), so language costs nothing there. A DS game is not: every
    /// cartridge keeps its party somewhere its own, and D-061 proved the addresses follow no
    /// pattern worth extrapolating from. Each one has to be measured against a running game,
    /// so the supported set is the set somebody has run. See D-063.
    /// </remarks>
    public static string DsRomNote =>
        "English ROMs are the supported ones for DS games. Every cartridge keeps its party at "
        + "its own address, so each has to be measured against a running game. Measured so far: "
        + string.Join(
            ", ",
            Gen4MemoryMap.Mapped.Concat(Gen5MemoryMap.Mapped).Order(StringComparer.Ordinal))
        + ". Any other language is recognised and refused rather than read at a guessed address.";

    /// <summary>Shown when the port is busy, which is nearly always a second mGBA.</summary>
    public static string PortHelp(int port) =>
        $"Nothing yet on port {port}. If mGBA is running with the script loaded, check its "
        + "scripting console for an error, and make sure no second copy of mGBA is holding "
        + "the port.";

    public static string TranslocationWarning =>
        "macOS is running this app from a temporary copy, because it was opened without "
        + "being moved first. Quit it, drag UltimatePoKeSync into Applications, and open it "
        + "from there.";
}
