from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding="utf-8", newline="")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one literal match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def regex_once(path: str, pattern: str, replacement: str) -> None:
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{path}: expected one regex match, found {count}: {pattern[:100]!r}")
    write(path, updated)


# Exactly sixteen Korean parent categories.
write(
    "TarkovHelper/Services/LocalizationService.Items.cs",
    '''namespace TarkovHelper.Services;

/// <summary>
/// Items page specific localization strings.
/// </summary>
public partial class LocalizationService
{
    #region Items Page - Filter Labels

    public string ItemsSearchPlaceholder => "아이템 검색...";
    public string ItemsFilterAll => "전체";
    public string ItemsFilterQuest => "퀘스트";
    public string ItemsFilterHideout => "은신처";
    public string ItemsFilterAllCategories => "전체 카테고리";
    public string ItemsFilterAllStatus => "전체 상태";
    public string ItemsFilterNotStarted => "미시작";
    public string ItemsFilterInProgress => "진행 중";
    public string ItemsFilterFulfilled => "완료";
    public string ItemsFilterFirOnly => "FIR만";
    public string ItemsFilterHideFulfilled => "완료 숨기기";
    public string ItemsSortName => "이름";
    public string ItemsSortTotalCount => "총 수량";
    public string ItemsSortQuestCount => "퀘스트 수량";
    public string ItemsSortProgress => "진행도";

    #endregion

    #region Items Page - Column Headers

    public string ItemsHeaderItemName => "아이템 이름";
    public string ItemsHeaderQuest => "퀘스트";
    public string ItemsHeaderHideout => "은신처";
    public string ItemsHeaderTotal => "합계";
    public string ItemsHeaderNeed => "필요";
    public string ItemsHeaderOwned => "보유:";

    #endregion

    #region Items Page - Detail Panel

    public string ItemsSelectItem => "아이템을 선택하면 상세 정보가 표시됩니다";
    public string ItemsOpenWiki => "위키 열기";
    public string ItemsYourInventory => "보유 아이템";
    public string ItemsProgress => "진행도";
    public string ItemsRequiredForQuests => "퀘스트 필요 항목";
    public string ItemsRequiredForHideout => "은신처 필요 항목";
    public string ItemsLevel => "레벨";

    #endregion

    public string ItemsLoading => "아이템 데이터 로딩 중...";

    private static readonly IReadOnlyDictionary<string, string> CategoryNamesKo =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["All Categories"] = "전체 카테고리",
            ["Weapons"] = "무기",
            ["Magazines"] = "탄창",
            ["Ammunition"] = "탄약",
            ["Medical"] = "의약품",
            ["Food"] = "음식",
            ["Melee"] = "근접 무기",
            ["Parts"] = "부품",
            ["Grenades"] = "수류탄",
            ["Barter"] = "물물교환",
            ["Rigs"] = "리그",
            ["Eyewear"] = "고글",
            ["Containers"] = "보관함",
            ["Armor"] = "보호구",
            ["Info"] = "정보",
            ["Keys"] = "열쇠",
            ["Special"] = "특수"
        };

    public string GetCategoryName(string categoryKey) =>
        CategoryNamesKo.TryGetValue(categoryKey, out var translated)
            ? translated
            : CategoryNamesKo["Special"];
}
'''
)

