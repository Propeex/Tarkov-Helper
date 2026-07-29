namespace TarkovHelper.Services;

/// <summary>
/// 제거된 자동 번역 서비스의 기존 정적 호출부를 위한 임시 호환 선택기입니다.
/// 네트워크 요청, 번역 처리, 캐시 저장 기능은 없습니다.
/// </summary>
[Obsolete("Use QuestKoreanSourcePolicy. Runtime quest translation has been removed.")]
public static class QuestContentTranslationService
{
    public static bool ContainsHangul(string? text) =>
        QuestKoreanSourcePolicy.ContainsHangul(text);

    public static string SelectQuestTitle(string? englishTitle, string? koreanTitle) =>
        QuestKoreanSourcePolicy.SelectQuestTitle(englishTitle, koreanTitle);

    public static string SelectQuestContent(string? englishContent, string? koreanContent) =>
        QuestKoreanSourcePolicy.SelectQuestContent(englishContent, koreanContent);
}
