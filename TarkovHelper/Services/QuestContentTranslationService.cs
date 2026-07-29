using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// 이전 호출부와의 호환을 위한 얇은 선택기입니다.
/// 외부 번역 API, 번역 캐시, 네트워크 요청은 사용하지 않습니다.
/// </summary>
[Obsolete("Use QuestKoreanSourcePolicy. Runtime quest translation has been removed.")]
public sealed class QuestContentTranslationService
{
    private static readonly QuestContentTranslationService SharedInstance = new();

    public static QuestContentTranslationService Instance => SharedInstance;

    private QuestContentTranslationService()
    {
    }

    public static bool ContainsHangul(string? text) =>
        QuestKoreanSourcePolicy.ContainsHangul(text);

    public static string SelectQuestTitle(string? englishTitle, string? koreanTitle) =>
        QuestKoreanSourcePolicy.SelectQuestTitle(englishTitle, koreanTitle);

    public static string SelectQuestContent(string? englishContent, string? koreanContent) =>
        QuestKoreanSourcePolicy.SelectQuestContent(englishContent, koreanContent);

    /// <summary>
    /// 자동 번역이 제거되어 아무 작업도 수행하지 않습니다.
    /// 기존 바이너리 호환을 위해서만 유지합니다.
    /// </summary>
    public Task TranslateMissingAsync(
        IReadOnlyCollection<QuestObjective> objectives,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 자동 번역이 제거되어 아무 작업도 수행하지 않습니다.
    /// 기존 호출부 호환을 위해서만 유지합니다.
    /// </summary>
    public Task TranslateMissingAsync(
        IEnumerable<TarkovTask> tasks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