# Rebuild the item aggregation methods so range requirements become separate,
# quest-scoped item rows with a shared completion pool.
regex_once(
    "TarkovHelper/Services/ItemsDataService.cs",
    r"        private static bool IsCurrency\(string normalizedName\) => CurrencyItems.Contains\(normalizedName\);\n\n        public string GetParentCategory\(string\? category\)\n        \{.*?\n        \}\n\n        public async Task<List<AggregatedItemViewModel>> GetAggregatedItemsAsync.*?\n        \}\n\n        private Dictionary<string, QuestItemAggregate> GetQuestItemRequirements.*?\n        \}\n\n        public List<QuestItemSourceViewModel> GetQuestSources.*?\n        \}\n\n        public List<HideoutItemSourceViewModel>",
    '''        private static bool IsCurrency(string normalizedName) => CurrencyItems.Contains(normalizedName);

        private static readonly IReadOnlyDictionary<string, string> CategoryMapping =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weapons"] = "Weapons",
                ["Magazines"] = "Magazines",
                ["Rounds"] = "Ammunition", ["Ammo boxes"] = "Ammunition", ["Shrapnel"] = "Ammunition",
                ["Medkits"] = "Medical", ["Medical supplies"] = "Medical", ["Injury treatment"] = "Medical",
                ["Stimulants"] = "Medical", ["Drugs"] = "Medical",
                ["Food"] = "Food", ["Drinks"] = "Food", ["Food and drink"] = "Food",
                ["Melee weapons"] = "Melee", ["Melee"] = "Melee",
                ["Mounts"] = "Parts", ["Stocks & chassis"] = "Parts", ["Handguards"] = "Parts",
                ["Barrels"] = "Parts", ["Flash hiders & muzzle brakes"] = "Parts", ["Suppressors"] = "Parts",
                ["Muzzle adapters"] = "Parts", ["Iron sights"] = "Parts", ["Pistol grips"] = "Parts",
                ["Receivers and slides"] = "Parts", ["Charging handles"] = "Parts", ["Gas blocks"] = "Parts",
                ["Foregrips"] = "Parts", ["Auxiliary parts"] = "Parts", ["Bipods"] = "Parts",
                ["Underbarrel grenade launchers"] = "Parts", ["Scopes"] = "Parts", ["Assault scopes"] = "Parts",
                ["Reflex sights"] = "Parts", ["Compact reflex sights"] = "Parts", ["Night vision scopes"] = "Parts",
                ["Thermal vision sights"] = "Parts", ["Flashlights"] = "Parts", ["Tactical combo devices"] = "Parts",
                ["Helmet mods"] = "Parts",
                ["Grenades"] = "Grenades", ["Throwables"] = "Grenades", ["Special grenades"] = "Grenades",
                ["Electronics"] = "Barter", ["Building materials"] = "Barter", ["Flammable materials"] = "Barter",
                ["Energy elements"] = "Barter", ["Household goods"] = "Barter", ["Tools"] = "Barter",
                ["Valuables"] = "Barter",
                ["Chest rigs"] = "Rigs", ["Backpacks"] = "Rigs",
                ["Eyewear"] = "Eyewear",
                ["Containers & cases"] = "Containers", ["Secure containers"] = "Containers",
                ["Armor vests"] = "Armor", ["Armor plates"] = "Armor", ["Headwear"] = "Armor",
                ["Face cover"] = "Armor", ["Earpieces"] = "Armor",
                ["Info items"] = "Info", ["Maps"] = "Info", ["Extraction intel"] = "Info",
                ["Notes"] = "Info", ["Dogtag"] = "Info",
                ["Keys"] = "Keys", ["Keycards"] = "Keys",
                ["Special equipment"] = "Special", ["Quest Items"] = "Special", ["Money"] = "Special",
                ["Posters"] = "Special", ["Armbands"] = "Special", ["Other"] = "Special"
            };

        public string GetParentCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return "Special";

            var baseCategory = category.Contains('|') ? category.Split('|')[0] : category;
            return CategoryMapping.TryGetValue(baseCategory.Trim(), out var parent)
                ? parent
                : "Special";
        }

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
                if (itemLookup != null && itemLookup.TryGetValue(hideoutItem.ItemNormalizedName, out var itemInfo))
                {
                    wikiLink = itemInfo.WikiLink;
                    category = itemInfo.Category;
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
                    ParentCategory = GetParentCategory(category),
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
                        existing.ParentCategory = GetParentCategory(questItem.Category);
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
                    ParentCategory = GetParentCategory(questItem.Category),
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

        public List<HideoutItemSourceViewModel>'''
)

