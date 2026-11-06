namespace UltimatePoKeSync.Providers.MelonDs.Tests;

/// <summary>
/// A stand-in for main RAM with SoulSilver in it: a cartridge header, the static pointer to
/// the save block, and a party at the measured offset past it. See D-067.
/// </summary>
/// <remarks>
/// Synthetic on purpose: no capture of a real save is committed, so the bytes are laid out
/// at the real addresses but say nothing about anyone's game.
/// </remarks>
internal static class Gen4Ram
{
    public const uint SaveBlockPointer = 0x0211186C;

    public const uint PartyOffset = 0xD080;

    /// <summary>One of the three save block addresses measured across boots.</summary>
    public const uint SaveBlock = 0x0226F284;

    public const uint PartyHead = SaveBlock + PartyOffset;

    public const byte FirstSlotMarker = 0x4A;

    public const byte LastByteMarker = 0xB7;

    public static byte[] Build(
        string gameCode = "IPGE",
        int partyCount = 6,
        uint? saveBlock = null)
    {
        var ram = new byte[Gen5Ram.Size];

        Gen5Ram.Header(gameCode, "POKEMON SS", revision: 0)
            .CopyTo(ram, (int)(Gen5MemoryMap.CartridgeHeader - Gen5Ram.BaseAddress));

        uint block = saveBlock ?? SaveBlock;
        BitConverter.GetBytes(block).CopyTo(ram, (int)(SaveBlockPointer - Gen5Ram.BaseAddress));

        long head = (long)block + PartyOffset - Gen5Ram.BaseAddress;
        if (head is >= 0 and < Gen5Ram.Size - 2048)
        {
            int at = (int)head;
            BitConverter.GetBytes(Gen5MemoryMap.SlotCapacity).CopyTo(ram, at);
            BitConverter.GetBytes(partyCount).CopyTo(ram, at + Gen5MemoryMap.PartyCountOffset);

            int slots = at + Gen5MemoryMap.FirstSlotOffset;
            ram[slots] = FirstSlotMarker;

            // The very last byte of the sixth slot, so a read one slot size short of 236
            // bytes is caught rather than passed.
            ram[slots + (Gen4MemoryMap.SlotSize * Gen5MemoryMap.SlotCapacity) - 1] = LastByteMarker;
        }

        return ram;
    }
}
