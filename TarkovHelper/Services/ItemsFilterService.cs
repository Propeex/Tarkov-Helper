using TarkovHelper.Pages; // For AggregatedItemViewModel and CollectorItemViewModel
using TarkovHelper.Models;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for filtering and sorting item requirement view models.
    /// Extracted from ItemsPage.xaml.cs and CollectorPage.xaml.cs to improve maintainability.
    /// </summary>
    public static class ItemsFilterService
    {
        public static IEnumerable<AggregatedItemViewModel> FilterAndSort(
            IEnumerable<AggregatedItemViewModel> items,
            string searchText,
            string sourceFilter,
            string categoryFilter,
            string fulfillmentFilter,
            bool firOnly,
            bool hideFulfilled,
            string sortBy)
        {
            var filtered = items.Where(vm =>
            {
                if (!string.IsNullOrEmpty(searchText) &&
                    !vm.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
                    !vm.SubtitleName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (sourceFilter == "Quest" && vm.QuestCount == 0)
                    return false;
                if (sourceFilter == "Hideout" && vm.HideoutCount == 0)
                    return false;

                if (categoryFilter != "All" &&
                    !string.Equals(vm.ParentCategory, categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (firOnly && !vm.FoundInRaid)
                    return false;

                if (fulfillmentFilter != "All")
                {
                    var status = vm.FulfillmentStatus;
                    if (fulfillmentFilter == "NotStarted" && status != ItemFulfillmentStatus.NotStarted)
                        return false;
                    if (fulfillmentFilter == "InProgress" && status != ItemFulfillmentStatus.PartiallyFulfilled)
                        return false;
                    if (fulfillmentFilter == "Fulfilled" && status != ItemFulfillmentStatus.Fulfilled)
                        return false;
                }

                return !hideFulfilled || !vm.IsFulfilled;
            }).ToList();

            // Range requirements are displayed as multiple item rows, but they remain
            // one logical requirement. Sort logical requirements first, then flatten
            // their members in source order so a range group can never be split apart.
            var logicalGroups = filtered
                .GroupBy(
                    GetLogicalRequirementKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select((group, sourceIndex) => new ItemRequirementGroup(
                    group.ToList(),
                    sourceIndex))
                .ToList();

            IOrderedEnumerable<ItemRequirementGroup> orderedGroups = sortBy switch
            {
                "Total" => logicalGroups
                    .OrderByDescending(group => group.TotalCount)
                    .ThenBy(group => group.DisplayName, StringComparer.CurrentCulture),
                "Quest" => logicalGroups
                    .OrderByDescending(group => group.QuestCount)
                    .ThenBy(group => group.DisplayName, StringComparer.CurrentCulture),
                "Hideout" => logicalGroups
                    .OrderByDescending(group => group.HideoutCount)
                    .ThenBy(group => group.DisplayName, StringComparer.CurrentCulture),
                "Progress" => logicalGroups
                    .OrderByDescending(group => group.ProgressPercent)
                    .ThenBy(group => group.DisplayName, StringComparer.CurrentCulture),
                _ => logicalGroups
                    .OrderBy(group => group.DisplayName, StringComparer.CurrentCulture)
            };

            foreach (var group in orderedGroups)
            {
                for (var index = 0; index < group.Items.Count; index++)
                {
                    var item = group.Items[index];
                    item.AlternativeGroupHeaderVisibility =
                        item.IsAlternativeGroupMember && index == 0
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
                    yield return item;
                }
            }
        }

        private static string GetLogicalRequirementKey(AggregatedItemViewModel item) =>
            item.IsAlternativeGroupMember
                ? $"range:{item.RequirementLookupKey}"
                : $"item:{item.ItemNormalizedName}";

        private sealed class ItemRequirementGroup
        {
            public ItemRequirementGroup(List<AggregatedItemViewModel> items, int sourceIndex)
            {
                Items = items;
                SourceIndex = sourceIndex;
            }

            public List<AggregatedItemViewModel> Items { get; }
            public int SourceIndex { get; }
            private AggregatedItemViewModel First => Items[0];
            public string DisplayName => First.DisplayName;
            public int TotalCount => First.TotalCount;
            public int QuestCount => First.QuestCount;
            public int HideoutCount => First.HideoutCount;
            public double ProgressPercent => First.ProgressPercent;
        }

        public static IEnumerable<CollectorItemViewModel> FilterAndSortCollector(
            IEnumerable<CollectorItemViewModel> items,
            string searchText,
            string fulfillmentFilter,
            bool firOnly,
            bool hideFulfilled,
            string sortBy)
        {
            var filtered = items.Where(vm =>
            {
                if (!string.IsNullOrEmpty(searchText))
                {
                    if (!vm.DisplayName.ToLowerInvariant().Contains(searchText) &&
                        !vm.SubtitleName.ToLowerInvariant().Contains(searchText))
                        return false;
                }

                if (firOnly && !vm.FoundInRaid)
                    return false;

                if (fulfillmentFilter != "All")
                {
                    var status = vm.FulfillmentStatus;
                    if (fulfillmentFilter == "NotStarted" && status != ItemFulfillmentStatus.NotStarted)
                        return false;
                    if (fulfillmentFilter == "InProgress" && status != ItemFulfillmentStatus.PartiallyFulfilled)
                        return false;
                    if (fulfillmentFilter == "Fulfilled" && status != ItemFulfillmentStatus.Fulfilled)
                        return false;
                }

                if (hideFulfilled && vm.IsFulfilled)
                    return false;

                return true;
            });

            return sortBy switch
            {
                "Total" => filtered.OrderByDescending(vm => vm.TotalCount).ThenBy(vm => vm.DisplayName),
                "Quest" => filtered.OrderByDescending(vm => vm.QuestCount).ThenBy(vm => vm.DisplayName),
                "Progress" => filtered.OrderByDescending(vm => vm.ProgressPercent).ThenBy(vm => vm.DisplayName),
                _ => filtered.OrderBy(vm => vm.DisplayName)
            };
        }
    }
}
