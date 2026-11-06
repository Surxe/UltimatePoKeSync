namespace UltimatePoKeSync.GameData;

/// <summary>
/// Gen 4 ability identifiers whose effects alter aggregate type effectiveness.
/// </summary>
/// <remarks>
/// Ability numbers do not change between generations, so these carry the same values as
/// the Gen 3 and Gen 5 ones. What differs is the list. Motor Drive, Dry Skin, Heatproof,
/// Filter and Solid Rock arrive in Gen 4. Lightning Rod and Storm Drain exist but are left
/// out on purpose: in Gen 4 they only redirect a move to their holder, and become
/// immunities in Gen 5. Sap Sipper does not exist yet. See D-070.
/// </remarks>
public static class Gen4AbilityIds
{
    public const int VoltAbsorb = 10;
    public const int WaterAbsorb = 11;
    public const int FlashFire = 18;
    public const int WonderGuard = 25;
    public const int Levitate = 26;
    public const int ThickFat = 47;
    public const int MotorDrive = 78;
    public const int Heatproof = 85;
    public const int DrySkin = 87;
    public const int Filter = 111;
    public const int SolidRock = 116;
}
