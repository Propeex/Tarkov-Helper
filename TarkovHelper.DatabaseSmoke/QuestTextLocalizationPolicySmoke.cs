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

        AssertTitle("English Quest", "번역된 제목");
        AssertTitle("한글 퀘스트", "다른 한글 제목");
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

    private static void AssertTitle(string original, string translated)
    {
        var task = new TarkovTask
        {
            Name = original,
            NameKo = translated
        };

        if (!string.Equals(task.Name, original, StringComparison.Ordinal) || task.NameKo != null)
        {
            throw new InvalidDataException(
                $"Quest title localization policy failed: original='{original}', NameKo='{task.NameKo}'.");
        }
    }
}