# View model fields and group-level fulfillment.
replace_once(
    "TarkovHelper/Pages/ItemsViewModels.cs",
    """        public string ItemNormalizedName { get; set; } = string.Empty;\n        public IReadOnlyList<string> AlternativeItemKeys { get; set; } = Array.Empty<string>();\n        public string DisplayName { get; set; } = string.Empty;\n""",
    """        public string ItemNormalizedName { get; set; } = string.Empty;\n        public string RequirementLookupKey { get; set; } = string.Empty;\n        public IReadOnlyList<string> AlternativeItemKeys { get; set; } = Array.Empty<string>();\n        public bool IsAlternativeGroupMember { get; set; }\n        public string AlternativeGroupHeaderText { get; set; } = string.Empty;\n        public Visibility AlternativeGroupHeaderVisibility { get; set; } = Visibility.Collapsed;\n        public Thickness ItemIndent { get; set; } = new(0);\n        public string DisplayName { get; set; } = string.Empty;\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsViewModels.cs",
    """        public int OwnedTotalQuantity => OwnedFirQuantity + OwnedNonFirQuantity;\n\n        // Fulfillment calculation\n        public ItemFulfillmentStatus FulfillmentStatus => ItemRequirementFulfillment.GetStatus(\n            TotalCount,\n            TotalFIRCount,\n            OwnedFirQuantity,\n            OwnedNonFirQuantity);\n\n        public double ProgressPercent => ItemRequirementFulfillment.GetProgressPercent(\n            TotalCount,\n            TotalFIRCount,\n            OwnedFirQuantity,\n            OwnedNonFirQuantity);\n""",
    """        private int _groupOwnedFirQuantity;\n        private int _groupOwnedNonFirQuantity;\n\n        public int GroupOwnedFirQuantity\n        {\n            get => _groupOwnedFirQuantity;\n            set\n            {\n                if (_groupOwnedFirQuantity == value) return;\n                _groupOwnedFirQuantity = value;\n                NotifyFulfillmentChanged();\n            }\n        }\n\n        public int GroupOwnedNonFirQuantity\n        {\n            get => _groupOwnedNonFirQuantity;\n            set\n            {\n                if (_groupOwnedNonFirQuantity == value) return;\n                _groupOwnedNonFirQuantity = value;\n                NotifyFulfillmentChanged();\n            }\n        }\n\n        public int OwnedTotalQuantity => OwnedFirQuantity + OwnedNonFirQuantity;\n        private int EffectiveOwnedFirQuantity => IsAlternativeGroupMember ? GroupOwnedFirQuantity : OwnedFirQuantity;\n        private int EffectiveOwnedNonFirQuantity => IsAlternativeGroupMember ? GroupOwnedNonFirQuantity : OwnedNonFirQuantity;\n\n        public ItemFulfillmentStatus FulfillmentStatus => ItemRequirementFulfillment.GetStatus(\n            TotalCount,\n            TotalFIRCount,\n            EffectiveOwnedFirQuantity,\n            EffectiveOwnedNonFirQuantity);\n\n        public double ProgressPercent => ItemRequirementFulfillment.GetProgressPercent(\n            TotalCount,\n            TotalFIRCount,\n            EffectiveOwnedFirQuantity,\n            EffectiveOwnedNonFirQuantity);\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsViewModels.cs",
    """        public string QuestDisplay => QuestCount > 0 ? FormatCountDisplay(QuestCount, QuestFIRCount) : \"0\";\n        public string HideoutDisplay => HideoutCount > 0 ? FormatCountDisplay(HideoutCount, HideoutFIRCount) : \"0\";\n        public string TotalDisplay => FormatCountDisplay(TotalCount, TotalFIRCount);\n""",
    """        public string QuestDisplay => IsAlternativeGroupMember\n            ? QuestCount > 0 ? $\"묶음 {FormatCountDisplay(QuestCount, QuestFIRCount)}\" : \"0\"\n            : QuestCount > 0 ? FormatCountDisplay(QuestCount, QuestFIRCount) : \"0\";\n        public string HideoutDisplay => HideoutCount > 0 ? FormatCountDisplay(HideoutCount, HideoutFIRCount) : \"0\";\n        public string TotalDisplay => IsAlternativeGroupMember\n            ? TotalCount > 0 ? $\"묶음 {FormatCountDisplay(TotalCount, TotalFIRCount)}\" : \"0\"\n            : FormatCountDisplay(TotalCount, TotalFIRCount);\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsViewModels.cs",
    """        public event PropertyChangedEventHandler? PropertyChanged;\n        protected void OnPropertyChanged(string propertyName) =>\n            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));\n""",
    """        private void NotifyFulfillmentChanged()\n        {\n            OnPropertyChanged(nameof(FulfillmentStatus));\n            OnPropertyChanged(nameof(ProgressPercent));\n            OnPropertyChanged(nameof(IsFulfilled));\n            OnPropertyChanged(nameof(FulfilledVisibility));\n            OnPropertyChanged(nameof(ItemOpacity));\n            OnPropertyChanged(nameof(NameTextDecorations));\n        }\n\n        public event PropertyChangedEventHandler? PropertyChanged;\n        protected void OnPropertyChanged(string propertyName) =>\n            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsViewModels.cs",
    """        public string ItemNormalizedName { get; set; } = string.Empty;\n        public IReadOnlyList<string> AlternativeItemKeys { get; set; } = Array.Empty<string>();\n        public string? IconLink { get; set; }\n""",
    """        public string ItemNormalizedName { get; set; } = string.Empty;\n        public string RequirementLookupKey { get; set; } = string.Empty;\n        public IReadOnlyList<string> AlternativeItemKeys { get; set; } = Array.Empty<string>();\n        public bool IsAlternativeGroupMember { get; set; }\n        public bool IsAlternativeGroupFirst { get; set; }\n        public string AlternativeGroupHeaderText { get; set; } = string.Empty;\n        public string? IconLink { get; set; }\n""",
)

