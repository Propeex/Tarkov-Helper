using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;

namespace TarkovHelper.Services;

/// <summary>
/// Service to load quest objectives with location data from tarkov_data.db
/// </summary>
public sealed class QuestObjectiveDbService
{
    private static QuestObjectiveDbService? _instance;
    public static QuestObjectiveDbService Instance => _instance ??= new QuestObjectiveDbService();

    private readonly string _databasePath;
    private List<QuestObjective> _allObjectives = new();
    private bool _isLoaded;

    public bool IsLoaded => _isLoaded;

    /// <summary>
    /// 데이터가 새로고침되었을 때 발생하는 이벤트.
    /// UI 페이지들은 이 이벤트를 구독하여 화면을 갱신해야 함.
    /// </summary>
    public event EventHandler? DataRefreshed;

    private QuestObjectiveDbService()
    {
        _databasePath = DatabaseUpdateService.Instance.DatabasePath;

        // 데이터베이스 업데이트 이벤트 구독
        DatabaseUpdateService.Instance.DatabaseUpdated += OnDatabaseUpdated;
    }

    /// <summary>
    /// 데이터베이스 업데이트 시 데이터 리로드
    /// </summary>
    private async void OnDatabaseUpdated(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[QuestObjectiveDbService] Database updated, reloading data...");
        await RefreshAsync();
    }

    /// <summary>
    /// 데이터 새로고침 (기존 데이터를 유지하면서 새 데이터로 atomic swap)
    /// </summary>
    public async Task RefreshAsync()
    {
        System.Diagnostics.Debug.WriteLine("[QuestObjectiveDbService] Refreshing objective data...");
        // 기존 데이터를 클리어하지 않음 - LoadObjectivesAsync()에서 atomic swap으로 교체
        if (await LoadObjectivesAsync())
        {
            // 성공적으로 교체된 경우에만 데이터 새로고침 이벤트 발생
            OnDataRefreshed();
        }
    }

