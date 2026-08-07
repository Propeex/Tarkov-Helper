namespace TarkovHelper.Models;

/// <summary>
/// Strict parser for persisted quest progress. Unknown or removed values are
/// rejected instead of being coerced into a runtime state.
/// </summary>
internal static class QuestStatusPersistence
{
    public static bool TryParse(string? value, out QuestStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(value) &&
               Enum.TryParse(value, ignoreCase: true, out status) &&
               Enum.IsDefined(status);
    }
}
