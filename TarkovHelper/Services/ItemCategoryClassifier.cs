namespace TarkovHelper.Services;

/// <summary>
/// Maps tarkov.dev's detailed item category hierarchy to the canonical
/// user-facing categories used by the Items tab.
/// </summary>
public static class ItemCategoryClassifier
{
    public const string RangeSubmission = "RangeSubmission";

    private static readonly HashSet<string> MagazineCategories = Set(
        "Magazine", "Cylinder Magazine", "Spring Driven Cylinder", "Magazines");

    private static readonly HashSet<string> AmmunitionCategories = Set(
        "Ammo", "Ammo container", "Rocket", "Rounds", "Ammo boxes", "Shrapnel", "Ammunition");

    private static readonly HashSet<string> MedicalCategories = Set(
        "Medikit", "Medkits", "Medical item", "Medical supplies", "Injury treatment",
        "Stimulant", "Stimulants", "Drug", "Drugs", "Meds", "Medical");

    private static readonly HashSet<string> FoodCategories = Set(
        "Food", "Drink", "Drinks", "Food and drink");

    private static readonly HashSet<string> MeleeCategories = Set(
        "Knife", "Melee", "Melee weapons");

    private static readonly HashSet<string> GrenadeCategories = Set(
        "Throwable weapon", "Volumetric Throw Weapon", "Grenades", "Throwables", "Special grenades");

    private static readonly HashSet<string> EyewearCategories = Set(
        "Vis. observ. device", "Night Vision", "Thermal Vision", "Eyewear");

    private static readonly HashSet<string> WeaponCategories = Set(
        "Weapon", "Weapons", "Assault rifle", "Assault carbine", "Machinegun", "SMG",
        "Handgun", "Revolver", "Shotgun", "Sniper rifle", "Marksman rifle",
        "Grenade launcher", "Rocket Launcher");

    private static readonly HashSet<string> PartCategories = Set(
        "Weapon mod", "Gear mod", "Functional mod", "Sights", "Parts", "Mount", "Mounts",
        "Stock", "Stocks & chassis", "Handguard", "Handguards", "Barrel", "Barrels",
        "Flashhider", "Flash hiders & muzzle brakes", "Silencer", "Suppressors",
        "Comb. muzzle device", "Muzzle adapters", "Ironsight", "Iron sights", "Pistol grip",
        "Pistol grips", "Receiver", "Receivers and slides", "Charging handle", "Charging handles",
        "Gas block", "Gas blocks", "Foregrip", "Foregrips", "Auxiliary Mod", "Auxiliary parts",
        "Bipod", "Bipods", "UBGL", "Underbarrel grenade launchers", "Scope", "Scopes",
        "Assault scope", "Assault scopes", "Reflex sight", "Reflex sights",
        "Compact reflex sight", "Compact reflex sights", "Special scope", "Night vision scopes",
        "Thermal vision sights", "Flashlight", "Flashlights", "Comb. tact. device",
        "Tactical combo devices", "Helmet mods");

    private static readonly HashSet<string> BarterCategories = Set(
        "Barter item", "Barter", "Electronics", "Building material", "Building materials",
        "Flammable materials", "Energy elements", "Household goods", "Tool", "Tools",
        "Jewelry", "Valuables", "Battery", "Fuel", "Lubricant");

    private static readonly HashSet<string> RigCategories = Set(
        "Chest rig", "Chest rigs", "Backpack", "Backpacks", "Rigs");

    private static readonly HashSet<string> ContainerCategories = Set(
        "Common container", "Port. container", "Locking container", "Random Loot Container",
        "Containers & cases", "Secure containers", "Containers");

    private static readonly HashSet<string> ArmorCategories = Set(
        "Armor", "Armor Plate", "Armor plates", "Armor vests", "Armored equipment",
        "Headwear", "Face Cover", "Face cover", "Headphones", "Earpieces");

    private static readonly HashSet<string> InfoCategories = Set(
        "Info", "Info items", "Dialog Item", "Notes", "Tapes", "Flyer", "Map", "Maps",
        "Extraction intel", "Dogtag");

    private static readonly HashSet<string> KeyCategories = Set(
        "Key", "Keys", "Mechanical Key", "Keycard", "Keycards");

    public static string Classify(
        string? primaryCategory,
        IEnumerable<string>? categoryHierarchy = null,
        bool isRangeSubmission = false)
    {
        if (isRangeSubmission)
            return RangeSubmission;

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(values, primaryCategory);
        if (categoryHierarchy != null)
        {
            foreach (var category in categoryHierarchy)
                Add(values, category);
        }

        if (Overlaps(values, MagazineCategories)) return "Magazines";
        if (Overlaps(values, AmmunitionCategories)) return "Ammunition";
        if (Overlaps(values, MedicalCategories)) return "Medical";
        if (Overlaps(values, FoodCategories)) return "Food";
        if (Overlaps(values, MeleeCategories)) return "Melee";
        if (Overlaps(values, GrenadeCategories)) return "Grenades";
        if (Overlaps(values, EyewearCategories)) return "Eyewear";
        if (Overlaps(values, WeaponCategories)) return "Weapons";
        if (Overlaps(values, PartCategories)) return "Parts";
        if (Overlaps(values, BarterCategories)) return "Barter";
        if (Overlaps(values, RigCategories)) return "Rigs";
        if (Overlaps(values, ContainerCategories)) return "Containers";
        if (Overlaps(values, ArmorCategories)) return "Armor";
        if (Overlaps(values, InfoCategories)) return "Info";
        if (Overlaps(values, KeyCategories)) return "Keys";
        return "Special";
    }

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);

    private static bool Overlaps(HashSet<string> values, HashSet<string> categorySet) =>
        values.Overlaps(categorySet);

    private static void Add(ISet<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (var part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            values.Add(part);
    }
}
