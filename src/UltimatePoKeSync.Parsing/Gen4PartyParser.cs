using PKHeX.Core;
using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Parsing;

/// <summary>
/// Interprets raw Gen 4 (Nintendo DS) bytes using PKHeX. See D-067 and D-069.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="Gen5PartyParser"/>, and deliberately the same shape: the job,
/// the refusals and the domain model are identical. <see cref="PK4"/> takes the 236 bytes
/// exactly as they sit in RAM, which was checked against a running SoulSilver: the party
/// decoded from memory matched the one PKHeX reads out of the same cartridge's save file,
/// field for field.
/// </para>
/// <para>
/// What carries over from Gen 5 rather than from Gen 3: nature is stored, not derived from
/// the personality value, and the physical/special split belongs to the move, which the
/// rules above decide. What differs is the size of the slot, the species ceiling of 493, and
/// the tables, read per game from PKHeX's HeartGold/SoulSilver data.
/// </para>
/// </remarks>
public sealed class Gen4PartyParser : IPartyParser
{
    /// <summary>PKHeX language code. 2 = English.</summary>
    private const int Language = 2;

    private const int Generation = 4;

    /// <summary>Last Gen 4 species (Arceus). Beyond that, the data is garbage.</summary>
    private const ushort MaxGen4Species = 493;

    private readonly GameStrings _strings = GameInfo.GetStrings("en");

    public bool CanParse(GameIdentity game) =>
        game.Generation == PokemonGeneration.Gen4 && ResolvePersonalTable(game.GameCode) is not null;

    public PartySnapshot Parse(RawPartySnapshot raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        IPersonalTable? personal = ResolvePersonalTable(raw.Game.GameCode);
        if (personal is null)
        {
            return new PartySnapshot(
                raw.Game, [], raw.CapturedAt, raw.Sequence,
                [new RejectedSlot(-1, $"game not supported by the Gen 4 parser: {raw.Game.GameCode}")]);
        }

        var members = new List<PokemonSnapshot>(raw.SlotCapacity);
        var rejected = new List<RejectedSlot>();

        // Slots past the declared count are not examined, for the reason of D-019: the game
        // does not wipe a slot when a Pokemon leaves, so the leftovers there still decode
        // into a complete, checksum-valid Pokemon that is no longer on the team.
        int usableSlots = Math.Min(raw.PartyCount, raw.SlotCapacity);

        for (int slot = 0; slot < usableSlots; slot++)
        {
            PK4 pk;
            try
            {
                pk = new PK4(raw.GetSlot(slot).ToArray());
            }
            catch (Exception)
            {
                rejected.Add(new RejectedSlot(slot, "bytes could not be read as a Gen 4 Pokemon"));
                continue;
            }

            if (pk.Species == 0)
            {
                rejected.Add(new RejectedSlot(slot, "empty slot but counted in the party"));
                continue;
            }

            if (!pk.ChecksumValid)
            {
                // Usually a read caught mid-write. If it becomes frequent, the address is
                // wrong and this is what says so instead of inventing a Pokemon. See D-008.
                rejected.Add(new RejectedSlot(slot, "invalid checksum"));
                continue;
            }

            if (pk.Species > MaxGen4Species)
            {
                rejected.Add(new RejectedSlot(slot, $"species out of range for Gen 4: {pk.Species}"));
                continue;
            }

            if (!BattleStatsAgree(pk))
            {
                // The game decrypts a record's battle stats in place while it works on them,
                // and leaves them that way for a while without setting any flag a reader could
                // check. Decrypting them a second time is how a Feraligatr becomes level 159
                // and asleep. If they make sense read as they stand, that is what they are;
                // if neither reading does, the slot is not shown. See D-073.
                byte[] inTheClear = raw.GetSlot(slot).ToArray();
                ScrambleBattleStats(inTheClear);
                var retried = new PK4(inTheClear);

                if (!retried.ChecksumValid || !BattleStatsAgree(retried))
                {
                    rejected.Add(new RejectedSlot(slot, "battle stats could not be read"));
                    continue;
                }

                pk = retried;
            }

            members.Add(ToSnapshot(slot, pk, personal));
        }

        return new PartySnapshot(raw.Game, members, raw.CapturedAt, raw.Sequence, rejected);
    }

