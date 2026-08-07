using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// SQLite DB에서 퀘스트 데이터를 로드하는 서비스.
/// tarkov_data.db의 Quests, QuestRequirements, QuestObjectives, QuestRequiredItems 테이블 사용.
/// </summary>
public sealed class QuestDbService
{
    private static readonly ILogger _log = Log.For<QuestDbService>();
    private static QuestDbService? _instance;
    public static QuestDbService Instance => _instance ??= new QuestDbService();

    private readonly string _databasePath;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private List<TarkovTask> _allQuests = new();
    private Dictionary<string, TarkovTask> _questsById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TarkovTask> _questsByNormalizedName = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public bool IsLoaded => _isLoaded;
    public int QuestCount => _allQuests.Count;

    /// <summary>
    /// 데이터가 새로고침되었을 때 발생하는 이벤트.
    /// UI 페이지들은 이 이벤트를 구독하여 화면을 갱신해야 함.
    /// </summary>
    public event EventHandler? DataRefreshed;

    private QuestDbService()
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
        _log.Info("Database updated, reloading data...");
        await RefreshAsync();
    }

    /// <summary>
    /// DB가 존재하는지 확인
    /// </summary>
    public bool DatabaseExists => File.Exists(_databasePath);

    /// <summary>
    /// 모든 퀘스트 반환
    /// </summary>
    public IReadOnlyList<TarkovTask> AllQuests => _allQuests;

    /// <summary>
    /// ID로 퀘스트 조회
    /// </summary>
    public TarkovTask? GetQuestById(string id)
    {
        return _questsById.TryGetValue(id, out var quest) ? quest : null;
    }

    /// <summary>
    /// NormalizedName으로 퀘스트 조회
    /// </summary>
    public TarkovTask? GetQuestByNormalizedName(string normalizedName)
    {
        return _questsByNormalizedName.TryGetValue(normalizedName, out var quest) ? quest : null;
    }

    /// <summary>
    /// DB에서 모든 퀘스트를 로드합니다.
    /// </summary>
    public async Task<bool> LoadQuestsAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            if (!DatabaseExists)
            {
                _log.Warning($"Database not found: {_databasePath}");
                return false;
            }

            var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Quests 테이블 존재 여부 확인
            if (!await TableExistsAsync(connection, "Quests"))
            {
                _log.Warning("Quests table not found");
                return false;
            }

            // 1. 기본 퀘스트 정보 로드
            var quests = await LoadBaseQuestsAsync(connection);
            EnsureUniqueNormalizedNames(quests);

            var questLookup = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in quests)
            {
                foreach (var id in quest.Ids ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        questLookup.TryAdd(id, quest);
                }
            }

            // 2. 선행 퀘스트 요구사항 로드
            await LoadQuestRequirementsAsync(connection, questLookup);

            // 3. 상인/평판 해금 조건 로드
            await LoadQuestTraderRequirementsAsync(connection, questLookup);

            // 4. 퀘스트 목표 로드
            await LoadQuestObjectivesAsync(connection, questLookup);

            // 5. 필요 아이템 로드
            await LoadQuestRequiredItemsAsync(connection, questLookup);

            // 6. 대체 퀘스트 로드
            await LoadOptionalQuestsAsync(connection, questLookup);

            // 7. 선택 퀘스트 관계를 이용해 OR 조건 보정
            NormalizeEquivalentAlternativeRequirementGroups(quests, questLookup);

            // 8. LeadsTo 역참조 구축
            BuildLeadsToReferences(quests);

            // 새 딕셔너리 빌드 (기존 데이터 유지하면서)
            var newQuestsById = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
            var newQuestsByNormalizedName = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var quest in quests)
            {
                foreach (var id in quest.Ids ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        newQuestsById.TryAdd(id, quest);
                }

                if (!string.IsNullOrEmpty(quest.NormalizedName))
                    newQuestsByNormalizedName.TryAdd(quest.NormalizedName, quest);
            }

            // Atomic swap - 모든 데이터가 준비된 후 한 번에 교체
            _allQuests = quests;
            _questsById = newQuestsById;
            _questsByNormalizedName = newQuestsByNormalizedName;
            _isLoaded = true;
            _log.Info($"Loaded {quests.Count} quests from DB");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Error loading quests", ex);
            return false;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        var sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    /// <summary>
    /// 컬럼이 존재하는지 확인
    /// </summary>
    private async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        var sql = $"PRAGMA table_info({tableName})";
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1); // column name is at index 1
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 기본 퀘스트 정보 로드
    /// </summary>
    private async Task<List<TarkovTask>> LoadBaseQuestsAsync(SqliteConnection connection)
    {
        var quests = new List<TarkovTask>();

        // 동적으로 존재하는 컬럼 확인
        var hasNormalizedName = await ColumnExistsAsync(connection, "Quests", "NormalizedName");
        var hasBsgId = await ColumnExistsAsync(connection, "Quests", "BsgId");
        var hasRequiredEdition = await ColumnExistsAsync(connection, "Quests", "RequiredEdition");
        var hasExcludedEdition = await ColumnExistsAsync(connection, "Quests", "ExcludedEdition");
        var hasRequiredPrestigeLevel = await ColumnExistsAsync(connection, "Quests", "RequiredPrestigeLevel");
        var hasRequiredDecodeCount = await ColumnExistsAsync(connection, "Quests", "RequiredDecodeCount");
        var hasWikiPageLink = await ColumnExistsAsync(connection, "Quests", "WikiPageLink");
        var hasLightkeeperRequired = await ColumnExistsAsync(connection, "Quests", "LightkeeperRequired");
        var hasRestartable = await ColumnExistsAsync(connection, "Quests", "Restartable");
        var hasGameModesJson = await ColumnExistsAsync(connection, "Quests", "GameModesJson");
        var hasDelayMin = await ColumnExistsAsync(connection, "Quests", "AvailableDelaySecondsMin");
        var hasDelayMax = await ColumnExistsAsync(connection, "Quests", "AvailableDelaySecondsMax");
        _log.Debug($"BsgId column exists: {hasBsgId}");

        // NormalizedName이 없으면 Name에서 생성
        var normalizedNameExpr = hasNormalizedName
            ? "NormalizedName"
            : "LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))";

        var sql = $@"
            SELECT
                Id,
                {(hasBsgId ? "BsgId" : "NULL")} as BsgId,
                Name, NameKO, NameJA,
                Trader, Location, MinLevel, MinScavKarma,
                KappaRequired, Faction,
                {normalizedNameExpr} as NormalizedName,
                {(hasRequiredEdition ? "RequiredEdition" : "NULL")} as RequiredEdition,
                {(hasExcludedEdition ? "ExcludedEdition" : "NULL")} as ExcludedEdition,
                {(hasRequiredPrestigeLevel ? "RequiredPrestigeLevel" : "NULL")} as RequiredPrestigeLevel,
                {(hasRequiredDecodeCount ? "RequiredDecodeCount" : "NULL")} as RequiredDecodeCount,
                {(hasWikiPageLink ? "WikiPageLink" : "NULL")} as WikiPageLink,
                {(hasLightkeeperRequired ? "LightkeeperRequired" : "0")} as LightkeeperRequired,
                {(hasRestartable ? "Restartable" : "0")} as Restartable,
                {(hasGameModesJson ? "GameModesJson" : "NULL")} as GameModesJson,
                {(hasDelayMin ? "AvailableDelaySecondsMin" : "NULL")} as AvailableDelaySecondsMin,
                {(hasDelayMax ? "AvailableDelaySecondsMax" : "NULL")} as AvailableDelaySecondsMax
            FROM Quests
            ORDER BY Name, Id";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);

            var quest = new TarkovTask
            {
                Ids = new List<string> { id },
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                NameKo = reader.IsDBNull(3) ? null : reader.GetString(3),
                NameJa = reader.IsDBNull(4) ? null : reader.GetString(4),
                Trader = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Maps = reader.IsDBNull(6) ? null : ParseMaps(reader.GetString(6)),
                RequiredLevel = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                RequiredScavKarma = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ReqKappa = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                Faction = reader.IsDBNull(10) ? null : reader.GetString(10),
                NormalizedName = reader.IsDBNull(11) ? GenerateNormalizedName(reader.GetString(2)) : reader.GetString(11),
                RequiredEdition = reader.IsDBNull(12) ? null : reader.GetString(12),
                ExcludedEdition = reader.IsDBNull(13) ? null : reader.GetString(13),
                RequiredPrestigeLevel = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                RequiredDecodeCount = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                WikiPageLink = reader.IsDBNull(16) ? null : reader.GetString(16),
                LightkeeperRequired = !reader.IsDBNull(17) && reader.GetInt32(17) == 1,
                Restartable = !reader.IsDBNull(18) && reader.GetInt32(18) == 1,
                GameModes = ParseStringArray(reader.IsDBNull(19) ? null : reader.GetString(19)),
                AvailableDelaySecondsMin = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                AvailableDelaySecondsMax = reader.IsDBNull(21) ? null : reader.GetInt32(21)
            };

            // BsgId가 있으면 Ids에 추가
            if (!string.IsNullOrEmpty(bsgId) && bsgId != id)
                quest.Ids.Add(bsgId);

            quests.Add(quest);
        }

        // BsgId 통계 출력
        var questsWithBsgId = quests.Count(q => q.Ids != null && q.Ids.Count > 1);
        _log.Debug($"Quests with BsgId: {questsWithBsgId}/{quests.Count}");
        if (quests.Count > 0 && quests[0].Ids != null)
            _log.Debug($"Sample quest IDs: {string.Join(", ", quests[0].Ids ?? [])} - {quests[0].Name}");

        return quests;
    }

    /// <summary>
    /// Location 문자열을 맵 리스트로 파싱
    /// </summary>
    private List<string>? ParseMaps(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        // "any" 또는 복수 맵 처리
        if (location.Equals("any", StringComparison.OrdinalIgnoreCase))
            return null;

        // 쉼표로 구분된 경우 처리
        if (location.Contains(','))
        {
            return location.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim().ToLowerInvariant())
                .ToList();
        }

        return new List<string> { location.ToLowerInvariant() };
    }

    /// <summary>
    /// Name에서 NormalizedName 생성
    /// </summary>
    private string GenerateNormalizedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        return name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace("’", "")
            .Replace(".", "")
            .Replace(",", "")
            .Replace("?", "")
            .Replace("!", "")
            .Replace(":", "")
            .Replace("\"", "");
    }

    /// <summary>
    /// tarkov.dev에는 이름이 같은 별도 퀘스트가 존재한다. 화면의 레거시 조회 키는
    /// NormalizedName을 사용하므로 첫 항목은 기존 키를 유지하고 후속 항목에 안정적인
    /// BSG/DB ID 접미사를 부여해 모든 퀘스트를 별도 항목으로 보존한다.
    /// </summary>
    private void EnsureUniqueNormalizedNames(List<TarkovTask> quests)
    {
        var duplicateGroups = quests
            .Where(quest => !string.IsNullOrWhiteSpace(quest.NormalizedName))
            .GroupBy(quest => quest.NormalizedName!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        if (duplicateGroups.Count == 0)
            return;

        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedCount = 0;

        foreach (var quest in quests)
        {
            var baseKey = quest.NormalizedName;
            if (string.IsNullOrWhiteSpace(baseKey))
                continue;

            if (usedKeys.Add(baseKey))
                continue;

            var suffix = BuildStableQuestKeySuffix(quest);
            var candidate = $"{baseKey}--{suffix}";
            var collisionIndex = 2;
            while (!usedKeys.Add(candidate))
            {
                candidate = $"{baseKey}--{suffix}-{collisionIndex}";
                collisionIndex++;
            }

            quest.NormalizedName = candidate;
            renamedCount++;
        }

        var samples = duplicateGroups
            .Take(8)
            .Select(group => $"{group.Key}({group.Count()})");
        _log.Warning(
            $"Resolved {renamedCount} duplicate normalized quest keys across " +
            $"{duplicateGroups.Count} name groups: {string.Join(", ", samples)}");
    }

    private static string BuildStableQuestKeySuffix(TarkovTask quest)
    {
        var stableId = quest.Ids?
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? quest.Ids?.FirstOrDefault()
            ?? "quest";

        var normalized = new string(stableId
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
            return "quest";

        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    /// <summary>
    /// 선행 퀘스트 요구사항 로드
    /// </summary>
    private async Task<bool> LoadQuestRequirementsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestRequirements"))
            return false;

        var hasStatusesJson = await ColumnExistsAsync(connection, "QuestRequirements", "StatusesJson");
        var hasNotes = await ColumnExistsAsync(connection, "QuestRequirements", "Notes");
        var sql = $@"
            SELECT QuestId, RequiredQuestId, RequirementType, GroupId,
                   {(hasStatusesJson ? "StatusesJson" : "NULL")},
                   {(hasNotes ? "Notes" : "NULL")}
            FROM QuestRequirements
            ORDER BY QuestId, GroupId, SortOrder";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var requiredQuestId = reader.GetString(1);
            var requirementType = reader.IsDBNull(2) ? "Complete" : reader.GetString(2);
            var groupId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var statuses = ParseStringArray(reader.IsDBNull(4) ? null : reader.GetString(4));
            if (statuses.Count == 0)
                statuses.Add(requirementType.ToLowerInvariant());
            var notes = reader.IsDBNull(5) ? null : reader.GetString(5);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            // 선행 퀘스트의 NormalizedName 찾기
            if (!questLookup.TryGetValue(requiredQuestId, out var requiredQuest))
                continue;

            var requiredNormalizedName = requiredQuest.NormalizedName;
            if (string.IsNullOrEmpty(requiredNormalizedName))
                continue;

            // Previous 리스트에 추가
            quest.Previous ??= new List<string>();
            if (!quest.Previous.Contains(requiredNormalizedName, StringComparer.OrdinalIgnoreCase))
                quest.Previous.Add(requiredNormalizedName);

            // TaskRequirements에 상세 정보 추가 (GroupId 포함)
            quest.TaskRequirements ??= new List<TaskRequirement>();
            var existing = quest.TaskRequirements.FirstOrDefault(r =>
                r.TaskId.Equals(requiredQuestId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                quest.TaskRequirements.Add(new TaskRequirement
                {
                    TaskId = requiredQuestId,
                    TaskNormalizedName = requiredNormalizedName,
                    Status = statuses,
                    GroupId = groupId,
                    Notes = notes
                });
            }
        }

        return true;
    }

    private async Task<bool> LoadQuestTraderRequirementsAsync(
        SqliteConnection connection,
        Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestTraderRequirements"))
            return false;

        const string sql = """
            SELECT QuestId, TraderId, TraderName, TraderNameKO,
                   RequirementType, CompareMethod, RequiredValue
            FROM QuestTraderRequirements
            ORDER BY QuestId, SortOrder;
            """;
        await using var command = new SqliteCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var questId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            quest.TraderRequirements.Add(new QuestTraderRequirement
            {
                TraderId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                TraderName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                TraderNameKo = reader.IsDBNull(3) ? null : reader.GetString(3),
                RequirementType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CompareMethod = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                RequiredValue = reader.IsDBNull(6) ? 0 : reader.GetDouble(6)
            });
        }

        return true;
    }

    /// <summary>
    /// 퀘스트 목표 로드
    /// </summary>
    private async Task<bool> LoadQuestObjectivesAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestObjectives"))
            return false;

        var sql = @"
            SELECT QuestId, Description
            FROM QuestObjectives
            ORDER BY QuestId, SortOrder";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var description = reader.IsDBNull(1) ? "" : reader.GetString(1);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            if (string.IsNullOrWhiteSpace(description))
                continue;

            quest.Objectives ??= new List<string>();
            quest.Objectives.Add(description);
        }

        return true;
    }

    /// <summary>
    /// 필요 아이템 로드
    /// </summary>
    private async Task<bool> LoadQuestRequiredItemsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestRequiredItems"))
            return false;

        var hasGroupId = await ColumnExistsAsync(connection, "QuestRequiredItems", "RequirementGroupId");
        var hasAlternativeFlag = await ColumnExistsAsync(connection, "QuestRequiredItems", "IsAlternativeGroup");
        var hasAlternativeIds = await ColumnExistsAsync(connection, "QuestRequiredItems", "AlternativeItemIds");
        var hasAlternativeNames = await ColumnExistsAsync(connection, "QuestRequiredItems", "AlternativeItemNames");
        var hasObjectiveId = await ColumnExistsAsync(connection, "QuestRequiredItems", "ObjectiveId");
        var hasObjectiveType = await ColumnExistsAsync(connection, "QuestRequiredItems", "ObjectiveType");
        var hasConsumesItem = await ColumnExistsAsync(connection, "QuestRequiredItems", "ConsumesItem");
        var hasTrackingKind = await ColumnExistsAsync(connection, "QuestRequiredItems", "TrackingKind");
        var hasMinDurability = await ColumnExistsAsync(connection, "QuestRequiredItems", "MinDurability");
        var hasMaxDurability = await ColumnExistsAsync(connection, "QuestRequiredItems", "MaxDurability");
        var sql = $@"
            SELECT QuestId, ItemId, ItemName, Count, RequiresFIR, RequirementType, DogtagMinLevel,
                   {(hasGroupId ? "RequirementGroupId" : "NULL")},
                   {(hasAlternativeFlag ? "IsAlternativeGroup" : "0")},
                   {(hasAlternativeIds ? "AlternativeItemIds" : "NULL")},
                   {(hasAlternativeNames ? "AlternativeItemNames" : "NULL")},
                   {(hasObjectiveId ? "ObjectiveId" : "NULL")},
                   {(hasObjectiveType ? "ObjectiveType" : "RequirementType")},
                   {(hasConsumesItem ? "ConsumesItem" : "1")},
                   {(hasTrackingKind ? "TrackingKind" : "'consumable'")},
                   {(hasMinDurability ? "MinDurability" : "NULL")},
                   {(hasMaxDurability ? "MaxDurability" : "NULL")}
            FROM QuestRequiredItems
            ORDER BY QuestId, SortOrder";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            var itemId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var itemName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var count = reader.IsDBNull(3) ? 1 : reader.GetInt32(3);
            var requiresFir = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
            var requirementType = reader.IsDBNull(5) ? "Required" : reader.GetString(5);
            var dogtagMinLevel = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var groupId = reader.IsDBNull(7) ? null : reader.GetString(7);
            var isAlternative = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;
            var alternativeIds = ParseStringArray(reader.IsDBNull(9) ? null : reader.GetString(9));
            var alternativeNames = ParseStringArray(reader.IsDBNull(10) ? null : reader.GetString(10));
            var objectiveId = reader.IsDBNull(11) ? null : reader.GetString(11);
            var objectiveType = reader.IsDBNull(12) ? requirementType : reader.GetString(12);
            var consumesItem = reader.IsDBNull(13) || reader.GetInt32(13) == 1;
            var trackingKind = reader.IsDBNull(14) ? "consumable" : reader.GetString(14);
            var minDurability = reader.IsDBNull(15) ? (double?)null : reader.GetDouble(15);
            var maxDurability = reader.IsDBNull(16) ? (double?)null : reader.GetDouble(16);

            if (isAlternative && alternativeIds.Count == 0)
                continue;
            if (!isAlternative && string.IsNullOrWhiteSpace(itemId))
                continue;

            quest.RequiredItems ??= new List<QuestItem>();
            quest.RequiredItems.Add(new QuestItem
            {
                ItemNormalizedName = isAlternative ? $"group:{groupId ?? questId}" : itemId!,
                ItemDisplayName = itemName,
                Amount = count,
                FoundInRaid = requiresFir,
                Requirement = requirementType,
                DogtagMinLevel = dogtagMinLevel,
                RequirementGroupId = groupId,
                IsAlternativeGroup = isAlternative,
                AlternativeItemIds = alternativeIds,
                AlternativeItemNames = alternativeNames,
                ObjectiveId = objectiveId,
                ObjectiveType = objectiveType,
                ConsumesItem = consumesItem,
                TrackingKind = trackingKind,
                MinDurability = minDurability,
                MaxDurability = maxDurability
            });
        }

        return true;
    }

    private static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)
                ?.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 대체 퀘스트 로드
    /// </summary>
    private async Task<bool> LoadOptionalQuestsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "OptionalQuests"))
            return false;

        var sql = @"
            SELECT QuestId, AlternativeQuestId
            FROM OptionalQuests";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var alternativeQuestId = reader.GetString(1);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            if (!questLookup.TryGetValue(alternativeQuestId, out var altQuest))
                continue;

            var altNormalizedName = altQuest.NormalizedName;
            if (string.IsNullOrEmpty(altNormalizedName))
                continue;

            quest.AlternativeQuests ??= new List<string>();
            if (!quest.AlternativeQuests.Contains(altNormalizedName, StringComparer.OrdinalIgnoreCase))
                quest.AlternativeQuests.Add(altNormalizedName);
        }

        return true;
    }

    /// <summary>
    /// 공통 후속 퀘스트가 선택지 중 한 퀘스트만 선행 조건으로 저장된 경우, 확인된 동등 선택지만
    /// 같은 OR 그룹에 보충합니다. 실패 상태를 요구하는 평판 복구 퀘스트 등 분기 전용 조건은
    /// 명시 목록에 포함하지 않아 기존 판정을 유지합니다.
    /// </summary>
    private void NormalizeEquivalentAlternativeRequirementGroups(
        List<TarkovTask> quests,
        Dictionary<string, TarkovTask> questLookup)
    {
        var nextGroupId = 1;
        var grouped = 0;
        var supplemented = 0;

        foreach (var quest in quests)
        {
            var requirements = quest.TaskRequirements;
            if (requirements == null || requirements.Count == 0)
                continue;

            var originalRequirements = requirements.ToList();
            var processed = new HashSet<TaskRequirement>();
            foreach (var requirement in originalRequirements)
            {
                if (!processed.Add(requirement) || requirement.GroupId != 0 ||
                    !RequiresCompletedAlternative(requirement))
                {
                    continue;
                }

                var requiredQuest = ResolveRequiredQuest(requirement, questLookup, quests);
                if (requiredQuest == null || requiredQuest.AlternativeQuests is not { Count: > 0 })
                    continue;

                var componentQuests = new List<TarkovTask> { requiredQuest };
                var queue = new Queue<TarkovTask>();
                var seenQuestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                queue.Enqueue(requiredQuest);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentKey = current.Ids?.FirstOrDefault() ?? current.NormalizedName;
                    if (string.IsNullOrWhiteSpace(currentKey) || !seenQuestKeys.Add(currentKey))
                        continue;

                    foreach (var alternativeName in current.AlternativeQuests ?? [])
                    {
                        var alternative = GetAlternativeTask(alternativeName, questLookup, quests);
                        if (alternative == null)
                            continue;

                        var alternativeKey = alternative.Ids?.FirstOrDefault() ?? alternative.NormalizedName;
                        if (!string.IsNullOrWhiteSpace(alternativeKey) &&
                            !seenQuestKeys.Contains(alternativeKey))
                        {
                            componentQuests.Add(alternative);
                            queue.Enqueue(alternative);
                        }
                    }
                }

                componentQuests = componentQuests
                    .GroupBy(value => value.Ids?.FirstOrDefault() ?? value.NormalizedName,
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                    .Select(group => group.First())
                    .ToList();
                if (componentQuests.Count < 2)
                    continue;

                var componentRequirements = new List<TaskRequirement>();
                foreach (var alternativeQuest in componentQuests)
                {
                    var existing = requirements.FirstOrDefault(candidate =>
                        RequiresCompletedAlternative(candidate) &&
                        ReferenceMatches(candidate, alternativeQuest));
                    if (existing == null)
                    {
                        existing = new TaskRequirement
                        {
                            TaskId = alternativeQuest.Ids?.FirstOrDefault() ?? string.Empty,
                            TaskNormalizedName = alternativeQuest.NormalizedName ?? string.Empty,
                            Status = requirement.Status?.ToList() ?? ["complete"],
                            Notes = "Inferred from mutually exclusive quest branch metadata."
                        };
                        requirements.Add(existing);
                        supplemented++;
                    }

                    processed.Add(existing);
                    componentRequirements.Add(existing);
                }

                foreach (var componentRequirement in componentRequirements)
                    componentRequirement.GroupId = nextGroupId;
                nextGroupId++;
                grouped += componentRequirements.Count;
            }
        }

        if (grouped > 0 || supplemented > 0)
        {
            _log.Info(
                $"Normalized {grouped} prerequisite rows into alternative OR groups; " +
                $"supplemented {supplemented} missing branch requirements.");
        }
    }

    private static bool RequiresCompletedAlternative(TaskRequirement requirement)
    {
        var statuses = requirement.Status;
        if (statuses == null || statuses.Count == 0)
            return true;

        return statuses.All(status =>
            string.Equals(status?.Trim(), "complete", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ReferenceMatches(TaskRequirement requirement, TarkovTask quest)
    {
        if (!string.IsNullOrWhiteSpace(requirement.TaskId) &&
            quest.Ids?.Contains(requirement.TaskId, StringComparer.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(requirement.TaskNormalizedName) &&
               string.Equals(
                   requirement.TaskNormalizedName,
                   quest.NormalizedName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static TarkovTask? GetAlternativeTask(
        string reference,
        Dictionary<string, TarkovTask> questLookup,
        List<TarkovTask> quests)
    {
        if (questLookup.TryGetValue(reference, out var byId))
            return byId;

        return quests.FirstOrDefault(quest =>
            string.Equals(quest.NormalizedName, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AreAlternativeQuests(TarkovTask left, TarkovTask right)
    {
        if (string.IsNullOrWhiteSpace(left.NormalizedName) ||
            string.IsNullOrWhiteSpace(right.NormalizedName))
        {
            return false;
        }

        return left.AlternativeQuests?.Contains(right.NormalizedName, StringComparer.OrdinalIgnoreCase) == true ||
               right.AlternativeQuests?.Contains(left.NormalizedName, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static TarkovTask? ResolveRequiredQuest(
        TaskRequirement requirement,
        Dictionary<string, TarkovTask> questLookup,
        List<TarkovTask> quests)
    {
        if (!string.IsNullOrWhiteSpace(requirement.TaskId) &&
            questLookup.TryGetValue(requirement.TaskId, out var requiredById))
        {
            return requiredById;
        }

        if (string.IsNullOrWhiteSpace(requirement.TaskNormalizedName))
            return null;

        return quests.FirstOrDefault(quest =>
            string.Equals(
                quest.NormalizedName,
                requirement.TaskNormalizedName,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// LeadsTo 역참조 구축 (Previous의 역방향)
    /// </summary>
    private void BuildLeadsToReferences(List<TarkovTask> quests)
    {
        var questByName = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests)
        {
            if (!string.IsNullOrWhiteSpace(quest.NormalizedName))
                questByName.TryAdd(quest.NormalizedName, quest);
        }

        foreach (var quest in quests)
        {
            if (quest.Previous == null || string.IsNullOrEmpty(quest.NormalizedName))
                continue;

            foreach (var prevName in quest.Previous)
            {
                if (questByName.TryGetValue(prevName, out var prevQuest))
                {
                    prevQuest.LeadsTo ??= new List<string>();
                    if (!prevQuest.LeadsTo.Contains(quest.NormalizedName, StringComparer.OrdinalIgnoreCase))
                        prevQuest.LeadsTo.Add(quest.NormalizedName);
                }
            }
        }
    }

    /// <summary>
    /// 데이터 새로고침 (기존 데이터를 유지하면서 새 데이터로 atomic swap)
    /// </summary>
    public async Task RefreshAsync()
    {
        _log.Debug("Refreshing quest data...");
        // 성공한 경우에만 UI에 새 데이터 이벤트를 전달한다.
        if (await LoadQuestsAsync())
            OnDataRefreshed();
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
}
