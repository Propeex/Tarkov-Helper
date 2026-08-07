from pathlib import Path


def replace_once_or_already(text: str, old: str, new: str, label: str) -> str:
    old_count = text.count(old)
    new_count = text.count(new)
    if old_count == 1:
        return text.replace(old, new, 1)
    if old_count == 0 and new_count >= 1:
        return text
    raise SystemExit(f"{label}: old={old_count}, new={new_count}")

program = Path("TarkovHelper.DatabaseSmoke/Program.cs")
text = program.read_text(encoding="utf-8")
text = replace_once_or_already(
    text,
    "(Id, BsgId, Name, NameEN, NormalizedName, Trader, Location,",
    "(Id, BsgId, Name, NameEN, Trader, Location,",
    "Program columns")
text = replace_once_or_already(
    text,
    "($id, $id, $name, $name, $normalized, 'Trader', 'any',",
    "($id, $id, $name, $name, 'Trader', 'any',",
    "Program values")
text = replace_once_or_already(
    text,
    '        questCommand.Parameters.AddWithValue("$normalized", questId);\n',
    "",
    "Program normalized parameter")
program.write_text(text, encoding="utf-8")

sql = Path("TarkovHelper/Services/TarkovDataDatabaseBuilder.Sql.cs")
text = sql.read_text(encoding="utf-8")
old = '''        var invalidSourceJson = await ExecuteScalarLongAsync(connection, """
            SELECT
                (SELECT COUNT(*) FROM Items WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1) +
                (SELECT COUNT(*) FROM Quests WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1) +
                (SELECT COUNT(*) FROM QuestObjectives WHERE SourceJson IS NULL OR json_valid(SourceJson) != 1);
            """, cancellationToken);
        if (invalidSourceJson != 0)
            throw new InvalidDataException($"API 원본 JSON 보존 오류: {invalidSourceJson}개");
'''
new = '''        var invalidItemSourceJson = await ExecuteScalarLongAsync(
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
                $"API 원본 JSON 보존 오류: total={invalidSourceJson}, " +
                $"items={invalidItemSourceJson}, quests={invalidQuestSourceJson}, " +
                $"objectives={invalidObjectiveSourceJson}");
        }
'''
text = replace_once_or_already(text, old, new, "Raw JSON validation")
sql.write_text(text, encoding="utf-8")

readme = Path("README.md")
text = readme.read_text(encoding="utf-8")
text = replace_once_or_already(
    text,
    "- 퀘스트 시작 시 선행 퀘스트 자동 완료 처리",
    "- 실제 수주·완료 기록과 선행 조건을 분리하여 안전하게 진행도 추적",
    "README prerequisite wording")
readme.write_text(text, encoding="utf-8")
