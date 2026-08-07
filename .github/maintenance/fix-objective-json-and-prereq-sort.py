from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly 1 match, found {count}")
    return text.replace(old, new, 1)

# 1) New content DBs get a stable prerequisite sort column.
schema = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Schema.cs")
text = schema.read_text(encoding="utf-8")
text = replace_once(
    text,
    '''        await EnsureColumnsAsync(connection, "QuestRequirements", new Dictionary<string, string>
        {
            ["StatusesJson"] = "TEXT",
            ["Notes"] = "TEXT",
            ["SourceJson"] = "TEXT"
        }, cancellationToken);
''',
    '''        await EnsureColumnsAsync(connection, "QuestRequirements", new Dictionary<string, string>
        {
            ["StatusesJson"] = "TEXT",
            ["Notes"] = "TEXT",
            ["SourceJson"] = "TEXT",
            ["SortOrder"] = "INTEGER NOT NULL DEFAULT 0"
        }, cancellationToken);
''',
    "QuestRequirements SortOrder schema")
schema.write_text(text, encoding="utf-8")

# 2) Old bundled/user content DBs remain readable even before migration.
service = Path("TarkovHelper/Services/QuestDbService.cs")
text = service.read_text(encoding="utf-8")
text = replace_once(
    text,
    '''        var hasStatusesJson = await ColumnExistsAsync(connection, "QuestRequirements", "StatusesJson");
        var hasNotes = await ColumnExistsAsync(connection, "QuestRequirements", "Notes");
        var sql = $@"\n            SELECT QuestId, RequiredQuestId, RequirementType, GroupId,\n                   {(hasStatusesJson ? "StatusesJson" : "NULL")},\n                   {(hasNotes ? "Notes" : "NULL")}\n            FROM QuestRequirements\n            ORDER BY QuestId, GroupId, SortOrder";
''',
    '''        var hasStatusesJson = await ColumnExistsAsync(connection, "QuestRequirements", "StatusesJson");
        var hasNotes = await ColumnExistsAsync(connection, "QuestRequirements", "Notes");
        var hasSortOrder = await ColumnExistsAsync(connection, "QuestRequirements", "SortOrder");
        var sql = $@"\n            SELECT QuestId, RequiredQuestId, RequirementType, GroupId,\n                   {(hasStatusesJson ? "StatusesJson" : "NULL")},\n                   {(hasNotes ? "Notes" : "NULL")}\n            FROM QuestRequirements\n            ORDER BY QuestId, GroupId, {(hasSortOrder ? "SortOrder" : "RequiredQuestId")}";
''',
    "QuestRequirements legacy sort compatibility")
service.write_text(text, encoding="utf-8")

# 3) Legacy coordinate preservation can replace objective rows. Re-attach the
# current API source JSON by stable objective ID after preserving coordinates.
writer = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Writer.cs")
text = writer.read_text(encoding="utf-8")
text = replace_once(
    text,
    '''        var preservedQuestLocations = PreserveLegacyQuestObjectiveLocations(
            snapshots["QuestObjectives"],
            questRows,
            questObjectiveRows);
        if (preservedQuestLocations > 0)
            Log.Info($"기존 퀘스트 지도 좌표 {preservedQuestLocations:N0}개를 보존했습니다.");

        var hideoutStationRows = new List<RowData>(data.HideoutStations.Count);
''',
    '''        var preservedQuestLocations = PreserveLegacyQuestObjectiveLocations(
            snapshots["QuestObjectives"],
            questRows,
            questObjectiveRows);
        if (preservedQuestLocations > 0)
            Log.Info($"기존 퀘스트 지도 좌표 {preservedQuestLocations:N0}개를 보존했습니다.");

        // Legacy coordinate preservation may replace a freshly-built objective
        // row with the old row. Restore the current API payload afterwards so
        // hand-maintained coordinates survive without losing source fidelity.
        var objectiveSourceById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var localized in data.Tasks)
        {
            var sourceTask = localized.English;
            for (var objectiveIndex = 0; objectiveIndex < sourceTask.Objectives.Count; objectiveIndex++)
            {
                var sourceObjective = sourceTask.Objectives[objectiveIndex];
                var sourceObjectiveId = Fallback(
                    sourceObjective.Id,
                    $"{sourceTask.Id}:objective:{objectiveIndex}");
                if (!string.IsNullOrWhiteSpace(sourceObjectiveId))
                {
                    objectiveSourceById[sourceObjectiveId!] =
                        sourceObjective.SourceJson ?? JsonSerializer.Serialize(sourceObjective);
                }
            }
        }

        foreach (var objectiveRow in questObjectiveRows)
        {
            var objectiveId = ReadString(objectiveRow, "Id");
            if (!string.IsNullOrWhiteSpace(objectiveId) &&
                objectiveSourceById.TryGetValue(objectiveId, out var sourceJson))
            {
                Set(objectiveRow, "SourceJson", sourceJson);
            }
        }

        var hideoutStationRows = new List<RowData>(data.HideoutStations.Count);
''',
    "Quest objective SourceJson restoration")
writer.write_text(text, encoding="utf-8")
