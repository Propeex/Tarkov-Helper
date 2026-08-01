namespace TarkovHelper.Services;

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
            ["Special"] = "특수",
            [ItemCategoryClassifier.RangeSubmission] = "범위 제출"
        };

    public string GetCategoryName(string categoryKey) =>
        CategoryNamesKo.TryGetValue(categoryKey, out var translated)
            ? translated
            : CategoryNamesKo["Special"];
}
