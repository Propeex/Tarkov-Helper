using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

internal sealed partial class TarkovDataDatabaseBuilder
{
    private static async Task EnsureExtendedSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnsAsync(connection, "Items", new Dictionary<string, string>
        {
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "Quests", new Dictionary<string, string>
        {
            ["LightkeeperRequired"] = "INTEGER NOT NULL DEFAULT 0",
            ["Restartable"] = "INTEGER NOT NULL DEFAULT 0",
            ["GameModesJson"] = "TEXT",
            ["AvailableDelaySecondsMin"] = "INTEGER",
            ["AvailableDelaySecondsMax"] = "INTEGER",
            ["TaskImageLink"] = "TEXT",
            ["NeededKeysJson"] = "TEXT",
            ["OtherRequirementsJson"] = "TEXT",
            ["StartRewardsJson"] = "TEXT",
            ["FinishRewardsJson"] = "TEXT",
            ["FailureOutcomeJson"] = "TEXT",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "QuestRequirements", new Dictionary<string, string>
        {
            ["StatusesJson"] = "TEXT",
            ["Notes"] = "TEXT",
            ["SourceJson"] = "TEXT",
            ["SortOrder"] = "INTEGER NOT NULL DEFAULT 0"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "QuestObjectives", new Dictionary<string, string>
        {
            ["MinDurability"] = "REAL",
            ["MaxDurability"] = "REAL",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "QuestRequiredItems", new Dictionary<string, string>
        {
            ["ObjectiveId"] = "TEXT",
            ["RequirementGroupId"] = "TEXT",
            ["IsAlternativeGroup"] = "INTEGER NOT NULL DEFAULT 0",
            ["AlternativeItemIds"] = "TEXT",
            ["AlternativeItemNames"] = "TEXT",
            ["ObjectiveType"] = "TEXT",
            ["ConsumesItem"] = "INTEGER NOT NULL DEFAULT 1",
            ["TrackingKind"] = "TEXT NOT NULL DEFAULT 'consumable'",
            ["MinDurability"] = "REAL",
            ["MaxDurability"] = "REAL",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "HideoutLevels", new Dictionary<string, string>
        {
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "HideoutItemRequirements", new Dictionary<string, string>
        {
            ["AttributesJson"] = "TEXT",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await EnsureColumnsAsync(connection, "HideoutTraderRequirements", new Dictionary<string, string>
        {
            ["RequirementType"] = "TEXT",
            ["CompareMethod"] = "TEXT",
            ["RequiredValue"] = "REAL",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS QuestTraderRequirements (
                Id TEXT PRIMARY KEY,
                QuestId TEXT NOT NULL,
                TraderId TEXT,
                TraderName TEXT,
                TraderNameKO TEXT,
                RequirementType TEXT,
                CompareMethod TEXT,
                RequiredValue REAL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                SourceJson TEXT,
                UpdatedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_QuestTraderRequirements_QuestId
                ON QuestTraderRequirements(QuestId);

            CREATE TABLE IF NOT EXISTS AmmoAcquisitionSources (
                Id TEXT PRIMARY KEY,
                ItemId TEXT NOT NULL,
                SourceType TEXT NOT NULL,
                SourceName TEXT NOT NULL,
                RequiredLevel INTEGER NOT NULL DEFAULT 1,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_AmmoAcquisitionSources_ItemId
                ON AmmoAcquisitionSources(ItemId);

            CREATE TABLE IF NOT EXISTS ContentBuildMetadata (
                Id TEXT PRIMARY KEY,
                Source TEXT NOT NULL,
                Transport TEXT NOT NULL,
                BuiltAt TEXT NOT NULL,
                ItemCount INTEGER NOT NULL,
                AmmoCount INTEGER NOT NULL,
                QuestCount INTEGER NOT NULL,
                QuestObjectiveCount INTEGER NOT NULL,
                QuestRequiredItemCount INTEGER NOT NULL,
                HideoutStationCount INTEGER NOT NULL,
                ObjectiveTypeHistogramJson TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                UpdatedAt TEXT
            );
            """, cancellationToken);
    }

    private static async Task EnsureColumnsAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyDictionary<string, string> additions,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info([{tableName}]);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name))
            {
                await ExecuteNonQueryAsync(
                    connection,
                    $"ALTER TABLE [{tableName}] ADD COLUMN [{name}] {definition};",
                    cancellationToken);
            }
        }
    }
}