    /// <summary>
    /// 데이터 새로고침 이벤트 발생
    /// </summary>
    private void OnDataRefreshed()
    {
        // UI 스레드에서 이벤트 발생
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DataRefreshed?.Invoke(this, EventArgs.Empty);
            });
        }
        else
        {
            DataRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Get all loaded objectives
    /// </summary>
    public IReadOnlyList<QuestObjective> AllObjectives => _allObjectives;

    /// <summary>
    /// Get objectives for a specific map
    /// </summary>
    public List<QuestObjective> GetObjectivesForMap(string mapKey, MapConfig mapConfig)
    {
        return _allObjectives
            .Where(o => mapConfig.MatchesMapName(o.EffectiveMapName))
            .ToList();
    }

    /// <summary>
    /// Load all quest objectives with location data
    /// </summary>
    public async Task<bool> LoadObjectivesAsync()
    {
        if (!File.Exists(_databasePath))
        {
            System.Diagnostics.Debug.WriteLine($"[QuestObjectiveDbService] Database not found: {_databasePath}");
            return false;
        }

        try
        {
            var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Check if QuestObjectives table exists
            if (!await TableExistsAsync(connection, "QuestObjectives"))
            {
                System.Diagnostics.Debug.WriteLine("[QuestObjectiveDbService] QuestObjectives table not found");
                return false;
            }

            // Check if optional/localization columns exist
            var hasOptionalPoints = await ColumnExistsAsync(connection, "QuestObjectives", "OptionalPoints");
            var hasObjectiveType = await ColumnExistsAsync(connection, "QuestObjectives", "ObjectiveType");
            var hasDescriptionEn = await ColumnExistsAsync(connection, "QuestObjectives", "DescriptionEN");
            var hasDescriptionKo = await ColumnExistsAsync(connection, "QuestObjectives", "DescriptionKO");
            var hasQuestNameKo = await ColumnExistsAsync(connection, "Quests", "NameKo");
            var hasQuestNameJa = await ColumnExistsAsync(connection, "Quests", "NameJa");
            var hasQuestBsgId = await ColumnExistsAsync(connection, "Quests", "BsgId");

            // 새 리스트 빌드 (기존 데이터 유지하면서)
            var newObjectives = new List<QuestObjective>();

            // Load objectives with location points and exact quest identity. Orphaned
            // preserved coordinate rows are excluded because they cannot be assigned a
            // trustworthy calculated quest status.
            var sql = $@"
                SELECT o.Id, o.QuestId, o.Description, o.MapName, o.LocationPoints,
                       q.Location as QuestLocation,
                       q.Name as QuestName,
                       {(hasQuestNameKo ? "q.NameKo as QuestNameKo," : "NULL as QuestNameKo,")}
                       {(hasQuestNameJa ? "q.NameJa as QuestNameJa," : "NULL as QuestNameJa,")}
                       q.Trader as TraderName,
                       {(hasQuestBsgId ? "q.BsgId" : "NULL")} as QuestBsgId,
                       {(hasDescriptionEn ? "o.DescriptionEN" : "NULL")} as DescriptionEN,
                       {(hasDescriptionKo ? "o.DescriptionKO" : "NULL")} as DescriptionKO
                       {(hasOptionalPoints ? ", o.OptionalPoints" : "")}
                       {(hasObjectiveType ? ", o.ObjectiveType" : "")}
                FROM QuestObjectives o
                INNER JOIN Quests q ON o.QuestId = q.Id
                WHERE ((o.LocationPoints IS NOT NULL AND o.LocationPoints != '')
                   {(hasOptionalPoints ? "OR (o.OptionalPoints IS NOT NULL AND o.OptionalPoints != '')" : "")})";

            await using var cmd = new SqliteCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var legacyDescription = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var descriptionEn = reader.IsDBNull(11) ? null : reader.GetString(11);
                var descriptionKo = reader.IsDBNull(12) ? null : reader.GetString(12);

                var objective = new QuestObjective
                {
                    Id = reader.GetString(0),
                    QuestId = reader.GetString(1),
                    QuestBsgId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Description = FirstNonEmpty(descriptionEn, legacyDescription),
                    // 영어 fallback은 한국어 번역으로 취급하지 않습니다. 레거시 값이
                    // 실제 한글일 때만 한국어 설명으로 보존하고 나머지는 자동 번역 대상입니다.
                    DescriptionKo = QuestContentTranslationService.ContainsHangul(descriptionKo)
                        ? descriptionKo
                        : QuestContentTranslationService.ContainsHangul(legacyDescription)
                            ? legacyDescription
                            : null,
                    MapName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    QuestLocation = reader.IsDBNull(5) ? null : reader.GetString(5),
                    QuestName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    QuestNameKo = reader.IsDBNull(7) ? null : reader.GetString(7),
                    QuestNameJa = reader.IsDBNull(8) ? null : reader.GetString(8),
                    TraderName = reader.IsDBNull(9) ? null : reader.GetString(9)
                };

                // Parse LocationPoints JSON
                var locationJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                objective.LocationPointsJson = locationJson;

                // Track column index for optional fields
                var nextIndex = 13;

                // Parse OptionalPoints JSON if column exists
                if (hasOptionalPoints && reader.FieldCount > nextIndex)
                {
                    var optionalJson = reader.IsDBNull(nextIndex) ? null : reader.GetString(nextIndex);
                    objective.OptionalPointsJson = optionalJson;
                    nextIndex++;
                }

                // Parse ObjectiveType if column exists
                if (hasObjectiveType && reader.FieldCount > nextIndex)
                {
                    var typeStr = reader.IsDBNull(nextIndex) ? "Custom" : reader.GetString(nextIndex);
                    objective.ObjectiveType = ParseObjectiveType(typeStr);
                }

                // Only add if has any coordinates and an exact quest identity.
                if ((objective.HasCoordinates || objective.HasOptionalPoints) &&
                    (!string.IsNullOrWhiteSpace(objective.QuestBsgId) ||
                     !string.IsNullOrWhiteSpace(objective.QuestId)))
                {
                    newObjectives.Add(objective);
                }
            }

            // 한국어가 누락된 목표 문장만 자동 번역합니다. 결과는 로컬 캐시를
            // 사용하므로 같은 문장을 지도 탭을 열 때마다 다시 요청하지 않습니다.
            await QuestContentTranslationService.Instance.TranslateMissingAsync(newObjectives);

            // Atomic swap - 모든 데이터와 번역이 준비된 후 한 번에 교체
            _allObjectives = newObjectives;
            _isLoaded = true;
            System.Diagnostics.Debug.WriteLine($"[QuestObjectiveDbService] Loaded {_allObjectives.Count} objectives with exact quest identity and location data");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QuestObjectiveDbService] Error loading objectives: {ex.Message}");
            return false;
        }
    }

    private static string FirstNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return fallback ?? string.Empty;
    }

    private async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        const string sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    private async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        var sql = $"PRAGMA table_info({tableName})";
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parse ObjectiveType string from DB to enum
    /// </summary>
    private static QuestObjectiveType ParseObjectiveType(string typeStr)
    {
        return typeStr?.ToLowerInvariant() switch
        {
            "kill" => QuestObjectiveType.Kill,
            "collect" => QuestObjectiveType.Collect,
            "handover" => QuestObjectiveType.HandOver,
            "visit" => QuestObjectiveType.Visit,
            "mark" => QuestObjectiveType.Mark,
            "stash" => QuestObjectiveType.Stash,
            "survive" => QuestObjectiveType.Survive,
            "build" => QuestObjectiveType.Build,
            "task" => QuestObjectiveType.Task,
            _ => QuestObjectiveType.Custom
        };
    }
}
