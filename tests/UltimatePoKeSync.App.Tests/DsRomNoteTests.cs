using UltimatePoKeSync.App.Services;
using UltimatePoKeSync.Providers.MelonDs;
using Xunit;

namespace UltimatePoKeSync.App.Tests;

/// <summary>
/// The setup screen's list of readable DS cartridges, which is built from the memory map
/// rather than repeated beside it. See D-063.
/// </summary>
public sealed class DsRomNoteTests
{
    [Fact]
    public void EveryMappedCartridgeIsNamedOnTheSetupScreen()
    {
        Assert.NotEmpty(Gen5MemoryMap.Mapped);
        Assert.All(
            Gen5MemoryMap.Mapped,
            name => Assert.Contains(name, SetupGuide.DsRomNote, StringComparison.Ordinal));
    }

    /// <summary>The Gen 4 cartridge is named alongside the Gen 5 ones. See D-067.</summary>
    [Fact]
    public void TheMeasuredGen4CartridgeIsNamedToo()
    {
        Assert.NotEmpty(Gen4MemoryMap.Mapped);
        Assert.All(
            Gen4MemoryMap.Mapped,
            name => Assert.Contains(name, SetupGuide.DsRomNote, StringComparison.Ordinal));
        Assert.DoesNotContain("HeartGold", SetupGuide.DsRomNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// And nothing unmeasured is named. A note that lists a cartridge the app cannot read
    /// sends somebody to load a game that will produce silence.
    /// </summary>
    [Fact]
    public void NothingUnmeasuredIsPromised()
    {
        Assert.Null(Gen5MemoryMap.For("IREI"));
        Assert.DoesNotContain("(Italy)", SetupGuide.DsRomNote.Replace("Black (Italy)", "")
            .Replace("White (Italy)", ""), StringComparison.Ordinal);
    }

    /// <summary>The screen says which languages are the supported path, because it is asked.</summary>
    [Fact]
    public void TheSupportedLanguageIsStated() =>
        Assert.Contains("English", SetupGuide.DsRomNote, StringComparison.Ordinal);
}
