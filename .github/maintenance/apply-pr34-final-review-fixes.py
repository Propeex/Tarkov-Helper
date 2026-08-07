from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


program_path = "TarkovHelper.DatabaseSmoke/Program.cs"
program = read(program_path)
old_error = '''    if (failureMessage == null ||
        !failureMessage.Contains("정적 JSON API와 GraphQL API가 모두 응답하지 않았습니다", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Outage fixture did not return the expected combined API error: {failureMessage ?? "no error"}");
    }

    if (outageHandler.StaticRequestCount != 1 || outageHandler.GraphQlRequestCount != 1)
'''
new_error = '''    if (failureMessage == null ||
        !failureMessage.Contains("GraphQL 예비 경로는 사용하지 않습니다", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Outage fixture did not return the expected fail-closed API error: {failureMessage ?? "no error"}");
    }

    if (outageHandler.StaticRequestCount != 1 || outageHandler.GraphQlRequestCount != 0)
'''
if old_error not in program:
    raise RuntimeError("Outage smoke expectation block not found")
program = program.replace(old_error, new_error, 1)
write(program_path, program)


overlay_path = "TarkovHelper/Services/TarkovDataDatabaseBuilder.Overlay.cs"
overlay = read(overlay_path)
old_counts = '''        var modeCorrectionCount = 0;
        foreach (var (modeName, modeNode) in modes)
        {
            if (modeNode is not JsonObject modeObject)
                throw new InvalidDataException($"퀘스트 보정 데이터의 modes.{modeName} 형식이 잘못되었습니다.");

            modeCorrectionCount += ValidateTaskOverlayContainer(modeObject, $"modes.{modeName}");
        }

        var localeCorrectionCount = 0;
        foreach (var (localeName, localeNode) in locales)
        {
            if (localeNode is not JsonObject localeObject)
                throw new InvalidDataException($"퀘스트 보정 데이터의 locales.{localeName} 형식이 잘못되었습니다.");

            if (localeObject["tasks"] is not null && localeObject["tasks"] is not JsonObject)
            {
                throw new InvalidDataException(
                    $"퀘스트 보정 데이터의 locales.{localeName}.tasks 형식이 잘못되었습니다.");
            }

            if (localeObject["tasks"] is JsonObject localeTasks)
                localeCorrectionCount += localeTasks.Count;
        }

        var totalCorrections = prestige.Count + tasks.Count + tasksAdd.Count +
                               modeCorrectionCount + localeCorrectionCount;
'''
new_counts = '''        var prestigeCorrectionCount = ValidateObjectPatchMap(prestige, "prestige");
        var taskCorrectionCount = ValidateObjectPatchMap(tasks, "tasks");
        var taskAdditionCorrectionCount = ValidateObjectPatchMap(tasksAdd, "tasksAdd");

        var modeCorrectionCount = 0;
        foreach (var (modeName, modeNode) in modes)
        {
            if (modeNode is not JsonObject modeObject)
                throw new InvalidDataException($"퀘스트 보정 데이터의 modes.{modeName} 형식이 잘못되었습니다.");

            modeCorrectionCount += ValidateTaskOverlayContainer(modeObject, $"modes.{modeName}");
        }

        var localeCorrectionCount = 0;
        foreach (var (localeName, localeNode) in locales)
        {
            if (localeNode is not JsonObject localeObject)
                throw new InvalidDataException($"퀘스트 보정 데이터의 locales.{localeName} 형식이 잘못되었습니다.");

            if (localeObject["tasks"] is not null && localeObject["tasks"] is not JsonObject)
            {
                throw new InvalidDataException(
                    $"퀘스트 보정 데이터의 locales.{localeName}.tasks 형식이 잘못되었습니다.");
            }

            if (localeObject["tasks"] is JsonObject localeTasks)
            {
                localeCorrectionCount += ValidateObjectPatchMap(
                    localeTasks,
                    $"locales.{localeName}.tasks");
            }
        }

        var totalCorrections = prestigeCorrectionCount + taskCorrectionCount +
                               taskAdditionCorrectionCount + modeCorrectionCount +
                               localeCorrectionCount;
'''
if old_counts not in overlay:
    raise RuntimeError("Overlay correction count block not found")
overlay = overlay.replace(old_counts, new_counts, 1)

old_helper = '''    private static int ValidateTaskOverlayContainer(JsonObject container, string label)
    {
        var correctionCount = 0;
        if (container["tasks"] is not null && container["tasks"] is not JsonObject)
            throw new InvalidDataException($"퀘스트 보정 데이터의 {label}.tasks 형식이 잘못되었습니다.");
        if (container["tasksAdd"] is not null && container["tasksAdd"] is not JsonObject)
            throw new InvalidDataException($"퀘스트 보정 데이터의 {label}.tasksAdd 형식이 잘못되었습니다.");

        if (container["tasks"] is JsonObject tasks)
            correctionCount += tasks.Count;
        if (container["tasksAdd"] is JsonObject tasksAdd)
            correctionCount += tasksAdd.Count;
        return correctionCount;
    }
'''
new_helper = '''    private static int ValidateTaskOverlayContainer(JsonObject container, string label)
    {
        var correctionCount = 0;
        if (container["tasks"] is not null && container["tasks"] is not JsonObject)
            throw new InvalidDataException($"퀘스트 보정 데이터의 {label}.tasks 형식이 잘못되었습니다.");
        if (container["tasksAdd"] is not null && container["tasksAdd"] is not JsonObject)
            throw new InvalidDataException($"퀘스트 보정 데이터의 {label}.tasksAdd 형식이 잘못되었습니다.");

        if (container["tasks"] is JsonObject tasks)
            correctionCount += ValidateObjectPatchMap(tasks, $"{label}.tasks");
        if (container["tasksAdd"] is JsonObject tasksAdd)
            correctionCount += ValidateObjectPatchMap(tasksAdd, $"{label}.tasksAdd");
        return correctionCount;
    }

    private static int ValidateObjectPatchMap(JsonObject map, string label)
    {
        foreach (var (id, node) in map)
        {
            if (node is not JsonObject)
            {
                throw new InvalidDataException(
                    $"퀘스트 보정 데이터의 {label}.{id} 항목이 JSON 객체가 아닙니다.");
            }
        }

        return map.Count;
    }
'''
if old_helper not in overlay:
    raise RuntimeError("Overlay task-container validation helper not found")
overlay = overlay.replace(old_helper, new_helper, 1)
write(overlay_path, overlay)

print("PR34 final review fixes staged successfully.")
