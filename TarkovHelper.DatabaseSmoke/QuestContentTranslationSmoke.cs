using System.Runtime.CompilerServices;
using TarkovHelper.Services;

internal static class QuestContentTranslationSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        AssertEqual(
            "한글 제목",
            QuestContentTranslationService.SelectQuestTitle("English title", "한글 제목"),
            "실제 한글 제목은 유지해야 합니다.");

        AssertEqual(
            "English title",
            QuestContentTranslationService.SelectQuestTitle("English title", "English title"),
            "NameKO의 영어 fallback을 한국어 제목으로 오인하면 안 됩니다.");

        AssertEqual(
            "한글 내용",
            QuestContentTranslationService.SelectQuestContent("English content", "한글 내용"),
            "실제 한글 내용은 유지해야 합니다.");

        AssertEqual(
            "English content",
            QuestContentTranslationService.SelectQuestContent("English content", "English content"),
            "DescriptionKO의 영어 fallback은 자동 번역 대상으로 남겨야 합니다.");

        if (!QuestContentTranslationService.ContainsHangul("A Helping Hand - 목표 완료") ||
            QuestContentTranslationService.ContainsHangul("Shipping Delay - Part 1"))
        {
            throw new InvalidOperationException("한글 감지 회귀 검사에 실패했습니다.");
        }
    }

    private static void AssertEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{message} Expected='{expected}', Actual='{actual}'");
        }
    }
}
