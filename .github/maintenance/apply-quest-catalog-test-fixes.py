from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected one match in {path}, found {count}: {old[:100]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


program = "TarkovHelper.DatabaseSmoke/Program.cs"
replace_once(
    program,
    '''    if (!string.Equals(correctedFixtureQuest.Name, "Corrected First Fixture Quest", StringComparison.Ordinal) ||\n        !string.Equals(correctedFixtureQuest.NameKo, "보정된 첫 번째 퀘스트", StringComparison.Ordinal))\n    {\n        throw new InvalidDataException(\n            $"Quest locale overlay was not applied: en={correctedFixtureQuest.Name}, ko={correctedFixtureQuest.NameKo}.");\n    }\n''',
    '''    if (!string.Equals(correctedFixtureQuest.NameKo, "보정된 첫 번째 퀘스트", StringComparison.Ordinal))\n    {\n        throw new InvalidDataException(\n            $"Quest Korean locale overlay was not applied: ko={correctedFixtureQuest.NameKo}.");\n    }\n''')

replace_once(
    program,
    '''    var koreanQuests = await ScalarAsync(connection,\n        "SELECT COUNT(*) FROM Quests WHERE NameKO IS NOT NULL AND NameKO != '' AND NameKO != NameEN;");\n    var questItemLinks = await ScalarAsync(connection,\n''',
    '''    var koreanQuests = await ScalarAsync(connection,\n        "SELECT COUNT(*) FROM Quests WHERE NameKO IS NOT NULL AND NameKO != '' AND NameKO != NameEN;");\n    var correctedOverlayEnglishRows = await ScalarAsync(connection, """\n        SELECT COUNT(*) FROM Quests\n        WHERE BsgId = 'fixture-quest-first'\n          AND json_valid(SourceJson) = 1\n          AND json_extract(SourceJson, '$.name') = 'Corrected First Fixture Quest';\n        """);\n    var deterministicOverlayMetadataRows = await ScalarAsync(connection, """\n        SELECT COUNT(*) FROM ContentBuildMetadata\n        WHERE Id = 'current'\n          AND Source = 'tarkov.dev + tarkov-data-overlay fixture-1'\n          AND Transport = 'static-json+overlay';\n        """);\n    var questItemLinks = await ScalarAsync(connection,\n''')

replace_once(
    program,
    '''    if (koreanItems < 4 || koreanQuests < 2)\n        throw new InvalidDataException("Korean localized names were not written correctly.");\n''',
    '''    if (koreanItems < 4 || koreanQuests < 2)\n        throw new InvalidDataException("Korean localized names were not written correctly.");\n    if (correctedOverlayEnglishRows != 1 || deterministicOverlayMetadataRows != 1)\n    {\n        throw new InvalidDataException(\n            $"Quest catalog overlay provenance was not persisted: " +\n            $"english={correctedOverlayEnglishRows}, metadata={deterministicOverlayMetadataRows}.");\n    }\n''')

replace_once(
    program,
    '''        var invalidPrerequisiteSourceJsonRows = await ScalarAsync(connection, """\n            SELECT COUNT(*)\n            FROM QuestRequirements\n            WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;\n            """);\n''',
    '''        var invalidPrerequisiteSourceJsonRows = await ScalarAsync(connection, """\n            SELECT COUNT(*)\n            FROM QuestRequirements\n            WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1;\n            """);\n        var questCatalogOverlayMetadataRows = await ScalarAsync(connection, """\n            SELECT COUNT(*) FROM ContentBuildMetadata\n            WHERE Id = 'current'\n              AND Source LIKE 'tarkov.dev + tarkov-data-overlay %'\n              AND Transport = 'static-json+overlay';\n            """);\n        var staleNeuanfangRows = await ScalarAsync(connection,\n            "SELECT COUNT(*) FROM Quests WHERE Name = 'Neuanfang' OR NameEN = 'Neuanfang';");\n        var newBeginningRows = await ScalarAsync(connection,\n            "SELECT COUNT(*) FROM Quests WHERE Name = 'New Beginning';");\n        var newBeginningPrestigeRows = await ScalarAsync(connection, """\n            SELECT COUNT(*) FROM Quests\n            WHERE Name = 'New Beginning'\n              AND RequiredPrestigeLevel IN (1, 2, 3, 4, 5);\n            """);\n        var addedNewBeginningRows = await ScalarAsync(connection, """\n            SELECT COUNT(*) FROM Quests\n            WHERE (BsgId = 'new_beginning_prestige_5' AND RequiredPrestigeLevel = 4)\n               OR (BsgId = 'new_beginning_prestige_6' AND RequiredPrestigeLevel = 5);\n            """);\n''')

replace_once(
    program,
    '''        if (questMinLevelSourceMismatches != 0 ||\n            questPrerequisiteCountMismatches != 0 ||\n            invalidPrerequisiteSourceJsonRows != 0)\n        {\n            throw new InvalidDataException(\n                $"Live quest start-condition mapping diverged from current API source data: " +\n                $"minLevels={questMinLevelSourceMismatches}, " +\n                $"prerequisiteCounts={questPrerequisiteCountMismatches}, " +\n                $"prerequisiteSourceJson={invalidPrerequisiteSourceJsonRows}." );\n        }\n'''.replace('Rows}." );', 'Rows}." );'),
    '''        if (questMinLevelSourceMismatches != 0 ||\n            questPrerequisiteCountMismatches != 0 ||\n            invalidPrerequisiteSourceJsonRows != 0)\n        {\n            throw new InvalidDataException(\n                $"Live quest start-condition mapping diverged from current API source data: " +\n                $"minLevels={questMinLevelSourceMismatches}, " +\n                $"prerequisiteCounts={questPrerequisiteCountMismatches}, " +\n                $"prerequisiteSourceJson={invalidPrerequisiteSourceJsonRows}.");\n        }\n        if (questCatalogOverlayMetadataRows != 1 || staleNeuanfangRows != 0 ||\n            newBeginningRows != 6 || newBeginningPrestigeRows != 5 || addedNewBeginningRows != 2)\n        {\n            throw new InvalidDataException(\n                $"Live quest catalog corrections are incomplete: overlay={questCatalogOverlayMetadataRows}, " +\n                $"neuanfang={staleNeuanfangRows}, newBeginning={newBeginningRows}, " +\n                $"prestigeMapped={newBeginningPrestigeRows}, added={addedNewBeginningRows}.");\n        }\n''')

replace_once(
    program,
    '''            $"expandedAmmo={expandedAmmoRows}, prerequisiteMinLevelMismatches={questMinLevelSourceMismatches}, " +\n            $"prerequisiteCountMismatches={questPrerequisiteCountMismatches}." );\n'''.replace('Mismatches}." );', 'Mismatches}.");'),
    '''            $"expandedAmmo={expandedAmmoRows}, prerequisiteMinLevelMismatches={questMinLevelSourceMismatches}, " +\n            $"prerequisiteCountMismatches={questPrerequisiteCountMismatches}, " +\n            $"newBeginning={newBeginningRows}, overlayMetadata={questCatalogOverlayMetadataRows}.");\n''')

print("quest catalog regression tests updated")