# Item list group marker and indentation.
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml",
    """            <Border x:Name=\"ItemBorder\" Padding=\"12,6\" BorderThickness=\"0,0,0,1\"\n                    BorderBrush=\"{StaticResource BorderBrush}\"\n                    Background=\"Transparent\"\n                    Opacity=\"{Binding ItemOpacity}\">\n""",
    """            <Border x:Name=\"ItemBorder\" Padding=\"12,6\" BorderThickness=\"0,0,0,1\"\n                    BorderBrush=\"{StaticResource BorderBrush}\"\n                    Background=\"Transparent\"\n                    Margin=\"{Binding ItemIndent}\"\n                    Opacity=\"{Binding ItemOpacity}\">\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml",
    """                    <StackPanel Grid.Column=\"1\" Grid.Row=\"0\" Margin=\"12,0,8,0\" VerticalAlignment=\"Center\">\n                        <StackPanel Orientation=\"Horizontal\">\n""",
    """                    <StackPanel Grid.Column=\"1\" Grid.Row=\"0\" Margin=\"12,0,8,0\" VerticalAlignment=\"Center\">\n                        <TextBlock Text=\"{Binding AlternativeGroupHeaderText}\"\n                                   Visibility=\"{Binding AlternativeGroupHeaderVisibility}\"\n                                   Foreground=\"{StaticResource AccentBrush}\"\n                                   FontSize=\"{DynamicResource FontSizeTiny}\"\n                                   FontWeight=\"SemiBold\" Margin=\"0,0,0,4\"/>\n                        <StackPanel Orientation=\"Horizontal\">\n""",
)

