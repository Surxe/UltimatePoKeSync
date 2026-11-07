using PKHeX.Core;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Parsing;

/// <summary>
/// Turns the fields a provider read out of a Gen 4 battle into what the Battle tab shows,
/// attaching what a Pokédex says about the species. See D-076.
/// </summary>
/// <remarks>
/// The battle type bits are the ones in pret/pokeheartgold <c>include/constants/battle.h</c>.
/// A read that does not make sense (a species past Arceus, a level past 100, more HP than the
/// maximum) is refused rather than shown, as with the party: battle records are rewritten
/// between frames too, and the caller holds the last good one.
/// </remarks>
public sealed class Gen4BattleDecoder
{
    private const int Language = 2;
    private const int Generation = 4;
    private const ushort MaxGen4Species = 493;

    public const uint BattleTypeTrainer = 1 << 0;
    public const uint BattleTypeDoubles = 1 << 1;
    public const uint BattleTypeSafari = 1 << 5;
    public const uint BattleTypePalPark = 1 << 9;
    public const uint BattleTypeTutorial = 1 << 10;
    public const uint BattleTypeBugContest = 1 << 12;

    /// <summary>Battles where a Poké, Great or Ultra Ball cannot be thrown at a wild Pokémon.</summary>
    private const uint NoStandardBalls =
        BattleTypeTrainer | BattleTypeSafari | BattleTypePalPark | BattleTypeTutorial | BattleTypeBugContest;

    private readonly IPersonalTable _personal = PersonalTable.HGSS;

    public static bool CanDecode(GameIdentity game) => game.GameCode is "IPGE";

    /// <summary>The battle, or null when the read does not make sense and should be ignored.</summary>
    public BattleSnapshot? Decode(RawBattleSnapshot raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (!raw.InBattle)
        {
            return BattleSnapshot.None with { Game = raw.Game, CapturedAt = raw.CapturedAt };
        }

        if ((raw.BattleTypeFlags & BattleTypeDoubles) != 0)
        {
            // Recognised, not read: a later decision (D-075).
            return new BattleSnapshot(raw.Game, BattleKind.Double, false, null, null, raw.CapturedAt);
        }

        if (raw.Opponent is not RawBattler opponent || !Plausible(opponent))
        {
            return null;
        }

        // The player's battler can be empty for a moment, between a faint and the next
        // send-out; the opponent is still worth showing then.
        uint? player = raw.Player is RawBattler mine && Plausible(mine) ? mine.Personality : null;

        return new BattleSnapshot(
            raw.Game,
            BattleKind.Single,
            IsWild: (raw.BattleTypeFlags & NoStandardBalls) == 0,
            ToOpponent(opponent),
            player,
            raw.CapturedAt);
    }

    private OpponentSnapshot ToOpponent(RawBattler battler)
    {
        PersonalInfo info = _personal.GetFormEntry(battler.SpeciesId, (byte)battler.Form);
        var primary = (PokemonType)info.Type1;
        var secondary = info.Type2 == info.Type1 ? PokemonType.None : (PokemonType)info.Type2;

        return new OpponentSnapshot(
            battler.SpeciesId,
            SpeciesName.GetSpeciesNameGeneration(battler.SpeciesId, Language, Generation),
            battler.Form,
            battler.Level,
            primary,
            secondary,
            info.CatchRate,
            battler.CurrentHp,
            battler.MaxHp,
            StatusByte.Read((int)(battler.StatusFlags & 0xFF)));
    }

    private static bool Plausible(RawBattler battler) =>
        battler.SpeciesId is > 0 and <= MaxGen4Species
        && battler.Level is >= 1 and <= 100
        && battler.MaxHp is > 0 and < 1000
        && battler.CurrentHp >= 0
        && battler.CurrentHp <= battler.MaxHp;
}
