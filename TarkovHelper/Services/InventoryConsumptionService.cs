using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Applies irreversible item spending after the corresponding quest or hideout
/// progress has been durably written to user_data.db. Progress resets do not
/// refund materials that were already consumed in the game.
/// </summary>
internal sealed class InventoryConsumptionService
{
    private static readonly ILogger _log = Log.For<InventoryConsumptionService>();
    private static readonly object InstanceLock = new();
    private static InventoryConsumptionService? _instance;

    public static InventoryConsumptionService Instance
    {
        get
        {
            lock (InstanceLock)
                return _instance ??= new InventoryConsumptionService();
        }
    }

    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmationPollDelay = TimeSpan.FromMilliseconds(50);

    private readonly object _pendingLock = new();
    private readonly HashSet<Task> _pendingTasks = [];
    private readonly HashSet<string> _scheduledSources = new(StringComparer.OrdinalIgnoreCase);

    private InventoryConsumptionService()
    {
    }

    /// <summary>
    /// Waits for all completion-verification and consumption operations without
    /// creating the singleton when it has never been used.
    /// </summary>
    public static async Task FlushExistingAsync()
    {
        InventoryConsumptionService? instance;
        lock (InstanceLock)
            instance = _instance;

        if (instance != null)
            await instance.FlushAsync().ConfigureAwait(false);
    }

    public void ConsumeQuestRequirements(TarkovTask task)
    {
        if (task.RequiredItems is not { Count: > 0 })
            return;

        var requirements = task.RequiredItems
            .Where(item => item.ConsumesItem && !string.IsNullOrWhiteSpace(item.ItemNormalizedName) && item.Amount > 0)
            .Select(item => new InventoryConsumptionRequirement(
                item.IsAlternativeGroup
                    ? QuestRequirementInventoryKey.BuildGroupKey(task, item)
                    : item.ItemNormalizedName,
                item.Amount,
                item.FoundInRaid,
                item.IsAlternativeGroup
                    ? QuestRequirementInventoryKey.BuildAlternativeItemKeys(task, item)
                    : null))
            .ToList();

        var questId = task.Ids?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var normalizedName = task.NormalizedName;
        if (string.IsNullOrWhiteSpace(questId) && string.IsNullOrWhiteSpace(normalizedName))
        {
            _log.Warning($"Inventory consumption skipped because quest identity is missing: {task.Name}");
            return;
        }

        var profile = ProfileService.Instance.CurrentProfile;
        var source = $"quest:{questId ?? normalizedName ?? task.Name}";
        QueueVerifiedConsumption(
            source,
            requirements,
            () => IsQuestCompletionPersistedAsync(questId, normalizedName, profile));
    }

    public void ConsumeHideoutLevels(HideoutModule module, int previousLevel, int newLevel)
    {
        if (newLevel <= previousLevel || string.IsNullOrWhiteSpace(module.NormalizedName))
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

        var stationId = module.NormalizedName;
        var profile = ProfileService.Instance.CurrentProfile;
        var source = $"hideout:{stationId}:{previousLevel}->{newLevel}";
        QueueVerifiedConsumption(
            source,
            requirements,
            () => IsHideoutLevelPersistedAsync(stationId, newLevel, profile));
    }

    private void QueueVerifiedConsumption(
        string source,
        IReadOnlyCollection<InventoryConsumptionRequirement> requirements,
        Func<Task<bool>> confirmation)
    {
        if (requirements.Count == 0)
            return;

        Task operation;
        lock (_pendingLock)
        {
            // A duplicated UI/log callback for the same completion must not schedule a
            // second irreversible deduction while the first one is still being verified.
            if (!_scheduledSources.Add(source))
                return;

            operation = VerifyAndConsumeAsync(source, requirements, confirmation);
            _pendingTasks.Add(operation);
        }

        _ = operation.ContinueWith(
            completed =>
            {
                lock (_pendingLock)
                {
                    _pendingTasks.Remove(completed);
                    _scheduledSources.Remove(source);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task VerifyAndConsumeAsync(
        string source,
        IReadOnlyCollection<InventoryConsumptionRequirement> requirements,
        Func<Task<bool>> confirmation)
    {
        try
        {
            if (!await WaitForPersistenceAsync(confirmation).ConfigureAwait(false))
            {
                _log.Error(
                    $"Inventory consumption cancelled for {source}: progress was not durably persisted " +
                    $"within {ConfirmationTimeout.TotalSeconds:F0} seconds.");
                return;
            }

            ConsumeNow(source, requirements);
        }
        catch (Exception ex)
        {
            // A failed persistence check must never fall through to irreversible item
            // consumption. The progress operation can be retried without double spending.
            _log.Error($"Inventory consumption verification failed for {source}", ex);
        }
    }

    private static async Task<bool> WaitForPersistenceAsync(Func<Task<bool>> confirmation)
    {
        var deadline = DateTime.UtcNow + ConfirmationTimeout;
        do
        {
            try
            {
                if (await confirmation().ConfigureAwait(false))
                    return true;
            }
            catch (SqliteException)
            {
                // A concurrent user-data transaction can briefly lock the database.
                // Retry until the bounded confirmation deadline instead of deducting.
            }

            await Task.Delay(ConfirmationPollDelay).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static async Task<bool> IsQuestCompletionPersistedAsync(
        string? questId,
        string? normalizedName,
        ProfileType profile)
    {
        await using var connection = CreateReadOnlyConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM QuestProgress
            WHERE ProfileType = $profile
              AND Status = $status
              AND (
                    ($id <> '' AND Id = $id)
                 OR ($normalizedName <> '' AND NormalizedName = $normalizedName)
              )
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$profile", (int)profile);
        command.Parameters.AddWithValue("$status", QuestStatus.Done.ToString());
        command.Parameters.AddWithValue("$id", questId ?? string.Empty);
        command.Parameters.AddWithValue("$normalizedName", normalizedName ?? string.Empty);

        return await command.ExecuteScalarAsync().ConfigureAwait(false) != null;
    }

    private static async Task<bool> IsHideoutLevelPersistedAsync(
        string stationId,
        int expectedLevel,
        ProfileType profile)
    {
        await using var connection = CreateReadOnlyConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM HideoutProgress
            WHERE ProfileType = $profile
              AND StationId = $stationId
              AND Level >= $expectedLevel
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$profile", (int)profile);
        command.Parameters.AddWithValue("$stationId", stationId);
        command.Parameters.AddWithValue("$expectedLevel", expectedLevel);

        return await command.ExecuteScalarAsync().ConfigureAwait(false) != null;
    }

    private static SqliteConnection CreateReadOnlyConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = UserDataDbService.Instance.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ConnectionString;

        return new SqliteConnection(connectionString);
    }

    private static void ConsumeNow(
        string source,
        IReadOnlyCollection<InventoryConsumptionRequirement> requirements)
    {
        // ItemInventoryService is recreated after a database/profile refresh. Resolve
        // it only after durable progress confirmation so the active UI instance changes.
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

    private async Task FlushAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_pendingLock)
                pending = _pendingTasks.ToArray();

            if (pending.Length == 0)
                return;

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
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
    bool FirOnly,
    IReadOnlyList<string>? AlternativeItemKeys = null);

public sealed record InventoryConsumptionResult(
    int Requested,
    int Consumed,
    int Missing);
