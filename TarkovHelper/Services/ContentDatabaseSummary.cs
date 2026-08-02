using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

/// <summary>
/// Deterministic snapshot used to report content changes and reject obviously
/// incomplete API responses before they can replace the active database.
/// </summary>
internal sealed class ContentDatabaseSummary
{
    private static readonly HashSet<string> KnownObjectiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HandOver", "Custom", "Collect", "Kill", "Visit", "Stash", "Mark", "Survive", "Task", "Build"
    };

    private static readonly string[] ItemSignatureColumns =
    {
        "Id", "Name", "NameEN", "NameKO", "ShortNameEN", "ShortNameKO",
        "NormalizedName", "WikiPageLink", "IconUrl", "Category", "Categories"
    };

    private static readonly string[] QuestSignatureColumns =
    {
        "Id", "Name", "NameEN", "NameKO", "Trader", "Location", "MinLevel",
        "KappaRequired", "Faction", "NormalizedName", "RequiredPrestigeLevel", "WikiPageLink"
    };

    public int ItemCount => ItemSignatures.Count;
    public int QuestCount => QuestSignatures.Count;
    public int HideoutStationCount { get; private set; }
    public Dictionary<string, string> ItemSignatures { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> QuestSignatures { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> UnknownObjectiveTypes { get; private set; } = Array.Empty<string>();
    public HashSet<string> ItemCategoryValues { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TraderValues { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MapValues { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ContentDatabaseSummary> ReadAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var result = new ContentDatabaseSummary();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 60
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var itemColumns = await ReadTableColumnsAsync(connection, "Items", cancellationToken);
        var questColumns = await ReadTableColumnsAsync(connection, "Quests", cancellationToken);
        RequireColumn(itemColumns, "Items", "Id");
        RequireColumn(questColumns, "Quests", "Id");

        result.ItemSignatures = await ReadSignaturesAsync(
            connection,
            BuildCompatibleSignatureSql("Items", itemColumns, ItemSignatureColumns),
            cancellationToken);

        result.QuestSignatures = await ReadSignaturesAsync(
            connection,
            BuildCompatibleSignatureSql("Quests", questColumns, QuestSignatureColumns),
            cancellationToken);

        await AppendQuestChildSignaturesAsync(
            connection,
            result.QuestSignatures,
            questColumns,
            "QuestRequirements",
            "QuestId",
            cancellationToken);
        await AppendQuestChildSignaturesAsync(
            connection,
            result.QuestSignatures,
            questColumns,
            "QuestObjectives",
            "QuestId",
            cancellationToken);
        await AppendQuestChildSignaturesAsync(
            connection,
            result.QuestSignatures,
            questColumns,
            "QuestRequiredItems",
            "QuestId",
            cancellationToken);

        result.HideoutStationCount = await TableExistsAsync(connection, "HideoutStations", cancellationToken)
            ? await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM HideoutStations;", cancellationToken)
            : 0;

        result.ItemCategoryValues = await ReadItemCategoryValuesAsync(
            connection,
            itemColumns,
            cancellationToken);
        result.TraderValues = await ReadDistinctColumnAsync(
            connection,
            "Quests",
            questColumns,
            "Trader",
            cancellationToken);
        result.MapValues = await ReadDistinctColumnAsync(
            connection,
            "Quests",
            questColumns,
            "Location",
            cancellationToken,
            excludeAny: true);

        result.UnknownObjectiveTypes = await ReadUnknownObjectiveTypesAsync(connection, cancellationToken);
        return result;
    }

    public ContentChangeSummary CompareTo(ContentDatabaseSummary newer)
    {
        var itemChanges = Compare(ItemSignatures, newer.ItemSignatures);
        var questChanges = Compare(QuestSignatures, newer.QuestSignatures);
        return new ContentChangeSummary(
            itemChanges.Added,
            itemChanges.Changed,
            itemChanges.Removed,
            questChanges.Added,
            questChanges.Changed,
            questChanges.Removed,
            newer.ItemCount,
            newer.QuestCount,
            newer.HideoutStationCount,
            newer.UnknownObjectiveTypes,
            NewValues(ItemCategoryValues, newer.ItemCategoryValues),
            NewValues(TraderValues, newer.TraderValues),
            NewValues(MapValues, newer.MapValues));
    }

    public void EnsurePlausibleReplacement(ContentDatabaseSummary newer)
    {
        EnsureCount("아이템", ItemCount, newer.ItemCount, minimumAbsolute: 500);
        EnsureCount("퀘스트", QuestCount, newer.QuestCount, minimumAbsolute: 100);
        EnsureCount("은신처", HideoutStationCount, newer.HideoutStationCount, minimumAbsolute: 5);
    }

    private static void EnsureCount(string label, int previous, int next, int minimumAbsolute)
    {
        if (next < minimumAbsolute)
            throw new InvalidDataException($"{label} 데이터가 비정상적으로 적습니다: {next:N0}개");

        if (previous > 0 && next < previous * 0.70)
        {
            throw new InvalidDataException(
                $"{label} 데이터가 이전 {previous:N0}개에서 {next:N0}개로 과도하게 감소했습니다. " +
                "불완전한 API 응답으로 판단하여 기존 데이터를 유지합니다.");
        }
    }

    private static string BuildCompatibleSignatureSql(
        string tableName,
        IReadOnlySet<string> availableColumns,
        IReadOnlyList<string> signatureColumns)
    {
        var keyExpression = availableColumns.Contains("BsgId")
            ? "COALESCE(NULLIF([BsgId], ''), [Id])"
            : "[Id]";
        var selectedColumns = signatureColumns
            .Select(column => availableColumns.Contains(column)
                ? $"[{column}]"
                : $"NULL AS [{column}]");

        return $"SELECT {keyExpression} AS [ContentKey], {string.Join(", ", selectedColumns)} " +
               $"FROM [{tableName}] ORDER BY [ContentKey];";
    }

    private static async Task<Dictionary<string, string>> ReadSignaturesAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var builder = new StringBuilder();
            for (var index = 1; index < reader.FieldCount; index++)
            {
                if (index > 1)
                    builder.Append('\u001f');
                builder.Append(reader.IsDBNull(index) ? "<null>" : reader.GetValue(index)?.ToString());
            }

            result[key] = Hash(builder.ToString());
        }

        return result;
    }

    private static async Task AppendQuestChildSignaturesAsync(
        SqliteConnection connection,
        Dictionary<string, string> questSignatures,
        IReadOnlySet<string> questColumns,
        string tableName,
        string questIdColumn,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken))
            return;

        var childColumns = await ReadTableColumnsAsync(connection, tableName, cancellationToken);
        if (!childColumns.Contains(questIdColumn))
            return;

        var questKeyExpression = questColumns.Contains("BsgId")
            ? "COALESCE(NULLIF([BsgId], ''), [Id])"
            : "[Id]";
        var questKeyByLocalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var questCommand = connection.CreateCommand())
        {
            questCommand.CommandText = $"SELECT [Id], {questKeyExpression} FROM [Quests];";
            await using var questReader = await questCommand.ExecuteReaderAsync(cancellationToken);
            while (await questReader.ReadAsync(cancellationToken))
                questKeyByLocalId[questReader.GetString(0)] = questReader.GetString(1);
        }

        var rowsByQuest = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM [{tableName}] ORDER BY [{questIdColumn}], rowid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var questIdOrdinal = reader.GetOrdinal(questIdColumn);
            if (reader.IsDBNull(questIdOrdinal))
                continue;
            var localQuestId = reader.GetString(questIdOrdinal);
            if (!questKeyByLocalId.TryGetValue(localQuestId, out var questKey))
                continue;

            var builder = new StringBuilder(tableName);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var columnName = reader.GetName(index);
                if (string.Equals(columnName, "UpdatedAt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(columnName, "CreatedAt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Append('\u001e');
                builder.Append(columnName);
                builder.Append('=');
                builder.Append(reader.IsDBNull(index) ? "<null>" : reader.GetValue(index)?.ToString());
            }

            if (!rowsByQuest.TryGetValue(questKey, out var rows))
            {
                rows = new List<string>();
                rowsByQuest[questKey] = rows;
            }
            rows.Add(builder.ToString());
        }

        foreach (var (questKey, rows) in rowsByQuest)
        {
            if (!questSignatures.TryGetValue(questKey, out var baseSignature))
                continue;
            rows.Sort(StringComparer.Ordinal);
            questSignatures[questKey] = Hash(baseSignature + string.Join('\u001d', rows));
        }
    }

    private static async Task<IReadOnlyList<string>> ReadUnknownObjectiveTypesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "QuestObjectives", cancellationToken))
            return Array.Empty<string>();

        var columns = await ReadTableColumnsAsync(connection, "QuestObjectives", cancellationToken);
        if (!columns.Contains("ObjectiveType"))
            return Array.Empty<string>();

        var unknownTypes = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ObjectiveType
            FROM QuestObjectives
            WHERE ObjectiveType IS NOT NULL AND TRIM(ObjectiveType) != ''
            ORDER BY ObjectiveType;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            if (!KnownObjectiveTypes.Contains(value))
                unknownTypes.Add(value);
        }

        return unknownTypes;
    }

    private static async Task<HashSet<string>> ReadTableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken))
            throw new InvalidDataException($"콘텐츠 데이터베이스에 필수 테이블 {tableName}이 없습니다.");

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}]);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(1));
        return result;
    }

    private static void RequireColumn(IReadOnlySet<string> columns, string tableName, string columnName)
    {
        if (!columns.Contains(columnName))
            throw new InvalidDataException($"콘텐츠 데이터베이스의 {tableName} 테이블에 필수 열 {columnName}이 없습니다.");
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<HashSet<string>> ReadItemCategoryValuesAsync(
        SqliteConnection connection,
        IReadOnlySet<string> itemColumns,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasCategory = itemColumns.Contains("Category");
        var hasCategories = itemColumns.Contains("Categories");
        if (!hasCategory && !hasCategories)
            return result;

        var categoryExpression = hasCategory ? "[Category]" : "NULL AS [Category]";
        var categoriesExpression = hasCategories ? "[Categories]" : "NULL AS [Categories]";
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {categoryExpression}, {categoriesExpression} FROM [Items];";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                AddValue(result, reader.GetString(0));
            if (reader.IsDBNull(1))
                continue;

            var json = reader.GetString(1);
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                            AddValue(result, element.GetString());
                    }
                }
            }
            catch (JsonException)
            {
                AddValue(result, json);
            }
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadDistinctColumnAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlySet<string> columns,
        string columnName,
        CancellationToken cancellationToken,
        bool excludeAny = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains(columnName))
            return result;

        var anyFilter = excludeAny ? $" AND LOWER(TRIM([{columnName}])) != 'any'" : string.Empty;
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT [{columnName}] FROM [{tableName}] " +
            $"WHERE [{columnName}] IS NOT NULL AND TRIM([{columnName}]) != ''{anyFilter};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            AddValue(result, reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString());
        return result;
    }

    private static void AddValue(ISet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value.Trim());
    }

    private static IReadOnlyList<string> NewValues(
        IReadOnlySet<string> older,
        IReadOnlySet<string> newer)
    {
        return newer
            .Where(value => !older.Contains(value))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (int Added, int Changed, int Removed) Compare(
        IReadOnlyDictionary<string, string> older,
        IReadOnlyDictionary<string, string> newer)
    {
        var added = newer.Keys.Count(key => !older.ContainsKey(key));
        var removed = older.Keys.Count(key => !newer.ContainsKey(key));
        var changed = newer.Count(pair =>
            older.TryGetValue(pair.Key, out var previous) &&
            !string.Equals(previous, pair.Value, StringComparison.Ordinal));
        return (added, changed, removed);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed record ContentChangeSummary(
    int ItemsAdded,
    int ItemsChanged,
    int ItemsRemoved,
    int QuestsAdded,
    int QuestsChanged,
    int QuestsRemoved,
    int ItemCount,
    int QuestCount,
    int HideoutStationCount,
    IReadOnlyList<string> UnknownObjectiveTypes,
    IReadOnlyList<string> NewItemCategoryValues,
    IReadOnlyList<string> NewTraderValues,
    IReadOnlyList<string> NewMapValues);