    private PokemonSnapshot ToSnapshot(int slot, PK4 pk, IPersonalTable personal)
    {
        // The interface is held rather than the concrete HeartGold/SoulSilver table, so the
        // Sinnoh games can add their own table here without touching this method.
        PersonalInfo info = personal.GetFormEntry(pk.Species, 0);

        var primary = (PokemonType)info.Type1;
        var secondary = info.Type2 == info.Type1 ? PokemonType.None : (PokemonType)info.Type2;

        return new PokemonSnapshot
        {
            SlotIndex = slot,
            SpeciesId = pk.Species,
            SpeciesName = SpeciesName.GetSpeciesNameGeneration(pk.Species, Language, Generation),
            Nickname = pk.Nickname,
            Level = pk.Stat_Level,
            PrimaryType = primary,
            SecondaryType = secondary,
            BaseStats = new StatBlock(info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE),
            IndividualValues = new StatBlock(
                pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE),
            EffortValues = new StatBlock(
                pk.EV_HP, pk.EV_ATK, pk.EV_DEF, pk.EV_SPA, pk.EV_SPD, pk.EV_SPE),
            CurrentStats = new StatBlock(
                pk.Stat_HPMax, pk.Stat_ATK, pk.Stat_DEF, pk.Stat_SPA, pk.Stat_SPD, pk.Stat_SPE),

            // As in Gen 5, PKHeX exposes the nature as a field. In Gen 4 it is still the
            // personality value modulo 25, so the two cannot disagree; reading the field keeps
            // the shape of the Gen 5 parser.
            NatureId = (int)pk.Nature,
            NatureName = Lookup(_strings.Natures, (int)pk.Nature, "?"),

            AbilityId = pk.Ability,
            AbilityName = Lookup(_strings.Ability, pk.Ability, "?"),
            HeldItemId = pk.HeldItem,
            HeldItemName = pk.HeldItem == 0 ? "-" : Lookup(_strings.Item, pk.HeldItem, "?"),
            Moves = ReadMoves(pk),
            IsEgg = pk.IsEgg,
            IsShiny = pk.IsShiny,
            PersonalityValue = pk.PID,
            Gender = (PokemonGender)pk.Gender,
            CurrentHp = pk.Stat_HPCurrent,
            Status = StatusByte.Read(pk.Status_Condition),
            Friendship = pk.CurrentFriendship,
            Experience = pk.EXP,
        };
    }

    private IReadOnlyList<MoveSlot> ReadMoves(PK4 pk)
    {
        var moves = new List<MoveSlot>(4);
        ReadOnlySpan<ushort> ids = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
        ReadOnlySpan<int> currentPp = [pk.Move1_PP, pk.Move2_PP, pk.Move3_PP, pk.Move4_PP];
        ReadOnlySpan<int> ppUps = [pk.Move1_PPUps, pk.Move2_PPUps, pk.Move3_PPUps, pk.Move4_PPUps];

        for (int i = 0; i < 4; i++)
        {
            ushort id = ids[i];
            if (id == 0)
            {
                moves.Add(MoveSlot.Empty);
                continue;
            }

            int basePp = MoveInfo.GetPP(EntityContext.Gen4, id);

            // Each PP Up adds 20% of the base PP, truncated.
            int maxPp = basePp + (basePp / 5 * ppUps[i]);

            moves.Add(new MoveSlot(
                id,
                Lookup(_strings.Move, id, "?"),
                (PokemonType)MoveInfo.GetType(id, EntityContext.Gen4),
                currentPp[i],
                maxPp));
        }

        return moves;
    }

