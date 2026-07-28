namespace TarkovHelper.Services;

/// <summary>
/// Database rebuild progress reported to the settings UI.
/// </summary>
public sealed record DatabaseBuildProgress(
    string Stage,
    string Message,
    double Percent,
    int Current,
    int? Total,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    public string ToDisplayText()
    {
        var percentText = $"{Math.Clamp(Percent, 0, 100):F0}%";
        var countText = Total is > 0
            ? $" · {Current:N0}/{Total.Value:N0}"
            : Current > 0
                ? $" · {Current:N0}개"
                : string.Empty;
        var etaText = EstimatedRemaining.HasValue
            ? $" · 예상 {FormatDuration(EstimatedRemaining.Value)} 남음"
            : string.Empty;

        return $"{percentText} · {Message}{countText}{etaText}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}시간 {duration.Minutes}분";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}분 {duration.Seconds}초";
        return $"{Math.Max(1, duration.Seconds)}초";
    }
}
