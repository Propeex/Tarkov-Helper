using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Applies irreversible item spending when a quest or hideout level is completed.
/// Progress resets do not refund items because the materials were already consumed
/// in the game.
/// </summary>
internal sealed class InventoryConsumptionService
{
    private static readonly ILogger _log = Log.For<InventoryConsumptionService>();
    private static InventoryConsumptionService? _instance;
    public static InventoryConsumptionService Instance =>
        _instance ??= new InventoryConsumptionService();

    private InventoryConsumptionService()
    {
    }

    public void ConsumeQuestRequirements(TarkovTask task)
    {
        if (task.RequiredItems is not { Count: > 0 })
            return;

        var requirements = task.RequiredItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemNormalizedName) && item.Amount > 0)
            .GroupBy(
                item => (Key: item.ItemNormalizedName, FirOnly: item.FoundInRaid),
                new RequirementKeyComparer())
            .Select(group => new InventoryConsumptionRequirement(
                group.Key.Key,
                group.Sum(item => item.Amount),
                group.Key.FirOnly))
            .ToList();

        Consume($"quest:{task.Ids?.FirstOrDefault() ?? task.NormalizedName ?? task.Name}", requirements);
    }

    public void ConsumeHideoutLevels(HideoutModule module, int previousLevel, int newLevel)
    {
        if (newLevel <= previousLevel)
            return;

        var requirements = module.Levels
            .Where(level => level.Level > previousLevel && level.Level <= newLevel)
            .SelectMany(level => level.ItemRequirements)
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemNormalizedName) && item.Count > 0)
            .GroupBy(
                item => (Key: item.ItemNormalizedName, FirOnly: item.FoundInRaid),
                new RequirementKeyComparer())
            .Select(group => new InventoryConsumptionRequirement(
                group.Key.Key,
                group.Sum(item => item.Count),
                group.Key.FirOnly))
            .ToList();

        Consume($"hideout:{module.NormalizedName}:{previousLevel}->{newLevel}", requirements);
    }

    private void Consume(string source, IReadOnlyCollection<InventoryConsumptionRequirement> requirements)
    {
        if (requirements.Count == 0)
            return;

        // ItemInventoryService is recreated after a database/profile refresh. Resolve
        // it at the point of use so this process-lifetime service never keeps a stale
        // inventory instance and silently deducts from an object no longer used by UI.
        var inventory = ItemInventoryService.Instance;

        // FIR-only requirements are processed first so general requirements cannot
        // consume the FIR stock needed for a mandatory FIR handover.
        var result = inventory.ConsumeBatch(
            requirements.OrderByDescending(requirement => requirement.FirOnly));

        var requested = requirements.Sum(requirement => requirement.Quantity);
        _log.Info(
            $"Inventory consumption applied for {source}: requested={requested}, " +
            $"consumed={result.Consumed}, missing={result.Missing}.");
    }

    private sealed class RequirementKeyComparer : IEqualityComparer<(string Key, bool FirOnly)>
    {
        public bool Equals((string Key, bool FirOnly) x, (string Key, bool FirOnly) y) =>
            x.FirOnly == y.FirOnly &&
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, bool FirOnly) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key),
                value.FirOnly);
    }
}

public sealed record InventoryConsumptionRequirement(
    string ItemNormalizedName,
    int Quantity,
    bool FirOnly);

public sealed record InventoryConsumptionResult(
    int Requested,
    int Consumed,
    int Missing);