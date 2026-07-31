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
    private async Task<DatabaseCounts> RewriteDatabaseAsync(
        string databasePath,
        MergedApiData data,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 60
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=OFF;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=60000;", cancellationToken);
        await EnsureAmmoTableAsync(connection, cancellationToken);
        await EnsureQuestRequiredItemColumnsAsync(connection, cancellationToken);

        var requiredTables = new[]
        {
            "Items", "Quests", "QuestRequirements", "QuestObjectives", "QuestRequiredItems",
            "HideoutStations", "HideoutLevels", "HideoutItemRequirements",
            "HideoutStationRequirements", "HideoutTraderRequirements", "HideoutSkillRequirements", "Ammo"
        };

        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken))
                throw new InvalidDataException($"기존 데이터베이스에 필수 테이블 {table}이 없습니다.");
        }

        var snapshots = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in requiredTables.Append("OptionalQuests"))
        {
            if (await TableExistsAsync(connection, table, cancellationToken))
                snapshots[table] = await ReadSnapshotAsync(connection, table, cancellationToken);
        }

        var itemOldByBsg = IndexRows(snapshots["Items"].Rows, "BsgId", "Id");
        var questOldByBsg = IndexRows(snapshots["Quests"].Rows, "BsgId", "Id");
        var stationOldById = IndexRows(snapshots["HideoutStations"].Rows, "Id");
        var objectiveOldById = IndexRows(snapshots["QuestObjectives"].Rows, "Id");
        var requirementOld = IndexRows(snapshots["QuestRequirements"].Rows, "QuestId", "RequiredQuestId");

        var itemRows = new List<RowData>(data.Items.Count);
        var itemIdByApiId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var localized in data.Items)
        {
            var item = localized.English;
            var old = itemOldByBsg.GetValueOrDefault(item.Id);
            var dbId = ReadString(old, "Id") ?? item.Id;
            itemIdByApiId[item.Id] = dbId;

            var row = CloneRow(old);
            Set(row, "Id", dbId);
            Set(row, "BsgId", item.Id);
            Set(row, "Name", Fallback(item.Name, item.Id));
            Set(row, "NameEN", Fallback(item.Name, item.Id));
            Set(row, "NameKO", Fallback(localized.Korean?.Name, item.Name, item.Id));
            PreserveOrSet(row, "NameJA", old, null);
            Set(row, "ShortNameEN", Fallback(item.ShortName, item.Name, item.Id));
            Set(row, "ShortNameKO", Fallback(localized.Korean?.ShortName, localized.Korean?.Name, item.ShortName, item.Name, item.Id));
            PreserveOrSet(row, "ShortNameJA", old, null);
            Set(row, "DescriptionEN", item.Description);
            Set(row, "DescriptionKO", Fallback(localized.Korean?.Description, item.Description));
            Set(row, "NormalizedName", Fallback(item.NormalizedName, Normalize(item.Name), item.Id));
            Set(row, "WikiPageLink", item.WikiLink);
            Set(row, "IconUrl", item.IconLink);
            Set(row, "Category", item.Category?.Name ?? item.Categories.FirstOrDefault()?.Name);
            Set(row, "Categories", JsonSerializer.Serialize(item.Categories.Select(category => category.Name).Where(name => !string.IsNullOrWhiteSpace(name))));
            Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            itemRows.Add(row);
        }

        var ammoRows = new List<RowData>();
        foreach (var localized in data.Items)
        {
            var item = localized.English;
            var ammo = item.Properties;
            if (ammo == null || string.IsNullOrWhiteSpace(ammo.Caliber))
                continue;

            var row = new RowData();
            Set(row, "ItemId", item.Id);
            Set(row, "Caliber", ammo.Caliber);
            Set(row, "ProjectileCount", Math.Max(1, ammo.ProjectileCount ?? 1));
            Set(row, "Damage", ammo.Damage ?? 0);
            Set(row, "ArmorDamage", ammo.ArmorDamage ?? 0);
            Set(row, "FragmentationChance", ammo.FragmentationChance ?? 0);
            Set(row, "PenetrationPower", ammo.PenetrationPower ?? 0);
            Set(row, "AccuracyModifier", ammo.AccuracyModifier ?? 0);
            Set(row, "RecoilModifier", ammo.RecoilModifier ?? 0);
            Set(row, "LightBleedModifier", ammo.LightBleedModifier ?? 0);
            Set(row, "HeavyBleedModifier", ammo.HeavyBleedModifier ?? 0);
            Set(row, "InitialSpeed", ammo.InitialSpeed ?? 0);
            Set(row, "AcquisitionSource", ResolveAcquisitionSource(item, ammo));
            Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ammoRows.Add(row);
        }

        var questRows = new List<RowData>(data.Tasks.Count);
        var questIdByApiId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var localized in data.Tasks)
        {
            var task = localized.English;
            var old = questOldByBsg.GetValueOrDefault(task.Id);
            var dbId = ReadString(old, "Id") ?? task.Id;
            questIdByApiId[task.Id] = dbId;

            var row = CloneRow(old);
            Set(row, "Id", dbId);
            Set(row, "BsgId", task.Id);
            Set(row, "Name", Fallback(task.Name, task.Id));
            Set(row, "NameEN", Fallback(task.Name, task.Id));
            Set(row, "NameKO", Fallback(localized.Korean?.Name, task.Name, task.Id));
            PreserveOrSet(row, "NameJA", old, null);
            Set(row, "Trader", Fallback(task.Trader?.Name, task.Trader?.NormalizedName, "Unknown"));
            Set(row, "Location", task.Map?.NormalizedName ?? "any");
            Set(row, "MinLevel", task.MinPlayerLevel);
            Set(row, "KappaRequired", task.KappaRequired ? 1 : 0);
            Set(row, "Faction", task.FactionName);
            Set(row, "NormalizedName", Fallback(task.NormalizedName, Normalize(task.Name), task.Id));
            Set(row, "RequiredPrestigeLevel", task.RequiredPrestige?.PrestigeLevel);
            Set(row, "WikiPageLink", task.WikiLink);
            Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            questRows.Add(row);
        }

        var questRequirementRows = new List<RowData>();
        var questObjectiveRows = new List<RowData>();
        var questRequiredItemRows = new List<RowData>();

        foreach (var localized in data.Tasks)
        {
            var task = localized.English;
            if (!questIdByApiId.TryGetValue(task.Id, out var questId))
                continue;

            for (var index = 0; index < task.TaskRequirements.Count; index++)
            {
                var requirement = task.TaskRequirements[index];
                if (requirement.Task?.Id is not { Length: > 0 } apiRequiredId ||
                    !questIdByApiId.TryGetValue(apiRequiredId, out var requiredQuestId))
                    continue;

                var oldKey = BuildCompositeKey(questId, requiredQuestId);
                var old = requirementOld.GetValueOrDefault(oldKey);
                var row = CloneRow(old);
                Set(row, "QuestId", questId);
                Set(row, "RequiredQuestId", requiredQuestId);
                Set(row, "RequirementType", requirement.Status.FirstOrDefault() ?? "complete");
                if (!HasValue(row, "GroupId"))
                    Set(row, "GroupId", 0);
                Set(row, "SortOrder", index);
                Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                questRequirementRows.Add(row);
            }

            var koreanObjectives = localized.Korean?.Objectives
                .Where(objective => !string.IsNullOrWhiteSpace(objective.Id))
                .ToDictionary(objective => objective.Id!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ApiTaskObjective>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < task.Objectives.Count; index++)
            {
                var objective = task.Objectives[index];
                var objectiveId = Fallback(objective.Id, $"{task.Id}:objective:{index}")!;
                var old = objectiveOldById.GetValueOrDefault(objectiveId);
                var row = CloneRow(old);
                koreanObjectives.TryGetValue(objectiveId, out var objectiveKo);

                Set(row, "Id", objectiveId);
                Set(row, "QuestId", questId);
                Set(row, "Description", Fallback(objectiveKo?.Description, objective.Description, objective.Type, objectiveId));
                Set(row, "DescriptionEN", objective.Description);
                Set(row, "DescriptionKO", Fallback(objectiveKo?.Description, objective.Description));
                Set(row, "MapName", objective.Maps.FirstOrDefault()?.NormalizedName ?? task.Map?.NormalizedName);
                Set(row, "ObjectiveType", objective.Type);
                Set(row, "Optional", objective.Optional ? 1 : 0);
                Set(row, "SortOrder", index);
                Set(row, "TargetCount", objective.Count);
                Set(row, "RequiresFIR", objective.FoundInRaid == true ? 1 : 0);
                Set(row, "DogtagMinLevel", objective.DogTagLevel);
                Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

                var primaryObjectiveItem = objective.Items.FirstOrDefault();
                if (primaryObjectiveItem != null && itemIdByApiId.TryGetValue(primaryObjectiveItem.Id, out var primaryItemId))
                {
                    Set(row, "ItemId", primaryItemId);
                    Set(row, "ItemName", Fallback(objectiveKo?.Items.FirstOrDefault(item => item.Id == primaryObjectiveItem.Id)?.Name, primaryObjectiveItem.Name, primaryObjectiveItem.Id));
                }

                questObjectiveRows.Add(row);

                if (!string.Equals(objective.Type, "item", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(objective.TypeName, "TaskObjectiveItem", StringComparison.OrdinalIgnoreCase))
                    continue;

                var koItems = objectiveKo?.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ApiItemReference>(StringComparer.OrdinalIgnoreCase);

                var validRequiredItems = objective.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && itemIdByApiId.ContainsKey(item.Id))
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (validRequiredItems.Count > 1)
                {
                    var alternativeIds = validRequiredItems.Select(item => itemIdByApiId[item.Id]).ToList();
                    var alternativeNames = validRequiredItems.Select(item =>
                    {
                        koItems.TryGetValue(item.Id, out var localizedItem);
                        return Fallback(localizedItem?.Name, item.Name, item.Id)!;
                    }).ToList();
                    var itemRow = new RowData();
                    Set(itemRow, "QuestId", questId);
                    Set(itemRow, "ObjectiveId", objectiveId);
                    Set(itemRow, "ItemId", null);
                    Set(itemRow, "ItemName", string.Join(", ", alternativeNames));
                    Set(itemRow, "ItemNameKO", string.Join(", ", alternativeNames));
                    Set(itemRow, "Count", Math.Max(1, objective.Count ?? 1));
                    Set(itemRow, "RequiresFIR", objective.FoundInRaid == true ? 1 : 0);
                    Set(itemRow, "RequirementType", objective.Type);
                    Set(itemRow, "DogtagMinLevel", objective.DogTagLevel);
                    Set(itemRow, "RequirementGroupId", objectiveId);
                    Set(itemRow, "IsAlternativeGroup", 1);
                    Set(itemRow, "AlternativeItemIds", JsonSerializer.Serialize(alternativeIds));
                    Set(itemRow, "AlternativeItemNames", JsonSerializer.Serialize(alternativeNames));
                    Set(itemRow, "SortOrder", index);
                    Set(itemRow, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    questRequiredItemRows.Add(itemRow);
                }
                else if (validRequiredItems.Count == 1)
                {
                    var requiredItem = validRequiredItems[0];
                    var itemId = itemIdByApiId[requiredItem.Id];
                    koItems.TryGetValue(requiredItem.Id, out var requiredItemKo);
                    var itemRow = new RowData();
                    Set(itemRow, "QuestId", questId);
                    Set(itemRow, "ObjectiveId", objectiveId);
                    Set(itemRow, "ItemId", itemId);
                    Set(itemRow, "ItemName", Fallback(requiredItemKo?.Name, requiredItem.Name, requiredItem.Id));
                    Set(itemRow, "ItemNameKO", Fallback(requiredItemKo?.Name, requiredItem.Name, requiredItem.Id));
                    Set(itemRow, "Count", Math.Max(1, objective.Count ?? 1));
                    Set(itemRow, "RequiresFIR", objective.FoundInRaid == true ? 1 : 0);
                    Set(itemRow, "RequirementType", objective.Type);
                    Set(itemRow, "DogtagMinLevel", objective.DogTagLevel);
                    Set(itemRow, "RequirementGroupId", null);
                    Set(itemRow, "IsAlternativeGroup", 0);
                    Set(itemRow, "AlternativeItemIds", null);
                    Set(itemRow, "AlternativeItemNames", null);
                    Set(itemRow, "SortOrder", index);
                    Set(itemRow, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    questRequiredItemRows.Add(itemRow);
                }
            }
        }

        var preservedQuestLocations = PreserveLegacyQuestObjectiveLocations(
            snapshots["QuestObjectives"],
            questRows,
            questObjectiveRows);
        if (preservedQuestLocations > 0)
            Log.Info($"기존 퀘스트 지도 좌표 {preservedQuestLocations:N0}개를 보존했습니다.");

        var hideoutStationRows = new List<RowData>(data.HideoutStations.Count);
        var hideoutLevelRows = new List<RowData>();
        var hideoutItemRows = new List<RowData>();
        var hideoutStationRequirementRows = new List<RowData>();
        var hideoutTraderRequirementRows = new List<RowData>();
        var hideoutSkillRequirementRows = new List<RowData>();

        foreach (var localized in data.HideoutStations)
        {
            var station = localized.English;
            var old = stationOldById.GetValueOrDefault(station.Id);
            var stationRow = CloneRow(old);
            Set(stationRow, "Id", station.Id);
            Set(stationRow, "Name", Fallback(station.Name, station.Id));
            Set(stationRow, "NameEN", Fallback(station.Name, station.Id));
            Set(stationRow, "NameKO", Fallback(localized.Korean?.Name, station.Name, station.Id));
            PreserveOrSet(stationRow, "NameJA", old, null);
            Set(stationRow, "NormalizedName", Fallback(station.NormalizedName, Normalize(station.Name), station.Id));
            Set(stationRow, "ImageLink", station.ImageLink);
            Set(stationRow, "MaxLevel", station.Levels.Count == 0 ? 0 : station.Levels.Max(level => level.Level));
            Set(stationRow, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            hideoutStationRows.Add(stationRow);

            var koLevels = localized.Korean?.Levels.ToDictionary(level => level.Level)
                ?? new Dictionary<int, ApiHideoutLevel>();

            foreach (var level in station.Levels)
            {
                koLevels.TryGetValue(level.Level, out var levelKo);
                var levelRow = new RowData();
                Set(levelRow, "Id", Fallback(level.Id, $"{station.Id}:{level.Level}"));
                Set(levelRow, "StationId", station.Id);
                Set(levelRow, "Level", level.Level);
                Set(levelRow, "ConstructionTime", level.ConstructionTime);
                Set(levelRow, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                hideoutLevelRows.Add(levelRow);

                var koItemRequirements = levelKo?.ItemRequirements
                    .Where(requirement => requirement.Item?.Id is { Length: > 0 })
                    .ToDictionary(requirement => requirement.Item!.Id, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ApiHideoutItemRequirement>(StringComparer.OrdinalIgnoreCase);

                for (var itemIndex = 0; itemIndex < level.ItemRequirements.Count; itemIndex++)
                {
                    var requirement = level.ItemRequirements[itemIndex];
                    if (requirement.Item?.Id is not { Length: > 0 } apiItemId ||
                        !itemIdByApiId.ContainsKey(apiItemId))
                        continue;

                    koItemRequirements.TryGetValue(apiItemId, out var requirementKo);
                    var row = new RowData();
                    Set(row, "StationId", station.Id);
                    Set(row, "Level", level.Level);
                    Set(row, "ItemId", apiItemId);
                    Set(row, "ItemName", Fallback(requirement.Item.Name, apiItemId));
                    Set(row, "ItemNameKO", Fallback(requirementKo?.Item?.Name, requirement.Item.Name, apiItemId));
                    Set(row, "ItemNameJA", null);
                    Set(row, "IconLink", requirement.Item.IconLink);
                    Set(row, "Count", Math.Max(1, requirement.Count ?? requirement.Quantity ?? 1));
                    Set(row, "FoundInRaid", 0);
                    Set(row, "SortOrder", itemIndex);
                    Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    hideoutItemRows.Add(row);
                }

                for (var stationIndex = 0; stationIndex < level.StationLevelRequirements.Count; stationIndex++)
                {
                    var requirement = level.StationLevelRequirements[stationIndex];
                    if (requirement.Station?.Id is not { Length: > 0 } requiredStationId)
                        continue;

                    var row = new RowData();
                    Set(row, "StationId", station.Id);
                    Set(row, "Level", level.Level);
                    Set(row, "RequiredStationId", requiredStationId);
                    Set(row, "RequiredStationName", Fallback(requirement.Station.Name, requiredStationId));
                    Set(row, "RequiredStationNameKO", levelKo?.StationLevelRequirements.FirstOrDefault(value => value.Station?.Id == requiredStationId)?.Station?.Name);
                    Set(row, "RequiredStationNameJA", null);
                    Set(row, "RequiredLevel", requirement.Level);
                    Set(row, "SortOrder", stationIndex);
                    Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    hideoutStationRequirementRows.Add(row);
                }

                var koTraderRequirements = levelKo?.TraderRequirements
                    .Where(requirement => requirement.Trader?.Id is { Length: > 0 })
                    .ToDictionary(requirement => requirement.Trader!.Id, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ApiTraderRequirement>(StringComparer.OrdinalIgnoreCase);

                for (var traderIndex = 0; traderIndex < level.TraderRequirements.Count; traderIndex++)
                {
                    var requirement = level.TraderRequirements[traderIndex];
                    if (requirement.Trader?.Id is not { Length: > 0 } traderId)
                        continue;

                    koTraderRequirements.TryGetValue(traderId, out var requirementKo);
                    var row = new RowData();
                    Set(row, "StationId", station.Id);
                    Set(row, "Level", level.Level);
                    Set(row, "TraderId", traderId);
                    Set(row, "TraderName", Fallback(requirement.Trader.Name, requirement.Trader.NormalizedName, traderId));
                    Set(row, "TraderNameKO", Fallback(requirementKo?.Trader?.Name, requirement.Trader.Name, traderId));
                    Set(row, "TraderNameJA", null);
                    Set(row, "RequiredLevel", requirement.Value ?? requirement.Level ?? 0);
                    Set(row, "SortOrder", traderIndex);
                    Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    hideoutTraderRequirementRows.Add(row);
                }

                var koSkillRequirements = levelKo?.SkillRequirements
                    .Where(requirement => !string.IsNullOrWhiteSpace(requirement.Skill?.Id ?? requirement.Name))
                    .ToDictionary(requirement => requirement.Skill?.Id ?? requirement.Name!, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, ApiSkillRequirement>(StringComparer.OrdinalIgnoreCase);

                for (var skillIndex = 0; skillIndex < level.SkillRequirements.Count; skillIndex++)
                {
                    var requirement = level.SkillRequirements[skillIndex];
                    var skillKey = requirement.Skill?.Id ?? requirement.Name;
                    if (string.IsNullOrWhiteSpace(skillKey))
                        continue;

                    koSkillRequirements.TryGetValue(skillKey, out var requirementKo);
                    var row = new RowData();
                    Set(row, "StationId", station.Id);
                    Set(row, "Level", level.Level);
                    Set(row, "SkillId", requirement.Skill?.Id);
                    Set(row, "SkillName", Fallback(requirement.Skill?.Name, requirement.Name, skillKey));
                    Set(row, "SkillNameKO", Fallback(requirementKo?.Skill?.Name, requirementKo?.Name, requirement.Skill?.Name, requirement.Name, skillKey));
                    Set(row, "SkillNameJA", null);
                    Set(row, "RequiredLevel", requirement.Level);
                    Set(row, "SortOrder", skillIndex);
                    Set(row, "UpdatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    hideoutSkillRequirementRows.Add(row);
                }
            }
        }

        var writePlan = new[]
        {
            new TableWrite("Items", itemRows),
            new TableWrite("Ammo", ammoRows),
            new TableWrite("Quests", questRows),
            new TableWrite("QuestRequirements", questRequirementRows),
            new TableWrite("QuestObjectives", questObjectiveRows),
            new TableWrite("QuestRequiredItems", questRequiredItemRows),
            new TableWrite("HideoutStations", hideoutStationRows),
            new TableWrite("HideoutLevels", hideoutLevelRows),
            new TableWrite("HideoutItemRequirements", hideoutItemRows),
            new TableWrite("HideoutStationRequirements", hideoutStationRequirementRows),
            new TableWrite("HideoutTraderRequirements", hideoutTraderRequirementRows),
            new TableWrite("HideoutSkillRequirements", hideoutSkillRequirementRows)
        };

        var totalRows = writePlan.Sum(plan => plan.Rows.Count);
        var completedRows = 0;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var deleteOrder = new[]
            {
                "QuestRequiredItems", "QuestObjectives", "QuestRequirements",
                "HideoutItemRequirements", "HideoutStationRequirements",
                "HideoutTraderRequirements", "HideoutSkillRequirements", "HideoutLevels",
                "Ammo", "Quests", "HideoutStations", "Items"
            };

            foreach (var table in deleteOrder)
                await ExecuteNonQueryAsync(connection, $"DELETE FROM [{table}];", cancellationToken, transaction);

            foreach (var plan in writePlan)
            {
                var snapshot = snapshots[plan.TableName];
                foreach (var row in plan.Rows)
                {
                    await InsertRowAsync(connection, transaction, snapshot.Columns, plan.TableName, row, cancellationToken);
                    completedRows++;
                    if (completedRows % 50 == 0 || completedRows == totalRows)
                    {
                        var fraction = totalRows == 0 ? 1 : completedRows / (double)totalRows;
                        Report(
                            "DB",
                            $"{KoreanTableName(plan.TableName)} 저장 중",
                            65 + 27 * fraction,
                            completedRows,
                            totalRows);
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA optimize;", cancellationToken);

        return new DatabaseCounts(
            itemRows.Count,
            ammoRows.Count,
            questRows.Count,
            questRequirementRows.Count,
            questObjectiveRows.Count,
            questRequiredItemRows.Count,
            hideoutStationRows.Count,
            hideoutLevelRows.Count,
            hideoutItemRows.Count,
            hideoutStationRequirementRows.Count,
            hideoutTraderRequirementRows.Count,
            hideoutSkillRequirementRows.Count);
    }
    private static async Task EnsureQuestRequiredItemColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(QuestRequiredItems);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectiveId"] = "TEXT",
            ["RequirementGroupId"] = "TEXT",
            ["IsAlternativeGroup"] = "INTEGER NOT NULL DEFAULT 0",
            ["AlternativeItemIds"] = "TEXT",
            ["AlternativeItemNames"] = "TEXT"
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name))
                await ExecuteNonQueryAsync(connection, $"ALTER TABLE QuestRequiredItems ADD COLUMN [{name}] {definition};", cancellationToken);
        }
    }

    private static async Task EnsureAmmoTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS Ammo (
                ItemId TEXT PRIMARY KEY,
                Caliber TEXT NOT NULL,
                ProjectileCount INTEGER NOT NULL DEFAULT 1,
                Damage INTEGER NOT NULL DEFAULT 0,
                ArmorDamage INTEGER NOT NULL DEFAULT 0,
                FragmentationChance REAL NOT NULL DEFAULT 0,
                PenetrationPower INTEGER NOT NULL DEFAULT 0,
                AccuracyModifier REAL NOT NULL DEFAULT 0,
                RecoilModifier REAL NOT NULL DEFAULT 0,
                LightBleedModifier REAL NOT NULL DEFAULT 0,
                HeavyBleedModifier REAL NOT NULL DEFAULT 0,
                InitialSpeed REAL NOT NULL DEFAULT 0,
                AcquisitionSource TEXT,
                UpdatedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Ammo_Caliber ON Ammo(Caliber);
            """, cancellationToken);
    }

    private static string ResolveAcquisitionSource(ApiItem item, ApiAmmoProperties ammo)
    {
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(ammo.AcquisitionSource))
        {
            sources.AddRange(ammo.AcquisitionSource
                .Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(source => source.Equals("raid-found", StringComparison.OrdinalIgnoreCase) ||
                                 source.StartsWith("trader:", StringComparison.OrdinalIgnoreCase) ||
                                 source.StartsWith("craft:", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var barter in item.BartersFor)
        {
            var trader = barter.Trader?.Name;
            if (!string.IsNullOrWhiteSpace(trader))
                sources.Add($"trader:{trader}:level:{Math.Max(1, barter.Level ?? 1)}");
        }

        foreach (var craft in item.CraftsFor)
        {
            var station = craft.Station?.Name;
            if (!string.IsNullOrWhiteSpace(station))
                sources.Add($"craft:{station}:level:{Math.Max(1, craft.Level ?? 1)}");
        }

        foreach (var purchase in item.BuyFor)
        {
            var trader = purchase.Vendor?.Name;
            if (!string.IsNullOrWhiteSpace(trader))
                sources.Add($"trader:{trader}");
        }

        if (sources.Count == 0)
            sources.Add("raid-found");

        return string.Join(" · ", sources.Distinct(StringComparer.OrdinalIgnoreCase));
    }


}