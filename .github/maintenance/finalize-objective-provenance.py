from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, found {count}")
    return text.replace(old, new, 1)

program = Path("TarkovHelper.DatabaseSmoke/Program.cs")
text = program.read_text(encoding="utf-8")
text = replace_once(
    text,
    "if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 2 || result.HideoutStationCount != 1)",
    "if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 3 || result.HideoutStationCount != 1)",
    "deterministic quest count")
program.write_text(text, encoding="utf-8")

writer = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Writer.cs")
text = writer.read_text(encoding="utf-8")
old = '''        foreach (var objectiveRow in questObjectiveRows)
        {
            var objectiveId = ReadString(objectiveRow, "Id");
            if (!string.IsNullOrWhiteSpace(objectiveId) &&
                objectiveSourceById.TryGetValue(objectiveId, out var sourceJson))
            {
                Set(objectiveRow, "SourceJson", sourceJson);
            }
        }

        var hideoutStationRows = new List<RowData>(data.HideoutStations.Count);
'''
new = '''        foreach (var objectiveRow in questObjectiveRows)
        {
            var objectiveId = ReadString(objectiveRow, "Id");
            if (!string.IsNullOrWhiteSpace(objectiveId) &&
                objectiveSourceById.TryGetValue(objectiveId, out var sourceJson))
            {
                Set(objectiveRow, "SourceJson", sourceJson);
                continue;
            }

            // Some hand-maintained map-coordinate rows no longer have a stable
            // objective ID in the current API. Keep those rows for map fidelity,
            // but record their provenance explicitly instead of pretending they
            // came from the current API or leaving the source unauditable.
            if (string.IsNullOrWhiteSpace(ReadString(objectiveRow, "SourceJson")))
            {
                Set(objectiveRow, "SourceJson", JsonSerializer.Serialize(new
                {
                    sourceKind = "legacy-preserved",
                    id = objectiveId,
                    questId = ReadString(objectiveRow, "QuestId"),
                    objectiveType = ReadString(objectiveRow, "ObjectiveType"),
                    description = ReadString(objectiveRow, "Description"),
                    mapName = ReadString(objectiveRow, "MapName"),
                    locationName = ReadString(objectiveRow, "LocationName")
                }));
            }
        }

        var hideoutStationRows = new List<RowData>(data.HideoutStations.Count);
'''
text = replace_once(text, old, new, "objective provenance fallback")
writer.write_text(text, encoding="utf-8")

sql = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Sql.cs")
text = sql.read_text(encoding="utf-8")
text = replace_once(
    text,
    '$"API 원본 JSON 보존 오류: total={invalidSourceJson}, " +',
    '$"콘텐츠 소스 JSON 보존 오류: total={invalidSourceJson}, " +',
    "source JSON validation wording")
sql.write_text(text, encoding="utf-8")
