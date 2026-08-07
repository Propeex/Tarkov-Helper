using System.Collections;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds the API-managed portion of tarkov_data.db from tarkov.dev.
/// The existing database is copied to a temporary file first so map assets,
/// hand-maintained coordinates, and unsupported columns remain intact.
/// </summary>
internal sealed partial class TarkovDataDatabaseBuilder
{
    private async Task ValidateDatabaseAsync(
        string databasePath,
        DatabaseCounts counts,
        CancellationToken cancellationToken)
    {
        if (counts.Items == 0 || counts.Quests == 0 || counts.HideoutStations == 0)
            throw new InvalidDataException("API 데이터가 비어 있어 기존 데이터베이스를 교체하지 않았습니다.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 60
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var removedOptionalQuestRows = await PruneDanglingOptionalQuestsAsync(connection, cancellationToken);
        if (removedOptionalQuestRows > 0)
        {
            Log.Warning($"존재하지 않는 퀘스트를 참조하던 선택 퀘스트 연결 {removedOptionalQuestRows}개를 정리했습니다.");
            Report(
                "검증",
                $"구버전 선택 퀘스트 연결 {removedOptionalQuestRows}개 정리",
                94,
                counts.TotalRows,
                counts.TotalRows);
        }

        var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite 무결성 검사 실패: {integrity}");

        var duplicateItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM (
                SELECT BsgId FROM Items
                WHERE BsgId IS NOT NULL AND BsgId != ''
                GROUP BY BsgId HAVING COUNT(*) > 1
            );
            """, cancellationToken);
        if (duplicateItems != 0)
            throw new InvalidDataException($"동일한 BSG 아이템 ID가 중복 저장되었습니다: {duplicateItems}개");

        var missingQuestItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems q
            LEFT JOIN Items i ON q.ItemId = i.Id
            WHERE q.ItemId IS NOT NULL AND q.ItemId != '' AND i.Id IS NULL;
            """, cancellationToken);
        if (missingQuestItems != 0)
            throw new InvalidDataException($"퀘스트 필요 아이템 연결 실패: {missingQuestItems}개");

        var malformedAlternativeQuestItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems q
            WHERE q.IsAlternativeGroup = 1
              AND (
                  q.ItemId IS NOT NULL
                  OR COALESCE(q.RequirementGroupId, '') = ''
                  OR q.Count <= 0
                  OR json_valid(q.AlternativeItemIds) != 1
                  OR json_array_length(q.AlternativeItemIds) < 2
                  OR json_valid(q.AlternativeItemNames) != 1
                  OR json_array_length(q.AlternativeItemNames) != json_array_length(q.AlternativeItemIds)
              );
            """, cancellationToken);
        if (malformedAlternativeQuestItems != 0)
            throw new InvalidDataException($"대체 가능 퀘스트 아이템 묶음 형식 오류: {malformedAlternativeQuestItems}개");

        var missingAlternativeQuestItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems q, json_each(q.AlternativeItemIds) alternative
            LEFT JOIN Items i ON i.Id = alternative.value
            WHERE q.IsAlternativeGroup = 1
              AND i.Id IS NULL;
            """, cancellationToken);
        if (missingAlternativeQuestItems != 0)
            throw new InvalidDataException($"대체 가능 퀘스트 아이템 연결 실패: {missingAlternativeQuestItems}개");

