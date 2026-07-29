using System.Runtime.CompilerServices;
using TarkovHelper.Models;
using TarkovHelper.Services;

internal static class QuestTextLocalizationPolicySmoke
{
    [ModuleInitializer]
    internal static void ValidateQuestTextLocalizationPolicy()
    {
        AssertContent(
            original: "Complete the objective",
            korean: "목표를 완료하십시오",
            expected: "목표를 완료하십시오",
            caseName: "English content is translated");

        AssertContent(
            original: "이미 한글인 목표 내용",
            korean: "다시 번역된 목표 내용",
            expected: "이미 한글인 목표 내용",
            caseName: "Korean content is preserved");

        AssertContent(
            original: "한글 내용 with an English term",
            korean: "혼합 문장 재번역",
            expected: "한글 내용 with an English term",
            caseName: "Mixed Korean content is preserved");

        AssertContent(
            original: "No translation available",
            korean: null,
            expected: "No translation available",
            caseName: "Missing Korean translation falls back to original");

        AssertTitleUnchanged(
            sourceTitle: "English Quest",
            koreanTitle: null,
            sourceContent: "Complete the objective",
            koreanContent: "목표를 완료하십시오");

        AssertTitleUnchanged(
            sourceTitle: "한글 퀘스트",
            koreanTitle: null,
            sourceContent: "Complete the objective",
            koreanContent: "목표를 완료하십시오");

        AssertTitleUnchanged(
            sourceTitle: "English Source Quest",
            koreanTitle: "표시 중인 한글 퀘스트",
            sourceContent: "이미 한글인 목표 내용",
            koreanContent: "다시 번역된 목표 내용");
    }

    private static void AssertContent(
        string? original,
        string? korean,
        string? expected,
        string caseName)
    {
        var actual = QuestTextLocalizationPolicy.SelectContent(original, korean);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Quest content localization policy failed ({caseName}): " +
                $"expected='{expected}', actual='{actual}'.");
        }
    }

    private static void AssertTitleUnchanged(
        string sourceTitle,
        string? koreanTitle,
        string sourceContent,
        string? koreanContent)
    {
        var task = new TarkovTask
        {
            Name = sourceTitle,
            NameKo = koreanTitle,
            Objectives = [sourceContent]
        };

        var titleBefore = QuestContentTranslationService.SelectQuestTitle(task.Name, task.NameKo);
        task.Objectives[0] = QuestTextLocalizationPolicy.SelectContent(
            task.Objectives[0],
            koreanContent) ?? task.Objectives[0];
        var titleAfter = QuestContentTranslationService.SelectQuestTitle(task.Name, task.NameKo);

        if (!string.Equals(titleAfter, titleBefore, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Quest content translation changed the title: before='{titleBefore}', after='{titleAfter}'.");
        }
    }
}
