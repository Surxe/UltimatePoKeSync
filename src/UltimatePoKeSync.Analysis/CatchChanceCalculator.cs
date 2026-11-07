using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Analysis;

/// <summary>The three balls every shop sells, which is what the Battle tab rates.</summary>
public enum StandardBall
{
    PokeBall = 0,
    GreatBall = 1,
    UltraBall = 2,
}

/// <param name="Ball">The ball thrown.</param>
/// <param name="Chance">Probability of a catch with one throw, 0 to 1.</param>
public sealed record CatchChance(StandardBall Ball, double Chance);

/// <summary>
/// The chance that one ball catches the opponent, as Gen 4 computes it. See D-077.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of <c>BattleSystem_CalculateBallShakes</c> in the HeartGold/SoulSilver
/// decompilation (pret/pokeheartgold, <c>src/battle/battle_command.c</c>), integer steps and
/// their order included: the game multiplies the catch rate by the ball first, divides by 10,
/// then applies the HP fraction, and the truncation at each step changes the result at low
/// catch rates. Then four shake checks, each passing when a 16-bit random number is below
/// <c>0xFFFF0 / isqrt(isqrt(0xFF0000 / a))</c>.
/// </para>
/// <para>
/// Only the Poké, Great and Ultra Balls: their multipliers do not depend on anything the
/// app does not read (turn count, time of day, terrain, the Pokédex), unlike Dusk, Timer or
/// Repeat. Gen 3 uses the same steps (pokeemerald), so this is not Gen 4 specific in
/// substance, but it is only wired up for Gen 4.
/// </para>
/// </remarks>
public static class CatchChanceCalculator
{
    /// <summary>Shake checks a ball must pass to catch. <c>BALL_SHAKE_MAX</c>.</summary>
    private const int ShakeChecks = 4;

    /// <summary>The game's random numbers here are 16-bit.</summary>
    private const double RandomRange = 65536;

    public static IReadOnlyList<CatchChance> ForStandardBalls(OpponentSnapshot opponent)
    {
        ArgumentNullException.ThrowIfNull(opponent);

        return
        [
            .. Enum.GetValues<StandardBall>().Select(ball => new CatchChance(
                ball,
                Chance(opponent.CatchRate, opponent.CurrentHp, opponent.MaxHp, opponent.Status, ball))),
        ];
    }

    /// <summary>Probability that one throw of <paramref name="ball"/> catches.</summary>
    public static double Chance(
        int catchRate,
        int currentHp,
        int maxHp,
        StatusCondition status,
        StandardBall ball)
    {
        if (maxHp <= 0 || catchRate <= 0)
        {
            return 0;
        }

        uint modified = ModifiedCatchRate(catchRate, Math.Clamp(currentHp, 0, maxHp), maxHp, status, ball);
        if (modified >= 255)
        {
            return 1;
        }

        if (modified == 0)
        {
            return 0;
        }

        uint shakeProbability = ShakeProbability(modified);
        double perShake = Math.Min(1, shakeProbability / RandomRange);
        return Math.Pow(perShake, ShakeChecks);
    }

    /// <summary>The game's <c>modifiedCatchRate</c>, "a" in most write-ups.</summary>
    internal static uint ModifiedCatchRate(
        int catchRate,
        int currentHp,
        int maxHp,
        StatusCondition status,
        StandardBall ball)
    {
        uint ballMultiplier = ball switch
        {
            StandardBall.PokeBall => 10,
            StandardBall.GreatBall => 15,
            StandardBall.UltraBall => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(ball)),
        };

        uint maxHpTimes3 = (uint)maxHp * 3;
        uint lostHp = maxHpTimes3 - ((uint)currentHp * 2);
        uint baseCatchMultiplier = (uint)Math.Min(catchRate, 255) * ballMultiplier;
        uint modified = ((baseCatchMultiplier / 10) * lostHp) / maxHpTimes3;

        if (status is StatusCondition.Sleep or StatusCondition.Freeze)
        {
            modified *= 2;
        }

        if (status is StatusCondition.Burn or StatusCondition.Paralysis
            or StatusCondition.Poison or StatusCondition.BadPoison)
        {
            modified = (modified * 15) / 10;
        }

        return modified;
    }

    /// <summary>The per-shake threshold a 16-bit random number has to stay under.</summary>
    internal static uint ShakeProbability(uint modifiedCatchRate) =>
        0xFFFF0u / IntegerSqrt(IntegerSqrt(0xFF0000u / modifiedCatchRate));

    /// <summary>Floor of the square root, as the DS's hardware square-root unit returns it.</summary>
    internal static uint IntegerSqrt(uint value)
    {
        uint root = (uint)Math.Sqrt(value);
        while ((ulong)root * root > value)
        {
            root--;
        }

        while ((ulong)(root + 1) * (root + 1) <= value)
        {
            root++;
        }

        return root;
    }
}