        var invalidConcreteQuestItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems q
            LEFT JOIN Items i ON i.Id = q.ItemId
            WHERE q.IsAlternativeGroup = 0
              AND (q.ItemId IS NULL OR q.ItemId = '' OR i.Id IS NULL);
            """, cancellationToken);
        if (invalidConcreteQuestItems != 0)
            throw new InvalidDataException($"단일 퀘스트 필요 아이템 형식 오류: {invalidConcreteQuestItems}개");

        var missingHideoutItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM HideoutItemRequirements h
            LEFT JOIN Items i ON h.ItemId = i.BsgId
            WHERE h.ItemId IS NOT NULL AND h.ItemId != '' AND i.Id IS NULL;
            """, cancellationToken);
        if (missingHideoutItems != 0)
            throw new InvalidDataException($"은신처 필요 아이템 연결 실패: {missingHideoutItems}개");


        var missingAmmoItems = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM Ammo a
            LEFT JOIN Items i ON a.ItemId = i.BsgId OR a.ItemId = i.Id
            WHERE i.Id IS NULL;
            """, cancellationToken);
        if (missingAmmoItems != 0)
            throw new InvalidDataException($"탄약 아이템 연결 실패: {missingAmmoItems}개");

        var invalidAmmoSources = await ReadInvalidAmmoSourcesAsync(connection, cancellationToken);
        if (invalidAmmoSources.Count != 0)
        {
            throw new InvalidDataException(
                $"탄약 입수 경로 형식 오류: {invalidAmmoSources.Count}개 · " +
                string.Join("; ", invalidAmmoSources.Take(12)));
        }

        var invalidQuestItemSemantics = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems
            WHERE COALESCE(TrackingKind, '') NOT IN ('consumable', 'track-only')
               OR COALESCE(ConsumesItem, -1) NOT IN (0, 1)
               OR (ConsumesItem = 1 AND TrackingKind != 'consumable')
               OR (ConsumesItem = 0 AND TrackingKind != 'track-only');
            """, cancellationToken);
        if (invalidQuestItemSemantics != 0)
            throw new InvalidDataException($"퀘스트 아이템 추적 의미 오류: {invalidQuestItemSemantics}개");

        var invalidItemSourceJson = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM Items WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;",
            cancellationToken);
        var invalidQuestSourceJson = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM Quests WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;",
            cancellationToken);
        var invalidObjectiveSourceJson = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM QuestObjectives WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;",
            cancellationToken);
        var invalidSourceJson = invalidItemSourceJson + invalidQuestSourceJson + invalidObjectiveSourceJson;
        if (invalidSourceJson != 0)
        {
            throw new InvalidDataException(
                $"콘텐츠 소스 JSON 보존 오류: total={invalidSourceJson}, " +
                $"items={invalidItemSourceJson}, quests={invalidQuestSourceJson}, " +
                $"objectives={invalidObjectiveSourceJson}");
        }

        var invalidAmmoValues = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM Ammo
            WHERE ProjectileCount < 1 OR Damage < 0 OR PenetrationPower < 0 OR ArmorDamage < 0
               OR FragmentationChance < 0 OR RicochetChance < 0 OR PenetrationChance < 0
               OR MisfireChance < 0 OR FailureToFeedChance < 0 OR InitialSpeed < 0;
            """, cancellationToken);
        if (invalidAmmoValues != 0)
            throw new InvalidDataException($"탄약 수치 범위 오류: {invalidAmmoValues}개");

        var missingAmmoSourceRows = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM Ammo a
            WHERE NOT EXISTS (SELECT 1 FROM AmmoAcquisitionSources s WHERE s.ItemId = a.ItemId);
            """, cancellationToken);
        if (missingAmmoSourceRows != 0)
            throw new InvalidDataException($"탄약 입수 경로 정규화 누락: {missingAmmoSourceRows}개");

        var invalidHideoutTraderRequirements = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM HideoutTraderRequirements
            WHERE COALESCE(RequirementType, '') = ''
               OR COALESCE(CompareMethod, '') = ''
               OR RequiredValue IS NULL;
            """, cancellationToken);
        if (invalidHideoutTraderRequirements != 0)
            throw new InvalidDataException($"은신처 상인 조건 형식 오류: {invalidHideoutTraderRequirements}개");

        var invalidQuestTraderRequirements = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM QuestTraderRequirements r
            LEFT JOIN Quests q ON q.Id = r.QuestId
            WHERE q.Id IS NULL OR COALESCE(r.RequirementType, '') = ''
               OR COALESCE(r.CompareMethod, '') = '' OR r.RequiredValue IS NULL;
            """, cancellationToken);
        if (invalidQuestTraderRequirements != 0)
            throw new InvalidDataException($"퀘스트 상인 조건 형식 오류: {invalidQuestTraderRequirements}개");

        var metadataRows = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM ContentBuildMetadata WHERE Id = 'current' AND SchemaVersion >= 2;",
            cancellationToken);
        if (metadataRows != 1)
            throw new InvalidDataException("콘텐츠 빌드 메타데이터가 생성되지 않았습니다.");

        var missingQuestReferences = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements r
            LEFT JOIN Quests q1 ON r.QuestId = q1.Id
            LEFT JOIN Quests q2 ON r.RequiredQuestId = q2.Id
            WHERE q1.Id IS NULL OR q2.Id IS NULL;
            """, cancellationToken);
        if (missingQuestReferences != 0)
            throw new InvalidDataException($"선행 퀘스트 연결 실패: {missingQuestReferences}개");

        var invalidQuestRequirements = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements r
            WHERE r.QuestId = r.RequiredQuestId
               OR json_valid(COALESCE(r.StatusesJson, '')) != 1
               OR json_array_length(r.StatusesJson) < 1
               OR EXISTS (
                    SELECT 1
                    FROM json_each(r.StatusesJson) status
                    WHERE LOWER(TRIM(CAST(status.value AS TEXT)))
                          NOT IN ('active', 'start', 'accept', 'complete', 'failed', 'fail')
               );
            """, cancellationToken);
        if (invalidQuestRequirements != 0)
            throw new InvalidDataException($"선행 퀘스트 상태 조건 형식 오류: {invalidQuestRequirements}개");

        var duplicateQuestRequirements = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM (
                SELECT QuestId, RequiredQuestId, COALESCE(GroupId, 0), COALESCE(StatusesJson, '')
                FROM QuestRequirements
                GROUP BY QuestId, RequiredQuestId, COALESCE(GroupId, 0), COALESCE(StatusesJson, '')
                HAVING COUNT(*) > 1
            );
            """, cancellationToken);
        if (duplicateQuestRequirements != 0)
            throw new InvalidDataException($"중복 선행 퀘스트 조건이 생성되었습니다: {duplicateQuestRequirements}개");

        var foreignKeyIssues = await ReadForeignKeyIssuesAsync(connection, cancellationToken);
        if (foreignKeyIssues.Count != 0)
        {
            throw new InvalidDataException(
                $"외래 키 검사 실패: {foreignKeyIssues.Count}개 · " +
                string.Join("; ", foreignKeyIssues.Take(12)));
        }

        Report("검증", "아이템·탄약·퀘스트·은신처 연결 검증 완료", 98, counts.TotalRows, counts.TotalRows);
    }

    private static async Task<List<string>> ReadInvalidAmmoSourcesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId, AcquisitionSource FROM Ammo ORDER BY ItemId;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.IsDBNull(0) ? "?" : reader.GetString(0);
            var source = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!IsValidAcquisitionSource(source))
                invalid.Add($"{itemId}={source ?? "<null>"}");
        }

        return invalid;
    }

    private static async Task<int> PruneDanglingOptionalQuestsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "OptionalQuests", cancellationToken))
            return 0;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM OptionalQuests
            WHERE NOT EXISTS (
                      SELECT 1 FROM Quests q WHERE q.Id = OptionalQuests.QuestId
                  )
               OR NOT EXISTS (
                      SELECT 1 FROM Quests q WHERE q.Id = OptionalQuests.AlternativeQuestId
                  );
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadForeignKeyIssuesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.IsDBNull(0) ? "?" : reader.GetString(0);
            var rowId = reader.IsDBNull(1) ? "?" : reader.GetValue(1)?.ToString() ?? "?";
            var parent = reader.IsDBNull(2) ? "?" : reader.GetString(2);
            var foreignKeyId = reader.IsDBNull(3) ? "?" : reader.GetValue(3)?.ToString() ?? "?";
            issues.Add($"{table}(rowid={rowId}) → {parent}(fk={foreignKeyId})");
        }

        return issues;
    }

    private static async Task<TableSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info([{tableName}]);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new ColumnInfo(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5)));
            }
        }

        var rows = new List<RowData>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT * FROM [{tableName}];";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new RowData();
                for (var index = 0; index < reader.FieldCount; index++)
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(row);
            }
        }

        return new TableSnapshot(columns, rows);
    }

    private static Dictionary<string, RowData> IndexRows(IEnumerable<RowData> rows, params string[] keyColumns)
    {
        var result = new Dictionary<string, RowData>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var keyParts = keyColumns
                .Select(column => ReadString(row, column))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (keyParts.Length == 0)
                continue;

            // Store an entry for each single key as well as the composite key.
            foreach (var keyPart in keyParts)
                result.TryAdd(keyPart!, row);
            result.TryAdd(BuildCompositeKey(keyParts), row);
        }
        return result;
    }

    private static async Task InsertRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ColumnInfo> columns,
        string tableName,
        RowData row,
        CancellationToken cancellationToken)
    {
        var insertColumns = new List<ColumnInfo>();
        var values = new List<object?>();

        foreach (var column in columns)
        {
            if (TryGetValue(row, column.Name, out var value))
            {
                insertColumns.Add(column);
                values.Add(value);
                continue;
            }

            var isAutoIntegerPrimaryKey = column.PrimaryKeyOrder > 0 &&
                column.Type.Contains("INT", StringComparison.OrdinalIgnoreCase);
            if (isAutoIntegerPrimaryKey || column.DefaultValue is not null)
                continue;

            if (column.NotNull)
            {
                insertColumns.Add(column);
                values.Add(DefaultForSqlType(column.Type));
            }
        }

        if (insertColumns.Count == 0)
            throw new InvalidDataException($"{tableName}에 삽입할 수 있는 열이 없습니다.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO [{tableName}] ({string.Join(", ", insertColumns.Select(column => $"[{column.Name}]"))}) " +
                              $"VALUES ({string.Join(", ", insertColumns.Select((_, index) => $"@p{index}"))});";

        for (var index = 0; index < insertColumns.Count; index++)
            command.Parameters.AddWithValue($"@p{index}", ToDbValue(values[index]));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToDbValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            bool boolean => boolean ? 1 : 0,
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            Enum enumValue => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static object DefaultForSqlType(string type)
    {
        if (type.Contains("INT", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("NUM", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DEC", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DOUBLE", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("FLOAT", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (type.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<byte>();
        return string.Empty;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountRowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        long count = 0;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            count++;
        return count;
    }

    private void Report(
        string stage,
        string message,
        double percent,
        int current,
        int? total)
    {
        percent = Math.Clamp(percent, _lastPercent, 100);
        _lastPercent = percent;
        var elapsed = DateTime.UtcNow - _startedAt;
        TimeSpan? remaining = null;

        if (percent >= 1 && percent < 100 && elapsed.TotalSeconds >= 1)
        {
            var estimatedTotalSeconds = elapsed.TotalSeconds / (percent / 100d);
            remaining = TimeSpan.FromSeconds(Math.Max(0, estimatedTotalSeconds - elapsed.TotalSeconds));
        }

        _report(new DatabaseBuildProgress(stage, message, percent, current, total, elapsed, remaining));
    }

    private static void ReplaceDatabaseAtomically(string tempPath, string targetPath, string backupPath)
    {
        SqliteConnection.ClearAllPools();
        CleanupFile(backupPath);

        try
        {
            File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            ReplaceWithMove(tempPath, targetPath, backupPath);
        }
        catch (IOException)
        {
            ReplaceWithMove(tempPath, targetPath, backupPath);
        }
    }

    private static void ReplaceWithMove(string tempPath, string targetPath, string backupPath)
    {
        if (File.Exists(targetPath))
            File.Move(targetPath, backupPath, overwrite: true);

        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            if (!File.Exists(targetPath) && File.Exists(backupPath))
                File.Move(backupPath, targetPath, overwrite: true);
            throw;
        }
    }

    private static void CleanupFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Warning($"임시 파일 정리 실패: {path} ({exception.Message})");
        }
    }

    private static RowData CloneRow(RowData? source)
    {
        return source is null ? new RowData() : new RowData(source, StringComparer.OrdinalIgnoreCase);
    }

    private static void Set(RowData row, string key, object? value)
    {
        row[key] = value;
    }

    private static void PreserveOrSet(RowData row, string key, RowData? old, object? fallback)
    {
        if (!TryGetValue(old, key, out var value))
            row[key] = fallback;
        else
            row[key] = value;
    }

    private static bool HasValue(RowData row, string key)
    {
        return TryGetValue(row, key, out var value) && value is not null && value is not DBNull;
    }

    private static string? ReadString(RowData? row, string key)
    {
        return TryGetValue(row, key, out var value) ? value?.ToString() : null;
    }

    private static bool TryGetValue(RowData? row, string key, out object? value)
    {
        if (row is not null && row.TryGetValue(key, out value))
            return true;
        value = null;
        return false;
    }

    private static string BuildCompositeKey(params string[] parts)
    {
        return string.Join("\u001F", parts.Select(part => part.Trim()));
    }

    private static string? Fallback(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousWasDash = false;
        foreach (var character in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasDash = false;
            }
            else if (!previousWasDash && builder.Length > 0)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string KoreanTableName(string tableName) => tableName switch
    {
        "Items" => "아이템",
        "Ammo" => "탄약",
        "Quests" => "퀘스트",
        "QuestRequirements" => "선행 퀘스트",
        "QuestObjectives" => "퀘스트 목표",
        "QuestRequiredItems" => "퀘스트 필요 아이템",
        "HideoutStations" => "은신처 시설",
        "HideoutLevels" => "은신처 레벨",
        "HideoutItemRequirements" => "은신처 필요 아이템",
        "HideoutStationRequirements" => "은신처 시설 조건",
        "HideoutTraderRequirements" => "은신처 상인 조건",
        "HideoutSkillRequirements" => "은신처 스킬 조건",
        _ => tableName
    };
}
