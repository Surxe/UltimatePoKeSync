using UltimatePoKeSync.Contracts;
using Xunit;

namespace UltimatePoKeSync.Analysis.Tests;

/// <summary>
/// Pins the HeartGold/SoulSilver capture steps (D-077). The expected thresholds were worked
/// out by hand from pret/pokeheartgold's <c>BattleSystem_CalculateBallShakes</c>, integer
/// division at every step, so a change in the order of operations shows up here.
/// </summary>
public sealed class CatchChanceCalculatorTests
{
    [Theory]
    // Catch rate 255 (Geodude) at full HP in a Poké Ball: a = 85, threshold 49931.
    [InlineData(255, 30, 30, StatusCondition.None, StandardBall.PokeBall, 49931)]
    // Catch rate 45 at 1 HP, asleep, Ultra Ball: a = 89 * 2 = 178, threshold 61680.
    [InlineData(45, 1, 100, StatusCondition.Sleep, StandardBall.UltraBall, 61680)]
    // Catch rate 3 at full HP in a Poké Ball: a truncates to 1, threshold 16643.
    [InlineData(3, 50, 50, StatusCondition.None, StandardBall.PokeBall, 16643)]
    // Catch rate 45 at half HP, paralysed, Great Ball: a = 44 * 1.5 = 66, threshold 47661.
    [InlineData(45, 50, 100, StatusCondition.Paralysis, StandardBall.GreatBall, 47661)]
    // Catch rate 190 at 20/25 HP in a Poké Ball: a = 88, threshold 52428.
    [InlineData(190, 20, 25, StatusCondition.None, StandardBall.PokeBall, 52428)]
    public void Matches_the_games_integer_steps(
        int catchRate, int hp, int maxHp, StatusCondition status, StandardBall ball, int threshold)
    {
        double chance = CatchChanceCalculator.Chance(catchRate, hp, maxHp, status, ball);

        Assert.Equal(Math.Pow(threshold / 65536.0, 4), chance, 12);
    }

    [Fact]
    public void A_modified_rate_of_255_or_more_always_catches()
    {
        Assert.Equal(1, CatchChanceCalculator.Chance(255, 1, 100, StatusCondition.None, StandardBall.UltraBall));
    }

    [Theory]
    [InlineData(StatusCondition.Freeze)]
    [InlineData(StatusCondition.Sleep)]
    public void Sleep_and_freeze_double_it(StatusCondition status)
    {
        double healthy = CatchChanceCalculator.Chance(45, 100, 100, StatusCondition.None, StandardBall.PokeBall);
        double ailing = CatchChanceCalculator.Chance(45, 100, 100, status, StandardBall.PokeBall);

        Assert.True(ailing > healthy);
    }

    [Fact]
    public void Better_balls_and_lower_hp_never_do_worse()
    {
        for (int hp = 100; hp >= 1; hp -= 9)
        {
            double poke = CatchChanceCalculator.Chance(45, hp, 100, StatusCondition.None, StandardBall.PokeBall);
            double great = CatchChanceCalculator.Chance(45, hp, 100, StatusCondition.None, StandardBall.GreatBall);
            double ultra = CatchChanceCalculator.Chance(45, hp, 100, StatusCondition.None, StandardBall.UltraBall);

            Assert.True(poke <= great && great <= ultra, $"at {hp} HP");
        }

        double full = CatchChanceCalculator.Chance(45, 100, 100, StatusCondition.None, StandardBall.PokeBall);
        double low = CatchChanceCalculator.Chance(45, 1, 100, StatusCondition.None, StandardBall.PokeBall);
        Assert.True(low > full);
    }

    [Fact]
    public void Rates_all_three_standard_balls_for_an_opponent()
    {
        var geodude = new OpponentSnapshot(
            74, "GEODUDE", 0, 10, PokemonType.Rock, PokemonType.Ground, 255, 30, 30, StatusCondition.None);

        IReadOnlyList<CatchChance> chances = CatchChanceCalculator.ForStandardBalls(geodude);

        Assert.Equal(
            [StandardBall.PokeBall, StandardBall.GreatBall, StandardBall.UltraBall],
            chances.Select(chance => chance.Ball));
        Assert.Equal(Math.Pow(49931 / 65536.0, 4), chances[0].Chance, 12);
    }
}
