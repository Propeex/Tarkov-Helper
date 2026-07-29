using System.Text.RegularExpressions;

namespace TarkovHelper.Models.Map;

/// <summary>
/// 맵의 개별 층(레벨) 설정.
/// SVG 파일 내의 &lt;g id="..."&gt; 레이어를 제어하는 데 사용됩니다.
/// </summary>
public sealed class MapFloorConfig
{
    private string _displayName = string.Empty;

    /// <summary>
    /// SVG에서 해당 층을 식별하는 그룹 ID (예: "basement", "main", "level2")
    /// </summary>
    public string LayerId { get; set; } = string.Empty;

    /// <summary>
    /// UI에 표시될 층 이름. 원본 설정이 영어여도 한국어 층 표기로 변환합니다.
    /// </summary>
    public string DisplayName
    {
        get => GetLocalizedDisplayName(_displayName, LayerId, Order);
        set => _displayName = value ?? string.Empty;
    }

    /// <summary>
    /// 층 순서 (낮을수록 아래층, 0이 기본 층)
    /// </summary>
    public int Order { get; set; } = 0;

    /// <summary>
    /// 기본으로 표시할 층인지 여부
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// 영문 표시명과 레이어 ID를 한국어 층 이름으로 변환합니다.
    /// 알 수 없는 이름도 Order를 사용해 서로 구분되는 한국어 표기를 만듭니다.
    /// </summary>
    public static string GetLocalizedDisplayName(string? displayName, string? layerId, int order)
    {
        var original = displayName?.Trim() ?? string.Empty;
        if (original.Any(character => character is >= '가' and <= '힣'))
            return original;

        var key = $"{layerId} {original}"
            .ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);

        if (ContainsAny(key, "basement3", "underground3", "b3"))
            return "지하 3층";
        if (ContainsAny(key, "basement2", "underground2", "b2"))
            return "지하 2층";
        if (ContainsAny(key, "basement", "underground", "lower", "b1"))
            return "지하 1층";
        if (ContainsAny(key, "rooftop", "roof"))
            return "옥상";
        if (ContainsAny(key, "mainfloor", "groundfloor", "ground", "main", "level0", "floor0", "0f"))
            return "지상층";

        var number = ExtractFloorNumber(key);
        if (number.HasValue)
            return number.Value <= 0 ? "지상층" : $"{number.Value}층";

        if (order < 0)
            return $"지하 {Math.Abs(order)}층";
        if (order == 0)
            return "지상층";

        // Order 1은 기본층 바로 위인 2층으로 취급합니다.
        return $"{order + 1}층";
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static int? ExtractFloorNumber(string value)
    {
        // LayerId와 DisplayName을 함께 검사하므로 "level2level2"처럼 같은 번호가
        // 두 번 나타날 수 있습니다. 모든 숫자를 이어 붙이지 말고 첫 번째 층 번호만 사용합니다.
        var match = Regex.Match(value, @"(?:level|floor)(?<number>\d+)|(?<number>\d+)(?:f|층)?");
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? number
            : null;
    }
}