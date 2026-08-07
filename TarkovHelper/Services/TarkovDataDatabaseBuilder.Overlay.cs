using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace TarkovHelper.Services;

internal sealed partial class TarkovDataDatabaseBuilder
{
    private const string QuestCatalogOverlayUrl =
        "https://raw.githubusercontent.com/tarkovtracker-org/tarkov-data-overlay/main/dist/overlay.json";
    private const int MaxQuestOverlayAttempts = 3;
    private static readonly TimeSpan QuestOverlayTimeout = TimeSpan.FromSeconds(20);

    private sealed record QuestCatalogOverlayInfo(
        string Version,
        string? Generated,
        string? Sha256);

    private sealed class QuestCatalogOverlayException : Exception
    {
        public QuestCatalogOverlayException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    private async Task<(JsonObject Overlay, QuestCatalogOverlayInfo Info)> DownloadQuestCatalogOverlayAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxQuestOverlayAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(QuestOverlayTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, QuestCatalogOverlayUrl);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("TarkovHelper-JH/quest-catalog-sync");

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"퀘스트 보정 데이터 요청 실패 ({(int)response.StatusCode} {response.ReasonPhrase})",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
                var node = await JsonNode.ParseAsync(stream, cancellationToken: timeoutSource.Token);
                if (node is not JsonObject overlay)
                    throw new InvalidDataException("퀘스트 보정 데이터가 JSON 객체가 아닙니다.");

                var info = ValidateQuestCatalogOverlay(overlay);
                Log.Info(
                    $"Quest catalog overlay loaded: version={info.Version}, generated={info.Generated ?? "unknown"}");
                return (overlay, info);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"퀘스트 보정 데이터 요청이 {QuestOverlayTimeout.TotalSeconds:F0}초 안에 완료되지 않았습니다.",
                    exception);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
            {
                lastException = exception;
            }