    /// <summary>
    /// Whether the battle stats belong to this record, checked against what the stored part
    /// implies: the level its experience gives, HP not above its maximum, a status the game
    /// can actually hold, and each of the six stats inside the range the formula allows for
    /// that species, level, IVs and nature. See D-073.
    /// </summary>
    /// <remarks>
    /// A range rather than one number, because Gen 4 recalculates stats only on level up:
    /// effort values gained since then are already in the stored part and not yet in the
    /// stats, and a berry can lower them in the meantime. So each stat is bounded by what it
    /// would be with no effort values and with the most a stat can hold. A block caught half
    /// re-encrypted would have to land inside six such ranges at once, a few points wide each.
    /// </remarks>
    private static bool BattleStatsAgree(PK4 pk)
    {
        if (pk.Stat_Level != Experience.GetLevel(pk.EXP, pk.PersonalInfo.EXPGrowth)
            || pk.Stat_HPMax == 0
            || pk.Stat_HPCurrent > pk.Stat_HPMax
            || !IsGen4Status(pk.Status_Condition))
        {
            return false;
        }

        ushort[] lowest = StatsWithEffort(pk, 0);
        ushort[] highest = StatsWithEffort(pk, MaxEffortPerStat);

        // PKHeX orders stats HP, Attack, Defense, Speed, Sp. Atk, Sp. Def.
        ReadOnlySpan<int> actual =
            [pk.Stat_HPMax, pk.Stat_ATK, pk.Stat_DEF, pk.Stat_SPE, pk.Stat_SPA, pk.Stat_SPD];

        for (int stat = 0; stat < actual.Length; stat++)
        {
            if (actual[stat] < lowest[stat] || actual[stat] > highest[stat])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gen 4 caps a single stat's effort values at 255.</summary>
    private const int MaxEffortPerStat = 255;

    private static ushort[] StatsWithEffort(PK4 pk, int effort)
    {
        var probe = (PK4)pk.Clone();
        probe.EV_HP = probe.EV_ATK = probe.EV_DEF = effort;
        probe.EV_SPA = probe.EV_SPD = probe.EV_SPE = effort;
        return probe.GetStats(probe.PersonalInfo);
    }

    /// <summary>
    /// The low byte holds at most one condition: sleep as a turn count in bits 0 to 2, or
    /// exactly one of poison, burn, freeze, paralysis and bad poison in bits 3 to 7. Nothing
    /// lives above it.
    /// </summary>
    private static bool IsGen4Status(int status)
    {
        if ((status & ~0xFF) != 0)
        {
            return false;
        }

        int sleep = status & 0x07;
        int others = status & 0xF8;
        return others == 0 || (sleep == 0 && (others & (others - 1)) == 0);
    }

    /// <summary>
    /// Applies the battle stats' own stream cipher to bytes 136 to 235: the game's LCRNG seeded
    /// with the personality value, XORed over each 16-bit word. XOR makes it its own inverse,
    /// so this turns stats the game left in the clear back into the form <see cref="PK4"/>
    /// expects, and PKHeX then undoes it.
    /// </summary>
    private static void ScrambleBattleStats(byte[] slot)
    {
        const int start = 136;
        uint seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slot);

        for (int offset = start; offset + 1 < slot.Length; offset += 2)
        {
            seed = unchecked((seed * 0x41C64E6D) + 0x6073);
            ushort word = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(slot.AsSpan(offset));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                slot.AsSpan(offset), (ushort)(word ^ (seed >> 16)));
        }
    }

    private static string Lookup(IReadOnlyList<string> table, int index, string fallback) =>
        index >= 0 && index < table.Count ? table[index] : fallback;

    /// <summary>
    /// HeartGold and SoulSilver share a table. Every regional code is listed rather than
    /// matched by prefix, so an unknown one is refused instead of being guessed at; the
    /// memory map is what decides which of them can actually be read. Only IPGE has been run
    /// against a real cartridge. See D-067.
    /// </summary>
    private static IPersonalTable? ResolvePersonalTable(string gameCode) => gameCode switch
    {
        // HeartGold, then SoulSilver. Verified live: IPGE.
        "IPKE" or "IPKP" or "IPKJ" or "IPKD" or "IPKF" or "IPKI" or "IPKS" or "IPKK" =>
            PersonalTable.HGSS,
        "IPGE" or "IPGP" or "IPGJ" or "IPGD" or "IPGF" or "IPGI" or "IPGS" or "IPGK" =>
            PersonalTable.HGSS,

        _ => null,
    };
}
