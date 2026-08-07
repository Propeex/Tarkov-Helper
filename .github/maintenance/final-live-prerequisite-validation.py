from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, found {count}")
    return text.replace(old, new, 1)

program = Path("TarkovHelper.DatabaseSmoke/Program.cs")
text = program.read_text(encoding="utf-8")
old_metrics = '''        var helpingHandLevel = await ScalarAsync(connection, "SELECT COALESCE(MAX(MinLevel), -1) FROM Quests WHERE Name = 'A Helping Hand';");
        var helpingHandPrerequisites = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements r
            JOIN Quests source ON source.Id = r.QuestId
            JOIN Quests required ON required.Id = r.RequiredQuestId
            WHERE source.Name = 'A Helping Hand' AND required.Name = 'Saving the Mole';
            """);
'''
new_metrics = '''        var questMinLevelSourceMismatches = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM Quests q
            WHERE json_valid(q.SourceJson) = 1
              AND COALESCE(q.MinLevel, 0) !=
                  COALESCE(CAST(json_extract(q.SourceJson, '$.minPlayerLevel') AS INTEGER), 0);
            """);
        var questPrerequisiteCountMismatches = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM Quests q
            WHERE json_valid(q.SourceJson) = 1
              AND COALESCE(json_array_length(json_extract(q.SourceJson, '$.taskRequirements')), 0) !=
                  (SELECT COUNT(*) FROM QuestRequirements r WHERE r.QuestId = q.Id);
            """);
        var invalidPrerequisiteSourceJsonRows = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements
            WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;
            """);
'''
text = replace_once(text, old_metrics, new_metrics, "live prerequisite metrics")
old_assert = '''        if (helpingHandLevel != 20 || helpingHandPrerequisites != 1)
        {
            throw new InvalidDataException(
                $"A Helping Hand start conditions were lost during API refresh: " +
                $"level={helpingHandLevel}, prerequisites={helpingHandPrerequisites}.");
        }

'''
new_assert = '''        if (questMinLevelSourceMismatches != 0 ||
            questPrerequisiteCountMismatches != 0 ||
            invalidPrerequisiteSourceJsonRows != 0)
        {
            throw new InvalidDataException(
                $"Live quest start-condition mapping diverged from current API source data: " +
                $"minLevels={questMinLevelSourceMismatches}, " +
                $"prerequisiteCounts={questPrerequisiteCountMismatches}, " +
                $"prerequisiteSourceJson={invalidPrerequisiteSourceJsonRows}.");
        }

'''
text = replace_once(text, old_assert, new_assert, "live prerequisite assertion")
old_output = '''            $"questTraderRequirements={questTraderRequirementRows}, trackedQuestItems={trackedQuestItemRows}, " +
            $"expandedAmmo={expandedAmmoRows}, A Helping Hand level={helpingHandLevel}, " +
            $"prerequisiteLinks={helpingHandPrerequisites}.");
'''
new_output = '''            $"questTraderRequirements={questTraderRequirementRows}, trackedQuestItems={trackedQuestItemRows}, " +
            $"expandedAmmo={expandedAmmoRows}, prerequisiteMinLevelMismatches={questMinLevelSourceMismatches}, " +
            $"prerequisiteCountMismatches={questPrerequisiteCountMismatches}.");
'''
text = replace_once(text, old_output, new_output, "live prerequisite output")
program.write_text(text, encoding="utf-8")

sql = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Sql.cs")
text = sql.read_text(encoding="utf-8")
old = '''            var keyParts = keyColumns
                .Select(column => ReadString(row, column))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
'''
new = '''            var keyParts = keyColumns
                .Select(column => ReadString(row, column) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
'''
text = replace_once(text, old, new, "nullable composite key")
sql.write_text(text, encoding="utf-8")
