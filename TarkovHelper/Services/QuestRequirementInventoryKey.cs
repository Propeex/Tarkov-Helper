using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Builds inventory keys for range/alternative quest requirements. These keys
/// are intentionally isolated from normal item keys so a physical item tracked
/// for a concrete requirement never satisfies a range requirement implicitly.
/// </summary>
public static class QuestRequirementInventoryKey
{
    public static string BuildGroupKey(TarkovTask task, QuestItem requirement)
    {
        var questKey = task.Ids?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                       ?? task.NormalizedName
                       ?? task.Name;
        var groupKey = requirement.RequirementGroupId
                       ?? requirement.ItemNormalizedName
                       ?? "range";

        return $"range:{Normalize(questKey)}:{Normalize(groupKey)}";
    }

    public static string BuildAlternativeItemKey(
        TarkovTask task,
        QuestItem requirement,
        string alternativeItemId) =>
        $"{BuildGroupKey(task, requirement)}:{Normalize(alternativeItemId)}";

    public static IReadOnlyList<string> BuildAlternativeItemKeys(
        TarkovTask task,
        QuestItem requirement) =>
        requirement.AlternativeItemIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => BuildAlternativeItemKey(task, requirement, value))
            .ToArray();

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());

        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

        return normalized.Trim('-');
    }
}
