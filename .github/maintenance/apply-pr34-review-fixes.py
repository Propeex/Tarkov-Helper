from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_between(text: str, start_marker: str, end_marker: str, replacement: str) -> str:
    start = text.find(start_marker)
    if start < 0:
        raise RuntimeError(f"start marker not found: {start_marker!r}")
    end = text.find(end_marker, start)
    if end < 0:
        raise RuntimeError(f"end marker not found: {end_marker!r}")
    return text[:start] + replacement + text[end:]


# 1) Do not allow the stale GraphQL path to replace a database after the
# current quest catalog has become overlay-dependent. The GraphQL task source
# cannot reproduce tasksAdd/disabled/locale/prestige corrections, so the only
# safe behavior is to keep the existing validated database.
json_path = "TarkovHelper/Services/TarkovDataDatabaseBuilder.JsonApi.cs"
json_text = read(json_path)
json_start = "        catch (Exception staticException) when (!staticDataReady)\n        {"
json_end = "\n        catch\n        {"
json_replacement = '''        catch (Exception staticException) when (!staticDataReady)
        {
            CleanupFile(tempPath);
            throw new InvalidOperationException(
                "현재 퀘스트 목록은 검증된 정적 JSON API와 보정 데이터 경로가 필요합니다. " +
                "보정되지 않은 퀘스트 목록으로 되돌아갈 수 있는 GraphQL 예비 경로는 사용하지 않습니다. " +
                $"JSON: {CompactApiError(staticException.Message)}",
                staticException);
        }'''
json_text = replace_between(json_text, json_start, json_end, json_replacement)
write(json_path, json_text)


