namespace TarkovHelper.Services;

/// <summary>
/// Canonical ordering for user-facing filters. Unknown future values are kept
/// after the known entries and sorted by their localized display name.
/// </summary>
public static class UiSortOrder
{
    private static readonly IReadOnlyDictionary<string, int> TraderRanks = BuildRanks(
        "prapor",
        "therapist",
        "fence",
        "skier",
        "peacekeeper",
        "mechanic",
        "ragman",
        "jaeger",
        "ref",
        "lightkeeper",
        "btrdriver");

    private static readonly IReadOnlyDictionary<string, int> MapRanks = BuildRanks(
        "customs",
        "shoreline",
        "thelabyrinth",
        "icebreaker",
        "factory",
        "woods",
        "interchange",
        "thelab",
        "reserve",
        "lighthouse",
        "streetsoftarkov",
        "groundzero",
        "terminal");

    public static IReadOnlyList<string> ItemCategories { get; } =
    [
        "Weapons",
        "Magazines",
        "Ammunition",
        "Medical",
        "Food",
        "Melee",
        "Parts",
        "Grenades",
        "Barter",
        "Rigs",
        "Eyewear",
        "Containers",
        "Armor",
        "Info",
        "Keys",
        "Special",
        ItemCategoryClassifier.RangeSubmission
    ];

    private static readonly IReadOnlyDictionary<string, int> CategoryRanks =
        BuildRanks(ItemCategories.ToArray());

    public static int GetTraderRank(string? trader)
    {
        var key = Normalize(trader);
        return TraderRanks.TryGetValue(key, out var rank) ? rank : int.MaxValue;
    }

    public static int GetMapRank(string? map)
    {
        var key = Normalize(map) switch
        {
            "streets" or "streetsoftarkov" => "streetsoftarkov",
            "thelabs" or "labs" or "lab" => "thelab",
            "labyrinth" => "thelabyrinth",
            "icebreakerterminal" => "icebreaker",
            _ => Normalize(map)
        };

        return MapRanks.TryGetValue(key, out var rank) ? rank : int.MaxValue;
    }

    public static int GetItemCategoryRank(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return int.MaxValue;

        return CategoryRanks.TryGetValue(category.Trim(), out var rank)
            ? rank
            : int.MaxValue;
    }

    private static IReadOnlyDictionary<string, int> BuildRanks(params string[] values) =>
        values.Select((value, index) => (value, index))
            .ToDictionary(pair => Normalize(pair.value), pair => pair.index, StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }
}
