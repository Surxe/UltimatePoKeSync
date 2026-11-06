using PKHeX.Core;
using UltimatePoKeSync.Contracts;
using Xunit;
using StatusCondition = UltimatePoKeSync.Contracts.StatusCondition;

namespace UltimatePoKeSync.Parsing.Tests;

/// <summary>
/// The Gen 4 parser against parties built with PKHeX and encrypted the way the game holds
/// them in RAM. See D-069.
/// </summary>
/// <remarks>
/// Synthetic rather than captured: no real save is committed (D-069). What makes these
/// trustworthy anyway is D-067's check that <c>PK4</c> decodes a running SoulSilver's RAM
/// to exactly what its save file says, so the parser is exercised on bytes of the same form.
/// </remarks>
public sealed class Gen4PartyParserTests
{
    private static readonly GameIdentity SoulSilver =
        new("IPGE", "POKEMON SS", 0, PokemonGeneration.Gen4);

    private readonly Gen4PartyParser _parser = new();

    [Fact]
    public void AnEncryptedPartyComesBackWhole()
    {
        PokemonSnapshot dragonite = Assert.Single(_parser.Parse(Party(Dragonite())).Members);

        Assert.Equal(149, dragonite.SpeciesId);
        // The name the game itself uses: Gen 4, like Gen 3, prints species in capitals, and
        // the Gen 3 parser already reports "TREECKO" the same way.
        Assert.Equal("DRAGONITE", dragonite.SpeciesName);
        Assert.Equal("DRAKE", dragonite.Nickname);
        Assert.Equal(58, dragonite.Level);
        Assert.Equal(PokemonType.Dragon, dragonite.PrimaryType);
        Assert.Equal(PokemonType.Flying, dragonite.SecondaryType);

        Assert.Equal(new StatBlock(11, 6, 29, 21, 26, 10), dragonite.IndividualValues);
        Assert.Equal(new StatBlock(65, 119, 41, 104, 26, 155), dragonite.EffortValues);
        Assert.Equal(143, dragonite.CurrentHp);
        Assert.Equal(189, dragonite.CurrentStats.Hp);

        Assert.Equal("Inner Focus", dragonite.AbilityName);
        Assert.Equal("Amulet Coin", dragonite.HeldItemName);
        Assert.False(dragonite.IsEgg);
    }

    /// <summary>
    /// Gen 4 is where a move's category stopped following its type, and a few move types and
    /// PP changed after it, so the move table has to be Gen 4's.
    /// </summary>
    [Fact]
    public void MovesAreNamedTypedAndCountedFromGen4Tables()
    {
        PokemonSnapshot dragonite = Assert.Single(_parser.Parse(Party(Dragonite())).Members);

        MoveSlot dracoMeteor = Assert.Single(dragonite.Moves, move => move.Name == "Draco Meteor");
        Assert.Equal(PokemonType.Dragon, dracoMeteor.Type);
        Assert.Equal(5, dracoMeteor.MaxPp);
        Assert.Equal(0, dracoMeteor.CurrentPp);

        // Two PP Ups on Iron Tail: 15 base, plus 3 each.
        MoveSlot ironTail = Assert.Single(dragonite.Moves, move => move.Name == "Iron Tail");
        Assert.Equal(21, ironTail.MaxPp);
    }

    /// <summary>Gen 4's nature is the personality value modulo 25, as in Gen 3.</summary>
    [Fact]
    public void NatureFollowsThePersonalityValue()
    {
        PokemonSnapshot dragonite = Assert.Single(_parser.Parse(Party(Dragonite())).Members);

        Assert.Equal((int)(dragonite.PersonalityValue % 25), dragonite.NatureId);
        Assert.Equal("Brave", dragonite.NatureName);
    }

    [Fact]
    public void AStatusFromBattleIsRead()
    {
        PK4 poisoned = Sandshrew();
        poisoned.Status_Condition = 1 << 3;

        PokemonSnapshot sandshrew = Assert.Single(_parser.Parse(Party(poisoned)).Members);

        Assert.Equal(StatusCondition.Poison, sandshrew.Status);
    }

    [Fact]
    public void AnEggIsReadAsAnEgg()
    {
        PK4 egg = Sandshrew();
        egg.IsEgg = true;
        egg.RefreshChecksum();

        Assert.True(Assert.Single(_parser.Parse(Party(egg)).Members).IsEgg);
    }