# 2) Treat an incomplete or schema-drifted overlay as invalid instead of
# recording static-json+overlay when no meaningful correction can be applied.
overlay_path = "TarkovHelper/Services/TarkovDataDatabaseBuilder.Overlay.cs"
overlay_text = read(overlay_path)
overlay_start = "    private static QuestCatalogOverlayInfo ValidateQuestCatalogOverlay(JsonObject overlay)\n    {"
overlay_end = "\n    private static QuestCatalogOverlayInfo ApplyQuestCatalogOverlay("
overlay_replacement = '''    private static QuestCatalogOverlayInfo ValidateQuestCatalogOverlay(JsonObject overlay)
    {
        if (overlay["$meta"] is not JsonObject meta)
            throw new InvalidDataException("퀘스트 보정 데이터에 $meta가 없습니다.");

        var version = NodeString(meta["version"]);
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("퀘스트 보정 데이터 버전이 없습니다.");

        var prestige = RequireOverlayObject(overlay, "prestige", "prestige");
        var tasks = RequireOverlayObject(overlay, "tasks", "tasks");
        var tasksAdd = RequireOverlayObject(overlay, "tasksAdd", "tasksAdd");
        var locales = RequireOverlayObject(overlay, "locales", "locales");
        var modes = RequireOverlayObject(overlay, "modes", "modes");

        if (modes["regular"] is not JsonObject regularMode)
            throw new InvalidDataException("퀘스트 보정 데이터에 modes.regular 객체가 없습니다.");

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
                localeCorrectionCount += localeTasks.Count;
        }

        var totalCorrections = prestige.Count + tasks.Count + tasksAdd.Count +
                               modeCorrectionCount + localeCorrectionCount;
        if (totalCorrections <= 0)
        {
            throw new InvalidDataException(
                "퀘스트 보정 데이터에 적용 가능한 prestige/tasks/tasksAdd/mode/locale 보정이 없습니다.");
        }

        // regular is the only runtime mode used by Tarkov Helper. Validate it
        // explicitly even when it currently has no task-specific overrides.
        ValidateTaskOverlayContainer(regularMode, "modes.regular");

        return new QuestCatalogOverlayInfo(
            version,
            NodeString(meta["generated"]),
            NodeString(meta["sha256"]));
    }

    private static JsonObject RequireOverlayObject(
        JsonObject overlay,
        string propertyName,
        string label)
    {
        if (overlay[propertyName] is not JsonObject value)
            throw new InvalidDataException($"퀘스트 보정 데이터의 {label} 형식이 잘못되었습니다.");
        return value;
    }

    private static int ValidateTaskOverlayContainer(JsonObject container, string label)
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
overlay_text = replace_between(
    overlay_text,
    overlay_start,
    overlay_end,
    overlay_replacement,
)
write(overlay_path, overlay_text)


# 3) Extend the deterministic HTTP fixture so fail-closed behavior can be
# tested both after a successful overlay application and for a parseable but
# unusable overlay artifact.
fixture_path = "TarkovHelper.DatabaseSmoke/FixtureTarkovApiHandler.cs"
fixture_text = read(fixture_path)
class_marker = "internal sealed class FixtureTarkovApiHandler : HttpMessageHandler\n{\n"
class_insert = '''internal sealed class FixtureTarkovApiHandler : HttpMessageHandler
{
    private readonly bool _failHideoutAfterOverlay;
    private readonly bool _invalidQuestOverlay;

    public FixtureTarkovApiHandler(
        bool failHideoutAfterOverlay = false,
        bool invalidQuestOverlay = false)
    {
        _failHideoutAfterOverlay = failHideoutAfterOverlay;
        _invalidQuestOverlay = invalidQuestOverlay;
    }
'''
if class_marker not in fixture_text:
    raise RuntimeError("Fixture handler class marker not found")
fixture_text = fixture_text.replace(class_marker, class_insert, 1)
fixture_text = fixture_text.replace(
    "    private static HttpResponseMessage HandleStaticJsonRequest(string path)\n",
    "    private HttpResponseMessage HandleStaticJsonRequest(string path)\n",
    1,
)
normalized_marker = "        var normalized = path.Trim('/');\n\n        return normalized switch\n"
normalized_replacement = '''        var normalized = path.Trim('/');

        if (_failHideoutAfterOverlay &&
            string.Equals(normalized, "regular/hideout", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(
                HttpStatusCode.BadRequest,
                new { error = "Injected post-overlay hideout failure" });
        }

        return normalized switch
'''
if normalized_marker not in fixture_text:
    raise RuntimeError("Fixture normalized path marker not found")
fixture_text = fixture_text.replace(normalized_marker, normalized_replacement, 1)
overlay_case_old = '''            "tarkovtracker-org/tarkov-data-overlay/main/dist/overlay.json" =>
                JsonResponse(HttpStatusCode.OK, CreateQuestCatalogOverlay()),
'''
overlay_case_new = '''            "tarkovtracker-org/tarkov-data-overlay/main/dist/overlay.json" =>
                JsonResponse(
                    HttpStatusCode.OK,
                    _invalidQuestOverlay
                        ? CreateInvalidQuestCatalogOverlay()
                        : CreateQuestCatalogOverlay()),
'''
if overlay_case_old not in fixture_text:
    raise RuntimeError("Fixture overlay switch case not found")
fixture_text = fixture_text.replace(overlay_case_old, overlay_case_new, 1)
create_overlay_marker = "    private static object CreateQuestCatalogOverlay()\n    {\n"
invalid_overlay_method = '''    private static object CreateInvalidQuestCatalogOverlay()
    {
        return new Dictionary<string, object>
        {
            ["$meta"] = new
            {
                version = "fixture-invalid"
            }
        };
    }

    private static object CreateQuestCatalogOverlay()
    {
'''
if create_overlay_marker not in fixture_text:
    raise RuntimeError("CreateQuestCatalogOverlay marker not found")
fixture_text = fixture_text.replace(create_overlay_marker, invalid_overlay_method, 1)
write(fixture_path, fixture_text)


# 4) Add regression smoke coverage. Both scenarios must fail without touching
# the active DB and without issuing a GraphQL request.
program_path = "TarkovHelper.DatabaseSmoke/Program.cs"
program_text = read(program_path)
call_marker = "    await RunOutageHandlingSmokeAsync(databasePath);\n\n"
call_replacement = "    await RunOutageHandlingSmokeAsync(databasePath);\n    await RunQuestCatalogFailClosedSmokeAsync(databasePath);\n\n"
if call_marker not in program_text:
    raise RuntimeError("Program fail-closed smoke call marker not found")
program_text = program_text.replace(call_marker, call_replacement, 1)
helper_marker = "\nstatic async Task RunPersistenceWriteQueueSmokeAsync()\n"
helper_method = r'''
static async Task RunQuestCatalogFailClosedSmokeAsync(string seedDatabasePath)
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "TarkovHelperQuestCatalogFailClosed",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    try
    {
        await RunScenarioAsync(
            "post-overlay-static-failure",
            new FixtureTarkovApiHandler(failHideoutAfterOverlay: true));
        await RunScenarioAsync(
            "invalid-overlay",
            new FixtureTarkovApiHandler(invalidQuestOverlay: true));
    }
    finally
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only; validation already completed.
        }
    }

    async Task RunScenarioAsync(string name, FixtureTarkovApiHandler fixtureHandler)
    {
        var scenarioPath = Path.Combine(root, name + ".db");
        File.Copy(seedDatabasePath, scenarioPath, overwrite: true);
        var before = await File.ReadAllBytesAsync(scenarioPath);

        using var httpClient = new HttpClient(fixtureHandler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        var builder = new TarkovDataDatabaseBuilder(
            httpClient,
            progress => Console.WriteLine($"[fail-closed:{name}] {progress.Message}"),
            enrichAmmoSources: false);

        Exception? failure = null;
        try
        {
            await builder.BuildPreferredAsync(scenarioPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            failure = exception;
        }

        if (failure == null)
            throw new InvalidDataException($"Fail-closed scenario unexpectedly succeeded: {name}.");
        if (fixtureHandler.GraphQlRequestCount != 0)
        {
            throw new InvalidDataException(
                $"Fail-closed scenario leaked into GraphQL fallback: {name}, " +
                $"requests={fixtureHandler.GraphQlRequestCount}.");
        }

        var after = await File.ReadAllBytesAsync(scenarioPath);
        if (!before.AsSpan().SequenceEqual(after))
            throw new InvalidDataException($"Fail-closed scenario replaced the existing database: {name}.");
    }
}

static async Task RunPersistenceWriteQueueSmokeAsync()
'''
if helper_marker not in program_text:
    raise RuntimeError("Program helper insertion marker not found")
program_text = program_text.replace(helper_marker, "\n" + helper_method, 1)
program_text = program_text.replace(
    '        $"Deterministic database smoke passed: profile=PVP, transport=static-json, " +',
    '        $"Deterministic database smoke passed: profile=PVP, transport=static-json+overlay, " +',
    1,
)
write(program_path, program_text)

print("PR34 review fixes staged successfully.")
