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

        private static readonly Dictionary<string, string> CategoryMapping = new(StringComparer.OrdinalIgnoreCase)
        {
            // Food and medicine
            { "Food", "Provisions" },
            { "Drinks", "Provisions" },
            { "Medkits", "Medical" },
            { "Medical supplies", "Medical" },
            { "Injury treatment", "Medical" },
            { "Stimulants", "Medical" },
            { "Drugs", "Medical" },

            // Barter and construction materials
            { "Electronics", "Barter" },
            { "Building materials", "Barter" },
            { "Flammable materials", "Barter" },
            { "Energy elements", "Barter" },
            { "Household goods", "Barter" },
            { "Tools", "Barter" },
            { "Valuables", "Barter" },
            { "Other", "Barter" },

            // Keys and information
            { "Info items", "KeysIntel" },
            { "Keys", "KeysIntel" },
            { "Keycards", "KeysIntel" },
            { "Maps", "KeysIntel" },
            { "Extraction intel", "KeysIntel" },
            { "Notes", "KeysIntel" },

            // Weapons, ammunition and parts
            { "Weapons", "Weapons" },
            { "Rounds", "Ammunition" },
            { "Ammo boxes", "Ammunition" },
            { "Shrapnel", "Ammunition" },
            { "Magazines", "Ammunition" },
            { "Mounts", "WeaponParts" },
            { "Stocks & chassis", "WeaponParts" },
            { "Handguards", "WeaponParts" },
            { "Barrels", "WeaponParts" },
            { "Flash hiders & muzzle brakes", "WeaponParts" },
            { "Suppressors", "WeaponParts" },
            { "Muzzle adapters", "WeaponParts" },
            { "Iron sights", "WeaponParts" },
            { "Pistol grips", "WeaponParts" },
            { "Receivers and slides", "WeaponParts" },
            { "Charging handles", "WeaponParts" },
            { "Gas blocks", "WeaponParts" },
            { "Foregrips", "WeaponParts" },
            { "Auxiliary parts", "WeaponParts" },
            { "Bipods", "WeaponParts" },
            { "Underbarrel grenade launchers", "WeaponParts" },
            { "Scopes", "WeaponParts" },
            { "Assault scopes", "WeaponParts" },
            { "Reflex sights", "WeaponParts" },
            { "Compact reflex sights", "WeaponParts" },
            { "Night vision scopes", "WeaponParts" },
            { "Thermal vision sights", "WeaponParts" },
            { "Flashlights", "WeaponParts" },
            { "Tactical combo devices", "WeaponParts" },

            // Wearable equipment
            { "Armor vests", "Equipment" },
            { "Armor plates", "Equipment" },
            { "Chest rigs", "Equipment" },
            { "Backpacks", "Equipment" },
            { "Headwear", "Equipment" },
            { "Eyewear", "Equipment" },
            { "Face cover", "Equipment" },
            { "Earpieces", "Equipment" },
            { "Armbands", "Equipment" },
            { "Special equipment", "Equipment" },
            { "Helmet mods", "Equipment" },

            // Storage, money and special items
            { "Containers & cases", "Containers" },
            { "Secure containers", "Containers" },
            { "Money", "Currency" },
            { "Quest Items", "Quest" },
            { "Dogtag", "Quest" },
            { "Posters", "Other" },
        };

        public string GetParentCategory(string? category)
        {
            if (string.IsNullOrEmpty(category))
                return "Other";

            var baseCategory = category.Contains('|') ? category.Split('|')[0] : category;

            return CategoryMapping.TryGetValue(baseCategory, out var parentCategory)
                ? parentCategory
                : "Other";
        }

        public async Task<List<AggregatedItemViewModel>> GetAggregatedItemsAsync(Dictionary<string, TarkovItem>? itemLookup)
        {
            // Get hideout requirements
            var hideoutItems = _hideoutProgressService.GetAllRemainingItemRequirements();

            // Get quest requirements, including zero-count placeholders from completed quests.
            var questItems = GetQuestItemRequirements(itemLookup);

            // Merge both sources
            var mergedItems = new Dictionary<string, AggregatedItemViewModel>(StringComparer.OrdinalIgnoreCase);

            // Add hideout items
            foreach (var kvp in hideoutItems)
            {
                var hideoutItem = kvp.Value;
                var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                    hideoutItem.ItemName, hideoutItem.ItemNameKo, hideoutItem.ItemNameJa);

                string? wikiLink = null;
                string? category = null;
                if (itemLookup != null && itemLookup.TryGetValue(hideoutItem.ItemNormalizedName, out var itemInfo))
                {
                    wikiLink = itemInfo.WikiLink;
                    category = itemInfo.Category;
                }

                mergedItems[kvp.Key] = new AggregatedItemViewModel
                {
                    ItemId = hideoutItem.ItemId,
                    ItemNormalizedName = hideoutItem.ItemNormalizedName,
                    DisplayName = displayName,
                    SubtitleName = subtitle,
                    SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                    Category = category,
                    ParentCategory = GetParentCategory(category),
                    QuestCount = 0,
                    QuestFIRCount = 0,
                    HideoutCount = hideoutItem.HideoutCount,
                    HideoutFIRCount = hideoutItem.HideoutFIRCount,
                    TotalCount = hideoutItem.HideoutCount,
                    TotalFIRCount = hideoutItem.HideoutFIRCount,
                    FoundInRaid = hideoutItem.FoundInRaid,
                    IconLink = hideoutItem.IconLink,
                    WikiLink = wikiLink
                };
            }

            // Add/merge quest items
            foreach (var kvp in questItems)
            {
                var questItem = kvp.Value;
                if (mergedItems.TryGetValue(kvp.Key, out var existing))
                {
                    existing.QuestCount = questItem.QuestCount;
                    existing.QuestFIRCount = questItem.QuestFIRCount;
                    existing.TotalCount = existing.HideoutCount + questItem.QuestCount;
                    existing.TotalFIRCount = existing.HideoutFIRCount + questItem.QuestFIRCount;
                    if (questItem.FoundInRaid)
                        existing.FoundInRaid = true;
                    if (string.IsNullOrEmpty(existing.WikiLink))
                        existing.WikiLink = questItem.WikiLink;
                    if (string.IsNullOrEmpty(existing.Category))
                    {
                        existing.Category = questItem.Category;
                        existing.ParentCategory = GetParentCategory(questItem.Category);
                    }
                }
                else
                {
                    var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                        questItem.ItemName, questItem.ItemNameKo, questItem.ItemNameJa);

                    mergedItems[kvp.Key] = new AggregatedItemViewModel
                    {
                        ItemId = questItem.ItemId,
                        ItemNormalizedName = questItem.ItemNormalizedName,
                        DisplayName = displayName,
                        SubtitleName = subtitle,
                        SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                        Category = questItem.Category,
                        ParentCategory = GetParentCategory(questItem.Category),
                        QuestCount = questItem.QuestCount,
                        QuestFIRCount = questItem.QuestFIRCount,
                        HideoutCount = 0,
                        HideoutFIRCount = 0,
                        TotalCount = questItem.QuestCount,
                        TotalFIRCount = questItem.QuestFIRCount,
                        FoundInRaid = questItem.FoundInRaid,
                        IconLink = questItem.IconLink,
                        WikiLink = questItem.WikiLink
                    };
                }
            }

            return mergedItems.Values.ToList();
        }

        private Dictionary<string, QuestItemAggregate> GetQuestItemRequirements(Dictionary<string, TarkovItem>? itemLookup)
        {
            var result = new Dictionary<string, QuestItemAggregate>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in _questProgressService.AllTasks)
            {
                var status = _questProgressService.GetStatus(task);
                if (status == QuestStatus.Failed || status == QuestStatus.Unavailable)
                    continue;

                if (task.RequiredItems == null)
                    continue;

                var isCompleted = status == QuestStatus.Done;

                foreach (var questItem in task.RequiredItems)
                {
                    TarkovItem? itemInfo = null;
                    itemLookup?.TryGetValue(questItem.ItemNormalizedName, out itemInfo);

                    if (itemInfo == null)
                        continue;

                    if (string.Equals(itemInfo.Category, "Quest Items", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var itemName = itemInfo.Name;
                    var iconLink = itemInfo.IconLink;
                    var wikiLink = itemInfo.WikiLink;

                    // Completed quests keep a zero-count item placeholder. This preserves the
                    // item row while allowing the existing fulfillment UI to mark it complete.
                    var countToAdd = isCompleted
                        ? 0
                        : IsCurrency(questItem.ItemNormalizedName) ? 1 : questItem.Amount;
                    var firCountToAdd = questItem.FoundInRaid ? countToAdd : 0;

                    if (result.TryGetValue(questItem.ItemNormalizedName, out var existing))
                    {
                        existing.QuestCount += countToAdd;
                        if (questItem.FoundInRaid)
                        {
                            existing.QuestFIRCount += firCountToAdd;
                            existing.FoundInRaid = true;
                        }
                    }
                    else
                    {
                        result[questItem.ItemNormalizedName] = new QuestItemAggregate
                        {
                            ItemId = itemInfo?.Id ?? questItem.ItemNormalizedName,
                            ItemName = itemName,
                            ItemNameKo = itemInfo?.NameKo,
                            ItemNameJa = itemInfo?.NameJa,
                            ItemNormalizedName = questItem.ItemNormalizedName,
                            IconLink = iconLink,
                            WikiLink = wikiLink,
                            Category = itemInfo?.Category,
                            QuestCount = countToAdd,
                            QuestFIRCount = firCountToAdd,
                            FoundInRaid = questItem.FoundInRaid
                        };
                    }
                }
            }

            return result;
        }

        public List<QuestItemSourceViewModel> GetQuestSources(string itemNormalizedName)
        {
            var sources = new List<QuestItemSourceViewModel>();

            foreach (var task in _questProgressService.AllTasks)
            {
                var status = _questProgressService.GetStatus(task);
                if (status == QuestStatus.Failed || status == QuestStatus.Unavailable)
                    continue;

                if (task.RequiredItems == null)
                    continue;

                foreach (var questItem in task.RequiredItems)
                {
                    if (string.Equals(questItem.ItemNormalizedName, itemNormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
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