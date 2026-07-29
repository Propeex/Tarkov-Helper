namespace TarkovHelper.Services;

/// <summary>
/// tarkov.dev와 데이터베이스에 저장된 공식 한국어 원문을 선택합니다.
/// 런타임 번역이나 외부 서비스 호출은 수행하지 않습니다.
/// </summary>
public static class QuestKoreanSourcePolicy
{
    /// <summary>
    /// 완성형과 자모를 포함한 실제 한글 문자열인지 확인합니다.
    /// 한국어 필드에 들어간 영문 fallback을 한국어 원문으로 오인하지 않기 위해 사용합니다.
    /// </summary>
    public static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var character in text)
        {
            if (character is >= '\u1100' and <= '\u11FF' ||
                character is >= '\u3130' and <= '\u318F' ||
                character is >= '\uA960' and <= '\uA97F' ||
                character is >= '\uAC00' and <= '\uD7A3' ||
                character is >= '\uD7B0' and <= '\uD7FF')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 공식 한국어 원문이 있으면 그대로 사용하고, 없으면 영문 원문을 사용합니다.
    /// </summary>
    public static string Select(string? englishText, string? koreanText)
    {
        if (ContainsHangul(koreanText))
            return koreanText!.Trim();

        if (!string.IsNullOrWhiteSpace(englishText))
            return englishText.Trim();

        return koreanText?.Trim() ?? string.Empty;
    }

    public static string SelectQuestTitle(string? englishTitle, string? koreanTitle) =>
        Select(englishTitle, koreanTitle);

    public static string SelectQuestContent(string? englishContent, string? koreanContent) =>
        Select(englishContent, koreanContent);
}
