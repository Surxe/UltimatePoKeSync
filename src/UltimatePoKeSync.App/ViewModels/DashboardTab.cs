namespace UltimatePoKeSync.App.ViewModels;

/// <summary>
/// The eight screens of the dashboard. Switching one changes the content area only: the
/// header and the party rail never move. See D-047.
/// </summary>
public enum DashboardTab
{
    Pokemon = 0,
    Stats = 1,
    Build = 2,
    Learnset = 3,
    Team = 4,
    TeamHints = 5,
    Bridge = 6,
    Battle = 7,
}