    /// <summary>
    /// Leftover bytes past the declared count decode as a complete Pokémon in real RAM, so
    /// they must never be looked at (D-019).
    /// </summary>
    [Fact]
    public void SlotsPastTheDeclaredCountAreNotRead()
    {
        PartySnapshot party = _parser.Parse(Party(1, Dragonite(), Sandshrew()));

        Assert.Single(party.Members);
        Assert.Empty(party.RejectedSlots);
    }

    [Fact]
    public void ABrokenChecksumIsRejectedRatherThanGuessedAt()
    {
        RawPartySnapshot raw = Party(Dragonite());
        byte[] data = raw.PartyData.ToArray();
        data[40] ^= 0xFF;

        PartySnapshot party = _parser.Parse(raw with { PartyData = data });

        Assert.Empty(party.Members);
        Assert.Contains(party.RejectedSlots, slot => slot.Reason == "invalid checksum");
    }

    [Fact]
    public void OnlyHeartGoldAndSoulSilverAreParsed()
    {
        Assert.True(_parser.CanParse(SoulSilver));
        Assert.True(_parser.CanParse(SoulSilver with { GameCode = "IPKE" }));

        // Platinum is Gen 4 but has its own tables, which nothing here reads yet.
        Assert.False(_parser.CanParse(SoulSilver with { GameCode = "CPUE" }));
        Assert.False(_parser.CanParse(SoulSilver with { Generation = PokemonGeneration.Gen5 }));
        Assert.False(new Gen5PartyParser().CanParse(SoulSilver));
    }

    [Fact]
    public void TheResolverPicksTheGen4ParserForSoulSilver()
    {
        IPartyParser? chosen = PartyParserResolver.CreateDefault().Resolve(SoulSilver);

        Assert.IsType<Gen4PartyParser>(chosen);
        Assert.Single(chosen.Parse(Party(Dragonite())).Members);
    }

    private static PK4 Dragonite()
    {
        var pk = new PK4
        {
            Species = 149,
            PID = 0x54C6_4D5D, // 1422282077 % 25 = 2, Brave
            TID16 = 4242,
            SID16 = 1717,
            EXP = 253966,
            Ability = 39,      // Inner Focus
            HeldItem = 223,    // Amulet Coin
            IV_HP = 11, IV_ATK = 6, IV_DEF = 29, IV_SPA = 21, IV_SPD = 26, IV_SPE = 10,
            EV_HP = 65, EV_ATK = 119, EV_DEF = 41, EV_SPA = 104, EV_SPD = 26, EV_SPE = 155,
            Move1 = 231, Move2 = 434, Move3 = 349, Move4 = 219, // Iron Tail, Draco Meteor, Dragon Dance, Safeguard
            Move1_PP = 5, Move2_PP = 0, Move3_PP = 20, Move4_PP = 25,
            Move1_PPUps = 2,
            Nickname = "DRAKE",
            OriginalTrainerName = "TEST",
            Stat_Level = 58,
            Stat_HPCurrent = 143,
            Stat_HPMax = 189,
        };
        pk.RefreshChecksum();
        return pk;
    }

    private static PK4 Sandshrew()
    {
        var pk = new PK4
        {
            Species = 27,
            PID = 0x0BADF00D,
            TID16 = 1,
            SID16 = 2,
            Ability = 8, // Sand Veil
            Move1 = 10,
            Move1_PP = 35,
            Nickname = "SANDY",
            OriginalTrainerName = "TEST",
            Stat_Level = 15,
            Stat_HPCurrent = 42,
            Stat_HPMax = 42,
        };
        pk.RefreshChecksum();
        return pk;
    }

    private static RawPartySnapshot Party(params PK4[] party) => Party(party.Length, party);

    /// <summary>Six 236-byte slots, each encrypted as the game holds it; the rest zeroed.</summary>
    private static RawPartySnapshot Party(int declaredCount, params PK4[] party)
    {
        const int slotSize = 236;
        var blob = new byte[slotSize * 6];

        for (int i = 0; i < party.Length; i++)
        {
            byte[] slot = party[i].Data.ToArray();
            Assert.Equal(slotSize, slot.Length);
            PokeCrypto.Encrypt45(slot);
            slot.CopyTo(blob, i * slotSize);
        }

        return new RawPartySnapshot(
            SoulSilver, declaredCount, blob, slotSize, DateTimeOffset.UnixEpoch, 1);
    }
}
