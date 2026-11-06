using UltimatePoKeSync.App.Services;
using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.App.Tests;

/// <summary>
/// What two emulators watched at once report as one state. See D-042.
/// </summary>
/// <remarks>
/// This is the bug the window found on its first run with melonDS: a Snivy was arriving
/// every second and the screen said the connection had been lost, because the mGBA provider
/// that nobody had opened kept announcing its retries on top of the one that was working.
/// </remarks>
public sealed class LiveTeamServiceTests
{
    [Theory]
    // One streaming beats anything the other one is doing.
    [InlineData(EmulatorConnectionState.Reconnecting, EmulatorConnectionState.Streaming,
        EmulatorConnectionState.Streaming)]
    [InlineData(EmulatorConnectionState.Streaming, EmulatorConnectionState.Reconnecting,
        EmulatorConnectionState.Streaming)]
    [InlineData(EmulatorConnectionState.Faulted, EmulatorConnectionState.Streaming,
        EmulatorConnectionState.Streaming)]

    // With neither streaming, the more hopeful of the two is what the window shows.
    [InlineData(EmulatorConnectionState.Reconnecting, EmulatorConnectionState.Connecting,
        EmulatorConnectionState.Connecting)]
    [InlineData(EmulatorConnectionState.Idle, EmulatorConnectionState.Reconnecting,
        EmulatorConnectionState.Reconnecting)]

    // Connected but on a game we cannot read is better news than not connected at all.
    [InlineData(EmulatorConnectionState.Reconnecting, EmulatorConnectionState.ConnectedNoGame,
        EmulatorConnectionState.ConnectedNoGame)]
    public void TheBetterOfTheTwoIsWhatIsReported(
        EmulatorConnectionState first,
        EmulatorConnectionState second,
        EmulatorConnectionState expected)
    {
        Assert.Equal(expected, Better(first, second));
    }

    /// <summary>
    /// The ordering has to be spelled out because the enum is declared in lifecycle order:
    /// Idle is 0 and Streaming is 3, so taking the smallest value would report a provider
    /// that never started over one that is sending a team.
    /// </summary>
    [Fact]
    public void TheEnumsOwnOrderIsNotTheOrderThatMatters()
    {
        Assert.True(EmulatorConnectionState.Idle < EmulatorConnectionState.Streaming);
        Assert.Equal(
            EmulatorConnectionState.Streaming,
            Better(EmulatorConnectionState.Idle, EmulatorConnectionState.Streaming));
    }

    /// <summary>The service's own ranking, not a copy of it.</summary>
    private static EmulatorConnectionState Better(
        EmulatorConnectionState first,
        EmulatorConnectionState second) =>
        new[] { first, second }.MinBy(LiveTeamService.Rank);

    /// <summary>
    /// The melonDS port override is a contributor's tool, so a bad value costs the override
    /// and nothing else. See D-066.
    /// </summary>
    [Theory]
    [InlineData("3434", 3434)]
    [InlineData(" 3434 ", 3434)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("melon", null)]
    [InlineData("0", null)]
    [InlineData("70000", null)]
    public void AMelonDsPortComesFromTheEnvironmentOnlyWhenItIsAPort(string? value, int? expected) =>
        Assert.Equal(expected, LiveTeamService.ParseMelonDsPort(value));
}
