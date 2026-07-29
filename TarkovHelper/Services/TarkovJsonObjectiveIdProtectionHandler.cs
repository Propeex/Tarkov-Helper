using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TarkovHelper.Services;

/// <summary>
/// Protects quest objective identifiers while the static tarkov.dev localization
/// files are applied. Objective descriptions use their objective ID as the
/// translation key, so translating every matching JSON string would otherwise
/// replace both the description and the ID with the localized sentence.
///
/// The static data can also reuse an objective ID in more than one quest even
/// though the local QuestObjectives table requires IDs to be globally unique.
/// Repeated IDs therefore receive a deterministic quest-scoped suffix before
/// localization is applied. The first occurrence keeps its original ID so any
/// existing hand-maintained objective data can still be reused where possible.
///
/// This handler also normalizes API compatibility differences before the data is
/// written. Neutral quest factions such as "Any Target" are stored without a
/// faction restriction, and sell-item catalogue objectives do not become player
/// inventory requirements.
///
/// Endpoint outages are classified before the builder's generic retry loops.
/// DNS failures, HTTP 503 responses, and requests that cannot even receive
/// response headers within a bounded interval should move immediately to the
/// fallback path or return a clear error instead of leaving the UI at 1%.
/// </summary>
internal sealed class TarkovJsonObjectiveIdProtectionHandler : DelegatingHandler
{
    private const string ProtectedPrefix = "__tarkov_helper_objective_id__:";
    private static readonly TimeSpan ResponseHeaderTimeout = TimeSpan.FromSeconds(25);
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _protectedIds = new(StringComparer.Ordinal);

    public TarkovJsonObjectiveIdProtectionHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "tarkov.dev";
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ResponseHeaderTimeout);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, timeoutSource.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TarkovApiUnavailableException(
                $"{host} 서버가 {ResponseHeaderTimeout.TotalSeconds:F0}초 안에 응답하지 않았습니다. 잠시 후 다시 시도하십시오.",
                exception);
        }
        catch (HttpRequestException exception) when (IsDnsFailure(exception))
        {
            throw new TarkovApiUnavailableException(
                $"{host} 주소를 찾지 못했습니다. 인터넷 또는 DNS 연결을 확인한 뒤 다시 시도하십시오.",
                exception);
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable && IsTarkovHost(host))
        {
            response.Dispose();
            throw new TarkovApiUnavailableException(
                $"{host} 서버가 현재 점검 또는 과부하 상태입니다(503). 잠시 후 다시 시도하십시오.");
        }

        if (!response.IsSuccessStatusCode || response.Content == null)
            return response;

        var path = request.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
        var isStaticTaskDocument = request.Method == HttpMethod.Get &&
            (string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(path, "regular/tasks_en", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(path, "regular/tasks_ko", StringComparison.OrdinalIgnoreCase));
        var isGraphQlResponse = request.Method == HttpMethod.Post &&
            string.Equals(path, "graphql", StringComparison.OrdinalIgnoreCase);

        if (!isStaticTaskDocument && !isGraphQlResponse)
            return response;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            ReplaceContent(response, json);
            return response;
        }

        if (root == null)
        {
            ReplaceContent(response, json);
            return response;
        }

        if (isGraphQlResponse)
        {
            NormalizeGraphQlTasks(root);
        }
        else if (string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase))
        {
            ProtectObjectiveIds(root);
        }
        else
        {
            AddRestorationTranslations(root);
        }

        ReplaceContent(response, root.ToJsonString());
        return response;
    }

    private static bool IsTarkovHost(string host)
    {
        return string.Equals(host, "tarkov.dev", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".tarkov.dev", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDnsFailure(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is not SocketException socketException)
                continue;

            return socketException.SocketErrorCode is
                SocketError.HostNotFound or
                SocketError.NoData or
                SocketError.TryAgain;
        }

        return false;
    }

    private void ProtectObjectiveIds(JsonObject root)
    {
        lock (_sync)
        {
            _protectedIds.Clear();

            if (root["data"]?["tasks"] is not JsonObject tasks)
                return;

            // QuestObjectives.Id is globally unique in SQLite, so the used-ID set
            // must span every quest rather than being reset for each individual quest.
            var usedCanonicalIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (taskKey, taskNode) in tasks)
            {
                if (taskNode is not JsonObject task)
                    continue;

                NormalizeQuestCompatibility(task);

                if (task["objectives"] is not JsonArray objectives)
                    continue;

                var taskId = ReadString(task["id"]) ?? taskKey;

                for (var index = 0; index < objectives.Count; index++)
                {
                    if (objectives[index] is not JsonObject objective)
                        continue;

                    var originalId = ReadString(objective["id"]);
                    var baseId = string.IsNullOrWhiteSpace(originalId)
                        ? $"{taskId}:objective:{index}"
                        : originalId;
                    var canonicalId = ReserveCanonicalId(
                        baseId,
                        taskId,
                        index,
                        usedCanonicalIds);

                    var protectedId = ProtectedPrefix + canonicalId;
                    _protectedIds[protectedId] = canonicalId;
                    objective["id"] = protectedId;
                }
            }
        }
    }

    private static void NormalizeGraphQlTasks(JsonObject root)
    {
        if (root["data"]?["tasks"] is not JsonArray tasks)
            return;

        foreach (var taskNode in tasks)
        {
            if (taskNode is JsonObject task)
                NormalizeQuestCompatibility(task);
        }
    }

    private static void NormalizeQuestCompatibility(JsonObject task)
    {
        if (IsNeutralFaction(ReadString(task["factionName"])))
            task["factionName"] = null;

        if (task["objectives"] is not JsonArray objectives)
            return;

        foreach (var objectiveNode in objectives)
        {
            if (objectiveNode is not JsonObject objective)
                continue;

            // tarkov.dev sellItem objectives expose the trader's entire accepted
            // catalogue. Those rows describe what may be sold, not items the player
            // must collect for quest completion.
            if (string.Equals(ReadString(objective["type"]), "sellItem", StringComparison.OrdinalIgnoreCase))
                objective["items"] = new JsonArray();
        }
    }

    private static bool IsNeutralFaction(string? faction)
    {
        if (string.IsNullOrWhiteSpace(faction))
            return true;

        return faction.Trim().ToLowerInvariant() is
            "any" or
            "any target" or
            "all" or
            "both" or
            "pmc";
    }

    private static string ReserveCanonicalId(
        string baseId,
        string taskId,
        int objectiveIndex,
        ISet<string> usedIds)
    {
        if (usedIds.Add(baseId))
            return baseId;

        var scopedBase = $"{baseId}:task:{taskId}:objective:{objectiveIndex}";
        var candidate = scopedBase;
        var duplicateIndex = 2;

        while (!usedIds.Add(candidate))
        {
            candidate = $"{scopedBase}:duplicate:{duplicateIndex}";
            duplicateIndex++;
        }

        return candidate;
    }

    private void AddRestorationTranslations(JsonObject root)
    {
        lock (_sync)
        {
            if (root["data"] is not JsonObject translations)
                return;

            foreach (var (protectedId, canonicalId) in _protectedIds)
                translations[protectedId] = canonicalId;
        }
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static void ReplaceContent(HttpResponseMessage response, string json)
    {
        response.Content.Dispose();
        response.Content = new StringContent(json, Encoding.UTF8, "application/json");
    }
}

internal sealed class TarkovApiUnavailableException : Exception
{
    public TarkovApiUnavailableException(string message)
        : base(message)
    {
    }

    public TarkovApiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