            if (attempt < MaxQuestOverlayAttempts)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
        }

        throw new QuestCatalogOverlayException(
            "현재 퀘스트 목록을 검증하는 보정 데이터를 가져오지 못했습니다. " +
            "오래된/누락된 퀘스트 목록으로 DB를 교체하지 않기 위해 업데이트를 중단합니다.",
            lastException);
    }

    private static QuestCatalogOverlayInfo ValidateQuestCatalogOverlay(JsonObject overlay)
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

    private static QuestCatalogOverlayInfo ApplyQuestCatalogOverlay(
        JsonObject englishDocument,
        JsonObject koreanDocument,
        JsonObject overlay)
    {
        var info = ValidateQuestCatalogOverlay(overlay);

        ApplyPrestigeOverlay(englishDocument, overlay);
        ApplyPrestigeOverlay(koreanDocument, overlay);

        var englishStats = ApplyTaskOverlay(englishDocument, overlay, "en");
        var koreanStats = ApplyTaskOverlay(koreanDocument, overlay, "ko");

        if (englishStats.FinalCount != koreanStats.FinalCount)
        {
            throw new InvalidDataException(
                $"퀘스트 보정 후 영문/한국어 목록 수가 다릅니다: " +
                $"en={englishStats.FinalCount}, ko={koreanStats.FinalCount}.");
        }

        Log.Info(
            $"Quest catalog overlay applied: version={info.Version}, " +
            $"patched={englishStats.Patched}, added={englishStats.Added}, " +
            $"disabled={englishStats.Disabled}, final={englishStats.FinalCount}");
        return info;
    }

    private sealed record QuestOverlayStats(int Patched, int Added, int Disabled, int FinalCount);

    private static QuestOverlayStats ApplyTaskOverlay(
        JsonObject document,
        JsonObject overlay,
        string locale)
    {
        var data = RequiredObject(document, "data", "퀘스트");
        var tasks = RequiredObject(data, "tasks", "퀘스트");

        var sharedPatches = overlay["tasks"] as JsonObject;
        var regularMode = overlay["modes"]?["regular"] as JsonObject;
        var modePatches = regularMode?["tasks"] as JsonObject;
        var patched = 0;
        var disabled = 0;

        foreach (var id in tasks.Select(pair => pair.Key).ToArray())
        {
            if (tasks[id] is not JsonObject task)
                continue;

            var changed = false;
            if (sharedPatches?[id] is JsonObject sharedPatch)
            {
                ApplyTaskPatch(task, sharedPatch);
                changed = true;
            }
            if (modePatches?[id] is JsonObject modePatch)
            {
                ApplyTaskPatch(task, modePatch);
                changed = true;
            }

            if (changed)
                patched++;

            if (GetBool(task, "disabled") == true)
            {
                tasks.Remove(id);
                disabled++;
            }
        }

        var additions = MergeTaskAdditions(
            overlay["tasksAdd"] as JsonObject,
            regularMode?["tasksAdd"] as JsonObject);
        var added = 0;
        foreach (var (id, additionNode) in additions)
        {
            if (additionNode is not JsonObject addition)
                continue;

            if (GetBool(addition, "disabled") == true)
                continue;

            if (tasks[id] is JsonObject existing)
            {
                ApplyTaskPatch(existing, addition);
                continue;
            }

            var clone = addition.DeepClone() as JsonObject
                ?? throw new InvalidDataException($"추가 퀘스트 {id}를 복제하지 못했습니다.");
            if (string.IsNullOrWhiteSpace(GetString(clone, "id")))
                clone["id"] = id;
            tasks[id] = clone;
            added++;
        }

        ApplyTaskLocaleOverlay(tasks, overlay, locale);
        return new QuestOverlayStats(patched, added, disabled, tasks.Count);
    }

    private static JsonObject MergeTaskAdditions(JsonObject? shared, JsonObject? modeSpecific)
    {
        var result = new JsonObject();
        if (shared != null)
        {
            foreach (var (id, value) in shared)
                result[id] = value?.DeepClone();
        }

        if (modeSpecific != null)
        {
            foreach (var (id, value) in modeSpecific)
            {
                if (value is not JsonObject modeAddition)
                {
                    result[id] = value?.DeepClone();
                    continue;
                }

                if (result[id] is JsonObject existing)
                    DeepMergeObject(existing, modeAddition);
                else
                    result[id] = modeAddition.DeepClone();
            }
        }

        return result;
    }

    private static void ApplyTaskLocaleOverlay(JsonObject tasks, JsonObject overlay, string locale)
    {
        if (overlay["locales"]?[locale]?["tasks"] is not JsonObject localeTasks)
            return;

        foreach (var (id, patchNode) in localeTasks)
        {
            if (tasks[id] is JsonObject task && patchNode is JsonObject patch)
                ApplyTaskPatch(task, patch);
        }
    }

    private static void ApplyTaskPatch(JsonObject task, JsonObject patch)
    {
        foreach (var (key, patchValue) in patch)
        {
            if (string.Equals(key, "objectives", StringComparison.OrdinalIgnoreCase) &&
                patchValue is JsonObject objectivePatches)
            {
                ApplyObjectivePatches(task, objectivePatches);
                continue;
            }

            if (string.Equals(key, "objectivesAdd", StringComparison.OrdinalIgnoreCase) &&
                patchValue is JsonArray objectiveAdditions)
            {
                var objectives = task["objectives"] as JsonArray;
                if (objectives == null)
                {
                    objectives = new JsonArray();
                    task["objectives"] = objectives;
                }

                foreach (var addition in objectiveAdditions)
                {
                    if (addition is JsonObject additionObject)
                    {
                        var additionId = GetString(additionObject, "id");
                        if (!string.IsNullOrWhiteSpace(additionId) &&
                            objectives.OfType<JsonObject>().Any(value =>
                                string.Equals(GetString(value, "id"), additionId, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                    }
                    objectives.Add(addition?.DeepClone());
                }
                continue;
            }

            if (patchValue is JsonObject patchObject && task[key] is JsonObject targetObject)
            {
                DeepMergeObject(targetObject, patchObject);
                continue;
            }

            task[key] = patchValue?.DeepClone();
        }
    }

    private static void ApplyObjectivePatches(JsonObject task, JsonObject patches)
    {
        if (task["objectives"] is not JsonArray objectives)
            return;

        var byId = objectives
            .OfType<JsonObject>()
            .Where(value => !string.IsNullOrWhiteSpace(GetString(value, "id")))
            .GroupBy(value => GetString(value, "id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (objectiveId, patchNode) in patches)
        {
            if (patchNode is not JsonObject patch || !byId.TryGetValue(objectiveId, out var objective))
                continue;
            DeepMergeObject(objective, patch);
        }
    }

    private static void DeepMergeObject(JsonObject target, JsonObject patch)
    {
        foreach (var (key, patchValue) in patch)
        {
            if (patchValue is JsonObject patchObject && target[key] is JsonObject targetObject)
            {
                DeepMergeObject(targetObject, patchObject);
                continue;
            }
            target[key] = patchValue?.DeepClone();
        }
    }

    private static void ApplyPrestigeOverlay(JsonObject document, JsonObject overlay)
    {
        if (overlay["prestige"] is not JsonObject prestigePatches)
            return;

        var data = RequiredObject(document, "data", "퀘스트");
        var prestigeNode = data["prestige"];

        if (prestigeNode is JsonObject prestigeObject)
        {
            foreach (var (id, patchNode) in prestigePatches)
            {
                if (patchNode is not JsonObject patch)
                    continue;

                if (prestigeObject[id] is JsonObject existing)
                {
                    DeepMergeObject(existing, patch);
                }
                else
                {
                    var added = patch.DeepClone() as JsonObject ?? new JsonObject();
                    added["id"] ??= id;
                    prestigeObject[id] = added;
                }
            }
            return;
        }

        if (prestigeNode is JsonArray prestigeArray)
        {
            var byId = prestigeArray
                .OfType<JsonObject>()
                .Where(value => !string.IsNullOrWhiteSpace(GetString(value, "id")))
                .GroupBy(value => GetString(value, "id")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var (id, patchNode) in prestigePatches)
            {
                if (patchNode is not JsonObject patch)
                    continue;

                if (byId.TryGetValue(id, out var existing))
                {
                    DeepMergeObject(existing, patch);
                }
                else
                {
                    var added = patch.DeepClone() as JsonObject ?? new JsonObject();
                    added["id"] ??= id;
                    prestigeArray.Add(added);
                }
            }
            return;
        }

        var created = new JsonArray();
        foreach (var (id, patchNode) in prestigePatches)
        {
            if (patchNode is not JsonObject patch)
                continue;
            var added = patch.DeepClone() as JsonObject ?? new JsonObject();
            added["id"] ??= id;
            created.Add(added);
        }
        data["prestige"] = created;
    }

    private static JsonObject? ResolvePrestigeObject(JsonNode? prestigeLookup, string id)
    {
        if (prestigeLookup is JsonObject objectLookup)
            return objectLookup[id] as JsonObject;

        if (prestigeLookup is JsonArray arrayLookup)
        {
            return arrayLookup
                .OfType<JsonObject>()
                .FirstOrDefault(value =>
                    string.Equals(GetString(value, "id"), id, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
