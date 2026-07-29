namespace TarkovHelper.Services;

/// <summary>
/// Identifies quest objectives that actually spend items when the quest is completed.
/// Acquisition, collection and catalogue objectives describe progress only and must
/// not be duplicated in QuestRequiredItems.
/// </summary>
internal static class QuestRequiredItemObjectivePolicy
{
    public static bool IsConsumable(string? objectiveType)
    {
        if (string.IsNullOrWhiteSpace(objectiveType))
            return false;

        var normalized = new string(
            objectiveType
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        return normalized is "handover" or "giveitem";
    }
}
