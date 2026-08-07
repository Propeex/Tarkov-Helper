namespace TarkovHelper.Services;

/// <summary>
/// Classifies task objectives that reference ordinary inventory items.
/// Quest-item objectives use virtual raid objects and are deliberately excluded.
/// </summary>
internal static class QuestRequiredItemObjectivePolicy
{
    public static QuestItemTrackingKind Classify(string? objectiveType)
    {
        var normalized = Normalize(objectiveType);
        return normalized switch
        {
            "handover" or "giveitem" or "plantitem" or "mark" or "useitem"
                => QuestItemTrackingKind.Consumable,
            "finditem" or "collect"
                => QuestItemTrackingKind.TrackOnly,
            _ => QuestItemTrackingKind.None
        };
    }

    public static bool IsConsumable(string? objectiveType) =>
        Classify(objectiveType) == QuestItemTrackingKind.Consumable;

    public static bool IsTrackOnly(string? objectiveType) =>
        Classify(objectiveType) == QuestItemTrackingKind.TrackOnly;

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}

internal enum QuestItemTrackingKind
{
    None,
    TrackOnly,
    Consumable
}
