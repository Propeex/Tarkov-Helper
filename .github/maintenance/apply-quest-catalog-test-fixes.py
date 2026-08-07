from pathlib import Path

p = Path("TarkovHelper.DatabaseSmoke/Program.cs")
text = p.read_text(encoding="utf-8")

old = '''    if (!string.Equals(correctedFixtureQuest.Name, "Corrected First Fixture Quest", StringComparison.Ordinal) ||
        !string.Equals(correctedFixtureQuest.NameKo, "보정된 첫 번째 퀘스트", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Quest locale overlay was not applied: en={correctedFixtureQuest.Name}, ko={correctedFixtureQuest.NameKo}.");
    }
'''
new = '''    if (!string.Equals(correctedFixtureQuest.NameKo, "보정된 첫 번째 퀘스트", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Quest Korean locale overlay was not applied: ko={correctedFixtureQuest.NameKo}.");
    }
'''
if text.count(old) != 1:
    raise SystemExit("deterministic locale assertion marker mismatch")
text = text.replace(old, new, 1)

marker = '''        var invalidPrerequisiteSourceJsonRows = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements
            WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;
            """);
'''
addition = marker + '''        var questCatalogOverlayMetadataRows = await ScalarAsync(connection, """
            SELECT COUNT(*) FROM ContentBuildMetadata
            WHERE Id = 'current'
              AND Source LIKE 'tarkov.dev + tarkov-data-overlay %'
              AND Transport = 'static-json+overlay';
            """);
        var staleNeuanfangRows = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM Quests WHERE Name = 'Neuanfang' OR NameEN = 'Neuanfang';");
        var newBeginningRows = await ScalarAsync(connection,
            "SELECT COUNT(*) FROM Quests WHERE Name = 'New Beginning';");
        var newBeginningPrestigeRows = await ScalarAsync(connection, """
            SELECT COUNT(*) FROM Quests
            WHERE Name = 'New Beginning'
              AND RequiredPrestigeLevel IN (1, 2, 3, 4, 5);
            """);
        var addedNewBeginningRows = await ScalarAsync(connection, """
            SELECT COUNT(*) FROM Quests
            WHERE (BsgId = 'new_beginning_prestige_5' AND RequiredPrestigeLevel = 4)
               OR (BsgId = 'new_beginning_prestige_6' AND RequiredPrestigeLevel = 5);
            """);
'''
if text.count(marker) != 1:
    raise SystemExit("live catalog SQL insertion marker mismatch")
text = text.replace(marker, addition, 1)

marker = '''        if (questMinLevelSourceMismatches != 0 ||
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
addition = marker + '''        if (questCatalogOverlayMetadataRows != 1 || staleNeuanfangRows != 0 ||
            newBeginningRows != 6 || newBeginningPrestigeRows != 5 || addedNewBeginningRows != 2)
        {
            throw new InvalidDataException(
                $"Live quest catalog corrections are incomplete: overlay={questCatalogOverlayMetadataRows}, " +
                $"neuanfang={staleNeuanfangRows}, newBeginning={newBeginningRows}, " +
                $"prestigeMapped={newBeginningPrestigeRows}, added={addedNewBeginningRows}.");
        }
'''
if text.count(marker) != 1:
    raise SystemExit("live catalog assertion insertion marker mismatch")
text = text.replace(marker, addition, 1)

old = '''            $"expandedAmmo={expandedAmmoRows}, prerequisiteMinLevelMismatches={questMinLevelSourceMismatches}, " +
            $"prerequisiteCountMismatches={questPrerequisiteCountMismatches}.");
'''
new = '''            $"expandedAmmo={expandedAmmoRows}, prerequisiteMinLevelMismatches={questMinLevelSourceMismatches}, " +
            $"prerequisiteCountMismatches={questPrerequisiteCountMismatches}, " +
            $"newBeginning={newBeginningRows}, overlayMetadata={questCatalogOverlayMetadataRows}.");
'''
if text.count(old) != 1:
    raise SystemExit("live validation log marker mismatch")
text = text.replace(old, new, 1)

p.write_text(text, encoding="utf-8")
print("quest catalog regression tests updated")
