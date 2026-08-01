using System.Windows;
using TarkovHelper.Models;
using TarkovHelper.Pages; // For AggregatedItemViewModel and others

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for aggregating item requirements from quests and hideout modules.
    /// Extracted from ItemsPage.xaml.cs to improve maintainability.
    /// </summary>
    public class ItemsDataService
    {
        private static ItemsDataService? _instance;
        public static ItemsDataService Instance => _instance ??= new ItemsDataService();

        private QuestProgressService _questProgressService => QuestProgressService.Instance;
        private HideoutProgressService _hideoutProgressService => HideoutProgressService.Instance;
        private QuestGraphService _questGraphService => QuestGraphService.Instance;
        private LocalizationService _loc => LocalizationService.Instance;

        private static readonly HashSet<string> CurrencyItems = new(StringComparer.OrdinalIgnoreCase)
        {
            "roubles", "dollars", "euros"
        };

        private static bool IsCurrency(string normalizedName) => CurrencyItems.Contains(normalizedName);

        public string GetParentCategory(string? category) =>
            ItemCategoryClassifier.Classify(category);

        public string GetParentCategory(
            string? category,
            IEnumerable<string>? categoryHierarchy,
            bool isRangeSubmission = false) =>
            ItemCategoryClassifier.Classify(category, categoryHierarchy, isRangeSubmission);

        public async Task<List<AggregatedItemViewModel>> GetAggregatedItemsAsync(Dictionary<string, TarkovItem>? itemLookup)
        {
            var hideoutItems = _hideoutProgressService.GetAllRemainingItemRequirements();
            var questItems = GetQuestItemRequirements(itemLookup);
            var mergedItems = new Dictionary<string, AggregatedItemViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in hideoutItems)
            {
                var hideoutItem = kvp.Value;
                var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                    hideoutItem.ItemName, hideoutItem.ItemNameKo, hideoutItem.ItemNameJa);

                string? wikiLink = null;
                string? category = null;
                IReadOnlyList<string> categoryHierarchy = Array.Empty<string>();
                if (itemLookup != null && itemLookup.TryGetValue(hideoutItem.ItemNormalizedName, out var itemInfo))
                {
                    wikiLink = itemInfo.WikiLink;
                    category = itemInfo.Category;
                    categoryHierarchy = itemInfo.Categories;
                }

                mergedItems[kvp.Key] = new AggregatedItemViewModel
                {
                    ItemId = hideoutItem.ItemId,
                    ItemNormalizedName = hideoutItem.ItemNormalizedName,
                    RequirementLookupKey = hideoutItem.ItemNormalizedName,
                    DisplayName = displayName,
                    SubtitleName = subtitle,
                    SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                    Category = category,
                    Categories = categoryHierarchy,
                    ParentCategory = GetParentCategory(category, categoryHierarchy),
                    HideoutCount = hideoutItem.HideoutCount,
                    HideoutFIRCount = hideoutItem.HideoutFIRCount,
                    TotalCount = hideoutItem.HideoutCount,
                    TotalFIRCount = hideoutItem.HideoutFIRCount,
                    FoundInRaid = hideoutItem.FoundInRaid,
                    IconLink = hideoutItem.IconLink,
                    WikiLink = wikiLink
                };
            }

            foreach (var kvp in questItems)
            {
                var questItem = kvp.Value;
                if (mergedItems.TryGetValue(kvp.Key, out var existing))
                {
                    existing.QuestCount = questItem.QuestCount;
                    existing.QuestFIRCount = questItem.QuestFIRCount;
                    existing.TotalCount = existing.HideoutCount + questItem.QuestCount;
                    existing.TotalFIRCount = existing.HideoutFIRCount + questItem.QuestFIRCount;
                    existing.FoundInRaid |= questItem.FoundInRaid;
                    existing.WikiLink ??= questItem.WikiLink;
                    if (string.IsNullOrEmpty(existing.Category))
                    {
                        existing.Category = questItem.Category;
                        existing.Categories = questItem.Categories;
                        existing.ParentCategory = GetParentCategory(
                            questItem.Category,
                            questItem.Categories,
                            questItem.IsAlternativeGroupMember);
                    }
                    continue;
                }

                var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                    questItem.ItemName, questItem.ItemNameKo, questItem.ItemNameJa);
                mergedItems[kvp.Key] = new AggregatedItemViewModel
                {
                    ItemId = questItem.ItemId,
                    ItemNormalizedName = questItem.ItemNormalizedName,
                    RequirementLookupKey = questItem.RequirementLookupKey,
                    AlternativeItemKeys = questItem.AlternativeItemKeys,
                    IsAlternativeGroupMember = questItem.IsAlternativeGroupMember,
                    AlternativeGroupHeaderText = questItem.AlternativeGroupHeaderText,
                    AlternativeGroupHeaderVisibility = questItem.IsAlternativeGroupFirst
                        ? Visibility.Visible
                        : Visibility.Collapsed,
                    ItemIndent = questItem.IsAlternativeGroupMember
                        ? new Thickness(22, 0, 0, 0)
                        : new Thickness(0),
                    DisplayName = displayName,
                    SubtitleName = subtitle,
                    SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                    Category = questItem.Category,
                    Categories = questItem.Categories,
                    ParentCategory = GetParentCategory(
                        questItem.Category,
                        questItem.Categories,
                        questItem.IsAlternativeGroupMember),
                    QuestCount = questItem.QuestCount,
                    QuestFIRCount = questItem.QuestFIRCount,
                    TotalCount = questItem.QuestCount,
                    TotalFIRCount = questItem.QuestFIRCount,
                    FoundInRaid = questItem.FoundInRaid,
                    IconLink = questItem.IconLink,
                    WikiLink = questItem.WikiLink
                };
            }

            return mergedItems.Values.ToList();
        }

        private Dictionary<string, QuestItemAggregate> GetQuestItemRequirements(Dictionary<string, TarkovItem>? itemLookup)
        {
            var result = new Dictionary<string, QuestItemAggregate>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in _questProgressService.AllTasks)
            {
                var status = _questProgressService.GetStatus(task);
                if (status is QuestStatus.Failed or QuestStatus.Unavailable || task.RequiredItems == null)
                    continue;

                var isCompleted = status == QuestStatus.Done;
                foreach (var questItem in task.RequiredItems)
                {
                    var countToAdd = isCompleted
                        ? 0
                        : IsCurrency(questItem.ItemNormalizedName) ? 1 : questItem.Amount;
                    var firCountToAdd = questItem.FoundInRaid ? countToAdd : 0;

                    if (questItem.IsAlternativeGroup)
                    {
                        var groupKey = QuestRequirementInventoryKey.BuildGroupKey(task, questItem);
                        var inventoryKeys = QuestRequirementInventoryKey.BuildAlternativeItemKeys(task, questItem);
                        for (var index = 0; index < questItem.AlternativeItemIds.Count; index++)
                        {
                            var itemId = questItem.AlternativeItemIds[index];
                            if (string.IsNullOrWhiteSpace(itemId) || itemLookup == null ||
                                !itemLookup.TryGetValue(itemId, out var itemInfo) ||
                                string.Equals(itemInfo.Category, "Quest Items", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var inventoryKey = QuestRequirementInventoryKey.BuildAlternativeItemKey(task, questItem, itemId);
                            var localizedName = index < questItem.AlternativeItemNames.Count &&
                                                !string.IsNullOrWhiteSpace(questItem.AlternativeItemNames[index])
                                ? questItem.AlternativeItemNames[index]
                                : itemInfo.NameKo ?? itemInfo.Name;

                            result[inventoryKey] = new QuestItemAggregate
                            {
                                ItemId = itemInfo.Id,
                                ItemName = localizedName,
                                ItemNameKo = localizedName,
                                ItemNormalizedName = inventoryKey,
                                RequirementLookupKey = groupKey,
                                AlternativeItemKeys = inventoryKeys,
                                IsAlternativeGroupMember = true,
                                IsAlternativeGroupFirst = index == 0,
                                AlternativeGroupHeaderText = $"범위 제출 · 아래 항목 중 아무거나 {questItem.Amount}개",
                                IconLink = itemInfo.IconLink,
                                WikiLink = itemInfo.WikiLink,
                                Category = itemInfo.Category,
                                Categories = itemInfo.Categories,
                                QuestCount = countToAdd,
                                QuestFIRCount = firCountToAdd,
                                FoundInRaid = questItem.FoundInRaid
                            };
                        }

                        continue;
                    }

                    if (itemLookup == null || !itemLookup.TryGetValue(questItem.ItemNormalizedName, out var concreteInfo) ||
                        string.Equals(concreteInfo.Category, "Quest Items", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (result.TryGetValue(questItem.ItemNormalizedName, out var existing))
                    {
                        existing.QuestCount += countToAdd;
                        existing.QuestFIRCount += firCountToAdd;
                        existing.FoundInRaid |= questItem.FoundInRaid;
                    }
                    else
                    {
                        result[questItem.ItemNormalizedName] = new QuestItemAggregate
                        {
                            ItemId = concreteInfo.Id,
                            ItemName = concreteInfo.Name,
                            ItemNameKo = concreteInfo.NameKo,
                            ItemNameJa = concreteInfo.NameJa,
                            ItemNormalizedName = questItem.ItemNormalizedName,
                            RequirementLookupKey = questItem.ItemNormalizedName,
                            IconLink = concreteInfo.IconLink,
                            WikiLink = concreteInfo.WikiLink,
                            Category = concreteInfo.Category,
                            Categories = concreteInfo.Categories,
                            QuestCount = countToAdd,
                            QuestFIRCount = firCountToAdd,
                            FoundInRaid = questItem.FoundInRaid
                        };
                    }
                }
            }

            return result;
        }

        public List<QuestItemSourceViewModel> GetQuestSources(string itemRequirementKey)
        {
            var sources = new List<QuestItemSourceViewModel>();
            foreach (var task in _questProgressService.AllTasks)
            {
                var status = _questProgressService.GetStatus(task);
                if (status is QuestStatus.Failed or QuestStatus.Unavailable || task.RequiredItems == null)
                    continue;

                foreach (var questItem in task.RequiredItems)
                {
                    var requirementKey = questItem.IsAlternativeGroup
                        ? QuestRequirementInventoryKey.BuildGroupKey(task, questItem)
                        : questItem.ItemNormalizedName;
                    if (!string.Equals(requirementKey, itemRequirementKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var questName = task.NameKo ?? task.Name;
                    if (status == QuestStatus.Done)
                        questName = $"✓ {questName}";

                    sources.Add(new QuestItemSourceViewModel
                    {
                        QuestName = questName,
                        TraderName = task.Trader,
                        Amount = questItem.Amount,
                        FoundInRaid = questItem.FoundInRaid,
                        Task = task,
                        QuestNormalizedName = task.NormalizedName ?? string.Empty,
                        DogtagMinLevel = questItem.DogtagMinLevel
                    });
                }
            }

            return sources;
        }

        public List<HideoutItemSourceViewModel> GetHideoutSources(string itemNormalizedName)
        {
            var sources = new List<HideoutItemSourceViewModel>();

            foreach (var module in _hideoutProgressService.AllModules)
            {
                var currentLevel = _hideoutProgressService.GetCurrentLevel(module);

                foreach (var level in module.Levels.Where(l => l.Level > currentLevel))
                {
                    foreach (var itemReq in level.ItemRequirements)
                    {
                        if (string.Equals(itemReq.ItemNormalizedName, itemNormalizedName, StringComparison.OrdinalIgnoreCase))
                        {
                            var moduleName = module.NameKo ?? module.Name;
                            sources.Add(new HideoutItemSourceViewModel
                            {
                                ModuleName = moduleName,
                                Level = level.Level,
                                Amount = itemReq.Count,
                                FoundInRaid = itemReq.FoundInRaid,
                                StationId = module.Id
                            });
                        }
                    }
                }
            }

            return sources.OrderBy(s => s.ModuleName).ThenBy(s => s.Level).ToList();
        }

    public async Task<List<CollectorItemViewModel>> GetCollectorAggregatedItemsAsync(bool includePreQuests, Dictionary<string, TarkovItem>? itemLookup)
    {
        var collectorItems = GetCollectorQuestItemRequirements(includePreQuests, itemLookup);

        return collectorItems.Values.Select(item =>
        {
            var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                item.ItemName, item.ItemNameKo, item.ItemNameJa);

            return new CollectorItemViewModel
            {
                ItemId = item.ItemId,
                ItemNormalizedName = item.ItemNormalizedName,
                DisplayName = displayName,
                SubtitleName = subtitle,
                SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                QuestCount = item.QuestCount,
                QuestFIRCount = item.QuestFIRCount,
                TotalCount = item.QuestCount,
                TotalFIRCount = item.QuestFIRCount,
                FoundInRaid = item.FoundInRaid,
                IconLink = item.IconLink,
                WikiLink = item.WikiLink
            };
        }).ToList();
    }

    private Dictionary<string, CollectorQuestItemAggregate> GetCollectorQuestItemRequirements(bool includePreQuests, Dictionary<string, TarkovItem>? itemLookup)
    {
        var result = new Dictionary<string, CollectorQuestItemAggregate>(StringComparer.OrdinalIgnoreCase);
        var questsToInclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var collectorQuest = _questProgressService.AllTasks
            .FirstOrDefault(t => string.Equals(t.NormalizedName, "collector", StringComparison.OrdinalIgnoreCase));

        if (collectorQuest != null && !string.IsNullOrEmpty(collectorQuest.NormalizedName))
        {
            var status = _questProgressService.GetStatus(collectorQuest);
            if (status != QuestStatus.Done && status != QuestStatus.Failed && status != QuestStatus.Unavailable)
            {
                questsToInclude.Add(collectorQuest.NormalizedName);
            }

            if (includePreQuests)
            {
                var prereqs = _questGraphService.GetAllPrerequisites(collectorQuest.NormalizedName);
                foreach (var prereq in prereqs)
                {
                    if (string.IsNullOrEmpty(prereq.NormalizedName))
                        continue;

                    var prereqStatus = _questProgressService.GetStatus(prereq);
                    if (prereqStatus == QuestStatus.Done || prereqStatus == QuestStatus.Failed || prereqStatus == QuestStatus.Unavailable)
                        continue;

                    questsToInclude.Add(prereq.NormalizedName);
                }
            }
        }

        foreach (var task in _questProgressService.AllTasks)
        {
            if (string.IsNullOrEmpty(task.NormalizedName))
                continue;

            if (!questsToInclude.Contains(task.NormalizedName))
                continue;

            if (task.RequiredItems == null)
                continue;

            foreach (var questItem in task.RequiredItems)
            {
                TarkovItem? itemInfo = null;
                itemLookup?.TryGetValue(questItem.ItemNormalizedName, out itemInfo);

                if (itemInfo == null)
                    continue;

                var countToAdd = IsCurrency(questItem.ItemNormalizedName) ? 1 : questItem.Amount;
                var firCountToAdd = questItem.FoundInRaid ? countToAdd : 0;

                if (result.TryGetValue(questItem.ItemNormalizedName, out var existing))
                {
                    existing.QuestCount += countToAdd;
                    if (questItem.FoundInRaid)
                    {
                        existing.QuestFIRCount += countToAdd;
                        existing.FoundInRaid = true;
                    }
                }
                else
                {
                    result[questItem.ItemNormalizedName] = new CollectorQuestItemAggregate
                    {
                        ItemId = itemInfo?.Id ?? questItem.ItemNormalizedName,
                        ItemName = itemInfo.Name,
                        ItemNameKo = itemInfo.NameKo,
                        ItemNameJa = itemInfo.NameJa,
                        ItemNormalizedName = questItem.ItemNormalizedName,
                        IconLink = itemInfo.IconLink,
                        WikiLink = itemInfo.WikiLink,
                        QuestCount = countToAdd,
                        QuestFIRCount = firCountToAdd,
                        FoundInRaid = questItem.FoundInRaid
                    };
                }
            }
        }

        return result;
    }

    public List<CollectorQuestItemSourceViewModel> GetCollectorQuestSources(string itemNormalizedName, bool includePreQuests)
    {
        var sources = new List<CollectorQuestItemSourceViewModel>();
        var questsToInclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var collectorQuest = _questProgressService.AllTasks
            .FirstOrDefault(t => string.Equals(t.NormalizedName, "collector", StringComparison.OrdinalIgnoreCase));

        if (collectorQuest != null && !string.IsNullOrEmpty(collectorQuest.NormalizedName))
        {
            var status = _questProgressService.GetStatus(collectorQuest);
            if (status != QuestStatus.Done && status != QuestStatus.Failed && status != QuestStatus.Unavailable)
            {
                questsToInclude.Add(collectorQuest.NormalizedName);
            }

            if (includePreQuests)
            {
                var prereqs = _questGraphService.GetAllPrerequisites(collectorQuest.NormalizedName);
                foreach (var prereq in prereqs)
                {
                    if (string.IsNullOrEmpty(prereq.NormalizedName))
                        continue;
                    var prereqStatus = _questProgressService.GetStatus(prereq);
                    if (prereqStatus == QuestStatus.Done || prereqStatus == QuestStatus.Failed || prereqStatus == QuestStatus.Unavailable)
                        continue;
                    questsToInclude.Add(prereq.NormalizedName);
                }
            }
        }

        foreach (var task in _questProgressService.AllTasks)
        {
            if (string.IsNullOrEmpty(task.NormalizedName))
                continue;

            if (!questsToInclude.Contains(task.NormalizedName))
                continue;

            if (task.RequiredItems == null)
                continue;

            foreach (var questItem in task.RequiredItems)
            {
                if (string.Equals(questItem.ItemNormalizedName, itemNormalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(new CollectorQuestItemSourceViewModel
                    {
                        QuestName = task.NameKo ?? task.Name,
                        TraderName = task.Trader,
                        Amount = questItem.Amount,
                        FoundInRaid = questItem.FoundInRaid,
                        IsKappaRequired = task.ReqKappa,
                        Task = task,
                        QuestNormalizedName = task.NormalizedName ?? string.Empty
                    });
                }
            }
        }

        return sources;
    }

    private (string DisplayName, string Subtitle, bool ShowSubtitle) GetLocalizedNames(
        string name, string? nameKo, string? nameJa)
    {
        return (!string.IsNullOrEmpty(nameKo)) ? (nameKo, name, true) : (name, string.Empty, false);
    }
}
}