# Item page inventory refresh, category order, and separated group quantities.
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    """                    if (vm.AlternativeItemKeys.Count > 0)\n                    {\n                        vm.OwnedFirQuantity = vm.AlternativeItemKeys.Sum(_inventoryService.GetFirQuantity);\n                        vm.OwnedNonFirQuantity = vm.AlternativeItemKeys.Sum(_inventoryService.GetNonFirQuantity);\n                    }\n                    else\n                    {\n                        var inventory = _inventoryService.GetInventory(vm.ItemNormalizedName);\n                        vm.OwnedFirQuantity = inventory.FirQuantity;\n                        vm.OwnedNonFirQuantity = inventory.NonFirQuantity;\n                    }\n""",
    """                    RefreshOwnedQuantities(vm);\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    """                    if (vm.AlternativeItemKeys.Count > 0)\n                    {\n                        vm.OwnedFirQuantity = vm.AlternativeItemKeys.Sum(_inventoryService.GetFirQuantity);\n                        vm.OwnedNonFirQuantity = vm.AlternativeItemKeys.Sum(_inventoryService.GetNonFirQuantity);\n                    }\n                    else\n                    {\n                        var inventory = _inventoryService.GetInventory(vm.ItemNormalizedName);\n                        vm.OwnedFirQuantity = inventory.FirQuantity;\n                        vm.OwnedNonFirQuantity = inventory.NonFirQuantity;\n                    }\n""",
    """                    RefreshOwnedQuantities(vm);\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    """            // Sort categories alphabetically by localized name\n            foreach (var category in _allCategories.OrderBy(c => _loc.GetCategoryName(c)))\n""",
    """            foreach (var category in _allCategories\n                         .OrderBy(UiSortOrder.GetItemCategoryRank)\n                         .ThenBy(c => _loc.GetCategoryName(c), StringComparer.CurrentCulture))\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    "            var questSources = _itemsDataService.GetQuestSources(itemVm.ItemNormalizedName);\n",
    "            var questSources = _itemsDataService.GetQuestSources(itemVm.RequirementLookupKey);\n",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    """            var hideoutSources = _itemsDataService.GetHideoutSources(itemVm.ItemNormalizedName);\n            HideoutRequirementsList.ItemsSource = hideoutSources;\n""",
    """            var hideoutSources = itemVm.IsAlternativeGroupMember\n                ? new List<HideoutItemSourceViewModel>()\n                : _itemsDataService.GetHideoutSources(itemVm.ItemNormalizedName);\n            HideoutRequirementsList.ItemsSource = hideoutSources;\n""",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    "            var questSources = _itemsDataService.GetQuestSources(_selectedItem.ItemNormalizedName);\n",
    "            var questSources = _itemsDataService.GetQuestSources(_selectedItem.RequirementLookupKey);\n",
)
replace_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    """            var hideoutSources = _itemsDataService.GetHideoutSources(_selectedItem.ItemNormalizedName);\n            HideoutRequirementsList.ItemsSource = hideoutSources;\n""",
    """            var hideoutSources = _selectedItem.IsAlternativeGroupMember\n                ? new List<HideoutItemSourceViewModel>()\n                : _itemsDataService.GetHideoutSources(_selectedItem.ItemNormalizedName);\n            HideoutRequirementsList.ItemsSource = hideoutSources;\n""",
)
regex_once(
    "TarkovHelper/Pages/ItemsPage.xaml.cs",
    r"        private void AdjustInventoryQuantity\(AggregatedItemViewModel item, int delta, bool fir\)\n        \{.*?\n        \}\n\n        private void UpdateDetailInventoryDisplay",
    '''        private void AdjustInventoryQuantity(AggregatedItemViewModel item, int delta, bool fir)
        {
            if (string.IsNullOrWhiteSpace(item.ItemNormalizedName) || delta == 0)
                return;

            if (fir)
                _inventoryService.AdjustFirQuantity(item.ItemNormalizedName, delta);
            else
                _inventoryService.AdjustNonFirQuantity(item.ItemNormalizedName, delta);

            RefreshOwnedQuantities(item);
        }

        private void SetInventoryQuantity(AggregatedItemViewModel item, int quantity, bool fir)
        {
            if (string.IsNullOrWhiteSpace(item.ItemNormalizedName))
                return;

            if (fir)
                _inventoryService.SetFirQuantity(item.ItemNormalizedName, quantity);
            else
                _inventoryService.SetNonFirQuantity(item.ItemNormalizedName, quantity);

            RefreshOwnedQuantities(item);
        }

        private void RefreshOwnedQuantities(AggregatedItemViewModel item)
        {
            item.OwnedFirQuantity = _inventoryService.GetFirQuantity(item.ItemNormalizedName);
            item.OwnedNonFirQuantity = _inventoryService.GetNonFirQuantity(item.ItemNormalizedName);

            if (!item.IsAlternativeGroupMember)
            {
                item.GroupOwnedFirQuantity = item.OwnedFirQuantity;
                item.GroupOwnedNonFirQuantity = item.OwnedNonFirQuantity;
                return;
            }

            var groupFir = item.AlternativeItemKeys.Sum(_inventoryService.GetFirQuantity);
            var groupNonFir = item.AlternativeItemKeys.Sum(_inventoryService.GetNonFirQuantity);
            foreach (var member in _allItemViewModels.Where(candidate =>
                         candidate.IsAlternativeGroupMember &&
                         string.Equals(candidate.RequirementLookupKey, item.RequirementLookupKey, StringComparison.OrdinalIgnoreCase)))
            {
                member.GroupOwnedFirQuantity = groupFir;
                member.GroupOwnedNonFirQuantity = groupNonFir;
            }
        }

        private void UpdateDetailInventoryDisplay'''
)

# Range requirements consume only their isolated group item keys.
replace_once(
    "TarkovHelper/Services/InventoryConsumptionService.cs",
    """            .Select(item => new InventoryConsumptionRequirement(\n                item.ItemNormalizedName,\n                item.Amount,\n                item.FoundInRaid,\n                item.IsAlternativeGroup ? item.AlternativeItemIds : null))\n""",
    """            .Select(item => new InventoryConsumptionRequirement(\n                item.IsAlternativeGroup\n                    ? QuestRequirementInventoryKey.BuildGroupKey(task, item)\n                    : item.ItemNormalizedName,\n                item.Amount,\n                item.FoundInRaid,\n                item.IsAlternativeGroup\n                    ? QuestRequirementInventoryKey.BuildAlternativeItemKeys(task, item)\n                    : null))\n""",
)

# Quest detail displays each range alternative as its own indented item row.
replace_once(
    "TarkovHelper/Pages/QuestListViewModels.cs",
    """        public string DisplayText { get; set; } = string.Empty;\n        public bool FoundInRaid { get; set; }\n""",
    """        public string DisplayText { get; set; } = string.Empty;\n        public string GroupHeaderText { get; set; } = string.Empty;\n        public Visibility GroupHeaderVisibility { get; set; } = Visibility.Collapsed;\n        public Thickness ItemMargin { get; set; } = new(0, 4, 0, 4);\n        public bool FoundInRaid { get; set; }\n""",
)
regex_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    r"        private void LoadRequiredItems\(List<QuestItem> requiredItems\)\n        \{.*?\n        \}\n\n        /// <summary>\n        /// Handle click on item name",
    '''        private void LoadRequiredItems(TarkovTask task)
        {
            var itemVms = new List<RequiredItemViewModel>();
            foreach (var item in task.RequiredItems ?? [])
            {
                if (_isUnloaded)
                    return;

                if (item.IsAlternativeGroup)
                {
                    var keys = QuestRequirementInventoryKey.BuildAlternativeItemKeys(task, item);
                    var ownedFir = keys.Sum(_inventoryService.GetFirQuantity);
                    var ownedTotal = keys.Sum(_inventoryService.GetTotalQuantity);
                    var fulfilled = item.FoundInRaid ? ownedFir >= item.Amount : ownedTotal >= item.Amount;

                    for (var index = 0; index < item.AlternativeItemIds.Count; index++)
                    {
                        var itemId = item.AlternativeItemIds[index];
                        var tarkovItem = GetItemByNormalizedName(itemId);
                        var displayName = index < item.AlternativeItemNames.Count &&
                                          !string.IsNullOrWhiteSpace(item.AlternativeItemNames[index])
                            ? item.AlternativeItemNames[index]
                            : GetLocalizedItemName(itemId);

                        itemVms.Add(new RequiredItemViewModel
                        {
                            GroupHeaderText = $"범위 제출 · 아래 항목 중 아무거나 {item.Amount}개",
                            GroupHeaderVisibility = index == 0 ? Visibility.Visible : Visibility.Collapsed,
                            ItemMargin = new Thickness(20, 4, 0, 4),
                            FoundInRaid = item.FoundInRaid,
                            RequirementType = item.Requirement,
                            ItemId = tarkovItem?.Id ?? string.Empty,
                            IsFulfilled = fulfilled,
                            DisplayText = displayName,
                            IconSource = !string.IsNullOrEmpty(tarkovItem?.Id)
                                ? _imageCache.GetLocalItemIcon(tarkovItem.Id)
                                : null
                        });
                    }

                    continue;
                }

                var fulfillmentInfo = _inventoryService.GetFulfillmentInfo(
                    item.ItemNormalizedName,
                    item.Amount,
                    item.FoundInRaid ? item.Amount : 0);
                var concrete = GetItemByNormalizedName(item.ItemNormalizedName, item.ItemDisplayName);
                itemVms.Add(new RequiredItemViewModel
                {
                    FoundInRaid = item.FoundInRaid,
                    RequirementType = item.Requirement,
                    ItemId = concrete?.Id ?? string.Empty,
                    IsFulfilled = fulfillmentInfo.Status == ItemFulfillmentStatus.Fulfilled,
                    DisplayText = $"{GetLocalizedItemName(item.ItemNormalizedName, item.ItemDisplayName)} x{item.Amount}",
                    IconSource = !string.IsNullOrEmpty(concrete?.Id)
                        ? _imageCache.GetLocalItemIcon(concrete.Id)
                        : null
                });
            }

            RequiredItemsList.ItemsSource = itemVms;
        }

        /// <summary>
        /// Handle click on item name'''
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    "                LoadRequiredItems(task.RequiredItems);\n",
    "                LoadRequiredItems(task);\n",
)
regex_once(
    "TarkovHelper/Pages/QuestListPage.xaml",
    r"                                        <ItemsControl x:Name=\"RequiredItemsList\">.*?                                        </ItemsControl>\n",
    '''                                        <ItemsControl x:Name="RequiredItemsList">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <StackPanel Margin="0,2">
                                                        <Border Visibility="{Binding GroupHeaderVisibility}"
                                                                Background="{StaticResource BackgroundMediumBrush}"
                                                                BorderBrush="{StaticResource AccentBrush}"
                                                                BorderThickness="3,0,0,0"
                                                                Padding="8,5" Margin="0,4,0,3">
                                                            <TextBlock Text="{Binding GroupHeaderText}"
                                                                       Foreground="{StaticResource AccentBrush}"
                                                                       FontSize="{DynamicResource FontSizeTiny}"
                                                                       FontWeight="SemiBold" TextWrapping="Wrap"/>
                                                        </Border>
                                                        <StackPanel Orientation="Horizontal"
                                                                    Margin="{Binding ItemMargin}"
                                                                    Cursor="Hand"
                                                                    Opacity="{Binding ItemOpacity}"
                                                                    MouseLeftButtonDown="ItemName_Click">
                                                            <Border Width="32" Height="32" CornerRadius="4"
                                                                    Background="{StaticResource BackgroundMediumBrush}"
                                                                    Margin="0,0,8,0">
                                                                <Grid>
                                                                    <Image Source="{Binding IconSource}" Width="28" Height="28"
                                                                           Stretch="Uniform" RenderOptions.BitmapScalingMode="HighQuality"/>
                                                                    <TextBlock Text="&#x2713;" FontSize="{DynamicResource FontSizeMedium}"
                                                                               FontWeight="Bold" Foreground="{StaticResource SuccessBrush}"
                                                                               HorizontalAlignment="Right" VerticalAlignment="Bottom"
                                                                               Visibility="{Binding FulfilledVisibility}"/>
                                                                </Grid>
                                                            </Border>
                                                            <StackPanel VerticalAlignment="Center">
                                                                <StackPanel Orientation="Horizontal">
                                                                    <TextBlock Text="{Binding DisplayText}"
                                                                               FontSize="{DynamicResource FontSizeXSmall}"
                                                                               TextDecorations="{Binding TextDecorations}"/>
                                                                    <Border Padding="4,1" CornerRadius="2" Margin="8,0,0,0"
                                                                            Background="{StaticResource WarningBrush}"
                                                                            Visibility="{Binding FirVisibility}">
                                                                        <TextBlock Text="FIR" FontSize="{DynamicResource FontSizeTiny}"
                                                                                   Foreground="White"/>
                                                                    </Border>
                                                                </StackPanel>
                                                                <TextBlock Text="{Binding RequirementType}"
                                                                           FontSize="{DynamicResource FontSizeTiny}"
                                                                           Foreground="{StaticResource TextSecondaryBrush}"
                                                                           Visibility="{Binding RequirementTypeVisibility}"/>
                                                            </StackPanel>
                                                        </StackPanel>
                                                    </StackPanel>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
'''
)

# Smoke checks: no Available state, exact categories/order, and isolated range keys.
replace_once(
    "TarkovHelper.DatabaseSmoke/Program.cs",
    """    if (eligibleStatus != QuestStatus.Available)\n        throw new InvalidDataException($\"Eligible unstarted quest must be Available: actual={eligibleStatus}.\");\n""",
    """    if (eligibleStatus != QuestStatus.Active)\n        throw new InvalidDataException($\"Eligible quest must be Active without a separate accept state: actual={eligibleStatus}.\");\n""",
)
regex_once(
    "TarkovHelper.DatabaseSmoke/Program.cs",
    r"\n    if \(!progressService.StartQuest\(statusTask\).*?    settingsService.PlayerLevel = originalPlayerLevel;\n",
    "\n    settingsService.PlayerLevel = originalPlayerLevel;\n",
)
replace_once(
    "TarkovHelper.DatabaseSmoke/Program.cs",
    """    var categories = ItemsDataService.Instance;\n    if (categories.GetParentCategory(\"Scopes\") != \"Scopes\" ||\n        categories.GetParentCategory(\"Magazines\") != \"Magazines\" ||\n        categories.GetParentCategory(\"Chest rigs\") != \"Chest rigs\" ||\n        categories.GetParentCategory(\"unrecognized-category\") != \"unrecognized-category\")\n    {\n        throw new InvalidDataException(\"Detailed item category preservation failed.\");\n    }\n""",
    """    var categories = ItemsDataService.Instance;\n    if (categories.GetParentCategory(\"Weapons\") != \"Weapons\" ||\n        categories.GetParentCategory(\"Magazines\") != \"Magazines\" ||\n        categories.GetParentCategory(\"Rounds\") != \"Ammunition\" ||\n        categories.GetParentCategory(\"Medkits\") != \"Medical\" ||\n        categories.GetParentCategory(\"Food\") != \"Food\" ||\n        categories.GetParentCategory(\"Melee weapons\") != \"Melee\" ||\n        categories.GetParentCategory(\"Scopes\") != \"Parts\" ||\n        categories.GetParentCategory(\"Grenades\") != \"Grenades\" ||\n        categories.GetParentCategory(\"Electronics\") != \"Barter\" ||\n        categories.GetParentCategory(\"Chest rigs\") != \"Rigs\" ||\n        categories.GetParentCategory(\"Eyewear\") != \"Eyewear\" ||\n        categories.GetParentCategory(\"Containers & cases\") != \"Containers\" ||\n        categories.GetParentCategory(\"Armor vests\") != \"Armor\" ||\n        categories.GetParentCategory(\"Info items\") != \"Info\" ||\n        categories.GetParentCategory(\"Keys\") != \"Keys\" ||\n        categories.GetParentCategory(\"unrecognized-category\") != \"Special\")\n    {\n        throw new InvalidDataException(\"Canonical sixteen item categories failed.\");\n    }\n\n    var categoryOrder = new[]\n    {\n        \"Weapons\", \"Magazines\", \"Ammunition\", \"Medical\", \"Food\", \"Melee\",\n        \"Parts\", \"Grenades\", \"Barter\", \"Rigs\", \"Eyewear\", \"Containers\",\n        \"Armor\", \"Info\", \"Keys\", \"Special\"\n    };\n    if (!categoryOrder.Select(UiSortOrder.GetItemCategoryRank).SequenceEqual(Enumerable.Range(0, 16)))\n        throw new InvalidDataException(\"Item category dropdown order is not canonical.\");\n\n    var rangeTask = new TarkovTask\n    {\n        Ids = [\"range-inventory-smoke\"],\n        NormalizedName = \"range-inventory-smoke\",\n        Name = \"Range Inventory Smoke\"\n    };\n    var rangeRequirement = new QuestItem\n    {\n        ItemNormalizedName = \"group:range-inventory\",\n        RequirementGroupId = \"range-inventory\",\n        IsAlternativeGroup = true,\n        AlternativeItemIds = [\"item-a\", \"item-b\", \"item-c\"],\n        Amount = 3\n    };\n    var rangeKeys = QuestRequirementInventoryKey.BuildAlternativeItemKeys(rangeTask, rangeRequirement);\n    if (rangeKeys.Count != 3 || rangeKeys.Any(key => key is \"item-a\" or \"item-b\" or \"item-c\"))\n        throw new InvalidDataException(\"Range requirement keys were not isolated from concrete item inventory.\");\n    inventory.SetNonFirQuantity(rangeKeys[1], 3);\n    inventory.SetNonFirQuantity(\"item-b\", 0);\n    if (rangeKeys.Sum(inventory.GetTotalQuantity) != 3 || inventory.GetTotalQuantity(\"item-b\") != 0)\n        throw new InvalidDataException(\"Range and concrete item inventories were not calculated independently.\");\n    foreach (var key in rangeKeys) inventory.SetNonFirQuantity(key, 0);\n""",
)

print("phase2 applied")
