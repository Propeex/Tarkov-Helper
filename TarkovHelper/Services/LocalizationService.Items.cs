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

    #region Items Page - Loading

    public string ItemsLoading => "아이템 데이터 로딩 중...";

    #endregion

    #region Item Categories - Parent Categories

    /// <summary>
    /// Get localized category name. Returns English name as fallback for unknown categories.
    /// </summary>
    public string GetCategoryName(string categoryKey)
    {
        return GetCategoryNameKO(categoryKey);
    }

    private static readonly IReadOnlyDictionary<string, string> CategoryNamesKo =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["All Categories"] = "전체 카테고리", ["Food"] = "식품", ["Drinks"] = "음료",
            ["Medkits"] = "의료 키트", ["Medical supplies"] = "의료용품", ["Injury treatment"] = "부상 치료제",
            ["Stimulants"] = "주사제", ["Drugs"] = "의약품", ["Electronics"] = "전자제품",
            ["Building materials"] = "건축 자재", ["Flammable materials"] = "가연성 물질", ["Energy elements"] = "에너지 부품",
            ["Household goods"] = "생활용품", ["Tools"] = "공구", ["Valuables"] = "귀중품", ["Other"] = "기타",
            ["Info items"] = "정보 아이템", ["Keys"] = "열쇠", ["Keycards"] = "키카드", ["Maps"] = "지도",
            ["Extraction intel"] = "탈출 정보", ["Notes"] = "문서", ["Weapons"] = "무기", ["Rounds"] = "탄약",
            ["Ammo boxes"] = "탄약 상자", ["Shrapnel"] = "파편", ["Magazines"] = "탄창", ["Mounts"] = "마운트",
            ["Stocks & chassis"] = "개머리판·섀시", ["Handguards"] = "핸드가드", ["Barrels"] = "총열",
            ["Flash hiders & muzzle brakes"] = "소염기·제퇴기", ["Suppressors"] = "소음기", ["Muzzle adapters"] = "총구 어댑터",
            ["Iron sights"] = "기계식 조준기", ["Pistol grips"] = "권총 손잡이", ["Receivers and slides"] = "리시버·슬라이드",
            ["Charging handles"] = "장전 손잡이", ["Gas blocks"] = "가스 블록", ["Foregrips"] = "전방 손잡이",
            ["Auxiliary parts"] = "보조 부품", ["Bipods"] = "양각대", ["Underbarrel grenade launchers"] = "총열 하부 유탄발사기",
            ["Scopes"] = "조준경", ["Assault scopes"] = "돌격 조준경", ["Reflex sights"] = "도트 사이트",
            ["Compact reflex sights"] = "소형 도트 사이트", ["Night vision scopes"] = "야간 조준경",
            ["Thermal vision sights"] = "열화상 조준경", ["Flashlights"] = "손전등", ["Tactical combo devices"] = "전술 복합 장치",
            ["Armor vests"] = "방탄복", ["Armor plates"] = "방탄판", ["Chest rigs"] = "전술 조끼", ["Backpacks"] = "배낭",
            ["Headwear"] = "머리 장비", ["Eyewear"] = "안경", ["Face cover"] = "안면 장비", ["Earpieces"] = "헤드셋",
            ["Armbands"] = "완장", ["Special equipment"] = "특수 장비", ["Helmet mods"] = "헬멧 부품",
            ["Containers & cases"] = "보관함·케이스", ["Secure containers"] = "보안 컨테이너", ["Money"] = "화폐",
            ["Quest Items"] = "퀘스트 아이템", ["Dogtag"] = "군번줄", ["Posters"] = "포스터"
        };

    private static string GetCategoryNameKO(string key) =>
        CategoryNamesKo.TryGetValue(key, out var translated) ? translated : key;

    #endregion
}
