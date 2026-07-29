using System.Runtime.CompilerServices;
using TarkovHelper.Services;

internal static class QuestKoreanSourceSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        AssertEqual(
            "한글 제목",
            QuestKoreanSourcePolicy.SelectQuestTitle("English title", "한글 제목"),
            "공식 한국어 제목을 선택해야 합니다.");

        AssertEqual(
            "English title",
            QuestKoreanSourcePolicy.SelectQuestTitle("English title", "English title"),
            "NameKO의 영문 fallback을 한국어 원문으로 오인하면 안 됩니다.");

        AssertEqual(
            "한글 내용",
            QuestKoreanSourcePolicy.SelectQuestContent("English content", "한글 내용"),
            "공식 한국어 목표 내용을 선택해야 합니다.");

        AssertEqual(
            "English content",
            QuestKoreanSourcePolicy.SelectQuestContent("English content", "English content"),
            "공식 한국어 내용이 없으면 영문 원문을 사용해야 합니다.");

        AssertEqual(
            "English content",
            QuestKoreanSourcePolicy.SelectQuestContent("English content", null),
            "한국어 필드가 없으면 영문 원문을 사용해야 합니다.");

        if (!QuestKoreanSourcePolicy.ContainsHangul("A Helping Hand - 목표 완료") ||
            QuestKoreanSourcePolicy.ContainsHangul("Shipping Delay - Part 1"))
        {
            throw new InvalidOperationException("한글 원문 감지 회귀 검사에 실패했습니다.");
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
