using System.Runtime.CompilerServices;
using System.Windows;
using TarkovHelper.Models.Ammo;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Ammo;

internal static class V182RequirementSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        ValidateCanonicalOrders();
        ValidateAmmoPresentation();
        ValidateRangeInventoryIsolation();
        ValidateRangeGroupSorting();
    }

    private static void ValidateCanonicalOrders()
    {
        var traders = new[]
        {
            "Prapor", "Therapist", "Fence", "Skier", "Peacekeeper", "Mechanic",
            "Ragman", "Jaeger", "Ref", "Lightkeeper", "BTR Driver"
        };
        AssertSequentialRanks(traders, UiSortOrder.GetTraderRank, "trader");

        var maps = new[]
        {
            "Customs", "Shoreline", "The Labyrinth", "Icebreaker", "Factory", "Woods",
            "Interchange", "The Lab", "Reserve", "Lighthouse", "Streets of Tarkov",
            "Ground Zero", "Terminal"
        };
        AssertSequentialRanks(maps, UiSortOrder.GetMapRank, "map");

        var categories = new[]
        {
            "Weapons", "Magazines", "Ammunition", "Medical", "Food", "Melee",
            "Parts", "Grenades", "Barter", "Rigs", "Eyewear", "Containers",
            "Armor", "Info", "Keys", "Special"
        };
        AssertSequentialRanks(categories, UiSortOrder.GetItemCategoryRank, "item category");
    }

    private static void AssertSequentialRanks(
        IReadOnlyList<string> values,
        Func<string?, int> rankSelector,
        string label)
    {
        var actual = values.Select(rankSelector).ToArray();
        var expected = Enumerable.Range(0, values.Count).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"v1.8.2 {label} order regression: expected={string.Join(',', expected)}, " +
                $"actual={string.Join(',', actual)}.");
        }
    }

    private static void ValidateAmmoPresentation()
    {
        var grenadeDisplay = AmmoDbService.GetCaliberDisplay("Caliber40mmRU");
        if (!string.Equals(grenadeDisplay, "40mm 러시아 유탄", StringComparison.Ordinal))
            throw new InvalidDataException($"Caliber display regression: {grenadeDisplay}");

        var efficiency = AmmoArmorClassResult.Create(4, 15, 0).DisplayText;
        if (!int.TryParse(efficiency, out _) || efficiency.Contains("x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Armor efficiency must be a bare number: {efficiency}");
    }

    private static void ValidateRangeInventoryIsolation()
    {
        var task = new TarkovHelper.Models.TarkovTask
        {
            Ids = ["v182-range-smoke"],
            NormalizedName = "v182-range-smoke",
            Name = "v1.8.2 Range Smoke"
        };
        var requirement = new TarkovHelper.Models.QuestItem
        {
            RequirementGroupId = "objective-a",
            ItemNormalizedName = "group:objective-a",
            IsAlternativeGroup = true,
            AlternativeItemIds = ["item-a", "item-b", "item-c"],
            Amount = 3
        };

        var keys = QuestRequirementInventoryKey.BuildAlternativeItemKeys(task, requirement);
        if (keys.Count != 3 || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidDataException("Range requirement did not produce three distinct inventory keys.");
        if (keys.Any(key => requirement.AlternativeItemIds.Contains(key, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Range inventory keys collided with concrete item inventory keys.");
        if (keys.Any(key => !key.StartsWith("range:v182-range-smoke:objective-a:", StringComparison.Ordinal)))
            throw new InvalidDataException($"Range inventory key scope is invalid: {string.Join(',', keys)}");
    }

    private static void ValidateRangeGroupSorting()
    {
        var firstRangeMember = RangeMember("range:test", "range:test:item-a", "Zeta", true);
        var secondRangeMember = RangeMember("range:test", "range:test:item-b", "Alpha", false);
        var concrete = new AggregatedItemViewModel
        {
            ItemNormalizedName = "concrete-middle",
            RequirementLookupKey = "concrete-middle",
            DisplayName = "Middle"
        };

        var sorted = ItemsFilterService.FilterAndSort(
                [firstRangeMember, secondRangeMember, concrete],
                searchText: string.Empty,
                sourceFilter: "All",
                categoryFilter: "All",
                fulfillmentFilter: "All",
                firOnly: false,
                hideFulfilled: false,
                sortBy: "Name")
            .ToList();

        var rangeIndices = sorted
            .Select((item, index) => (item, index))
            .Where(pair => pair.item.IsAlternativeGroupMember)
            .Select(pair => pair.index)
            .ToArray();
        if (rangeIndices.Length != 2 || rangeIndices[1] != rangeIndices[0] + 1)
            throw new InvalidDataException("Range members were split apart by item sorting.");
        if (sorted[rangeIndices[0]].AlternativeGroupHeaderVisibility != Visibility.Visible ||
            sorted[rangeIndices[1]].AlternativeGroupHeaderVisibility != Visibility.Collapsed)
        {
            throw new InvalidDataException("Range group header was not assigned to the first visible member.");
        }

        var searched = ItemsFilterService.FilterAndSort(
                [firstRangeMember, secondRangeMember, concrete],
                searchText: "alpha",
                sourceFilter: "All",
                categoryFilter: "All",
                fulfillmentFilter: "All",
                firOnly: false,
                hideFulfilled: false,
                sortBy: "Name")
            .ToList();
        if (searched.Count != 1 || searched[0] != secondRangeMember ||
            searched[0].AlternativeGroupHeaderVisibility != Visibility.Visible)
        {
            throw new InvalidDataException("Filtered range group did not promote its first visible member header.");
        }
    }

    private static AggregatedItemViewModel RangeMember(
        string groupKey,
        string inventoryKey,
        string displayName,
        bool headerVisible) =>
        new()
        {
            ItemNormalizedName = inventoryKey,
            RequirementLookupKey = groupKey,
            AlternativeItemKeys = ["range:test:item-a", "range:test:item-b"],
            IsAlternativeGroupMember = true,
            AlternativeGroupHeaderText = "범위 제출 · 아래 항목 중 아무거나 3개",
            AlternativeGroupHeaderVisibility = headerVisible ? Visibility.Visible : Visibility.Collapsed,
            DisplayName = displayName,
            QuestCount = 3,
            TotalCount = 3
        };
}
