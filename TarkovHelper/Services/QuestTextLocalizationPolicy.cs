namespace TarkovHelper.Services;

/// <summary>
/// 퀘스트 제목과 목표 내용에 적용되는 표시 정책입니다.
/// 제목은 원문을 유지하고, 목표 내용은 영어 원문에 대해서만 한국어 번역을 사용합니다.
/// </summary>
internal static class QuestTextLocalizationPolicy
{
    public static string PreserveTitle(string? originalTitle)
    {
        return originalTitle ?? string.Empty;
    }

    public static string? SelectContent(
        string? originalContent,
        string? koreanTranslation)
    {
        if (string.IsNullOrWhiteSpace(originalContent))
        {
            return string.IsNullOrWhiteSpace(koreanTranslation)
                ? originalContent
                : koreanTranslation;
        }

        // 이미 한글인 내용은 제목 언어와 관계없이 원문을 그대로 유지합니다.
        if (ContainsHangul(originalContent))
            return originalContent;

        // 한글이 아닌 원문은 한국어 번역이 있을 때만 번역문을 사용합니다.
        return string.IsNullOrWhiteSpace(koreanTranslation)
            ? originalContent
            : koreanTranslation;
    }

    private static bool ContainsHangul(string value)
    {
        foreach (var character in value)
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
}
