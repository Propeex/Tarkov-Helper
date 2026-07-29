namespace TarkovHelper.Services;

/// <summary>
/// 기존 지도 모델과의 호환을 위한 퀘스트 텍스트 선택 정책입니다.
/// 공식 한국어 원문이 있으면 사용하고, 없으면 영문 원문을 사용합니다.
/// </summary>
internal static class QuestTextLocalizationPolicy
{
    public static string PreserveTitle(string? originalTitle)
    {
        return originalTitle?.Trim() ?? string.Empty;
    }

    public static string? SelectContent(
        string? englishContent,
        string? koreanContent)
    {
        var selected = QuestKoreanSourcePolicy.SelectQuestContent(
            englishContent,
            koreanContent);

        return string.IsNullOrWhiteSpace(selected) ? null : selected;
    }
}
