using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace TarkovHelper.Services;

/// <summary>
/// Protects quest objective identifiers while the static tarkov.dev localization
/// files are applied. Objective descriptions use their objective ID as the
/// translation key, so translating every matching JSON string would otherwise
/// replace both the description and the ID with the localized sentence.
/// </summary>
internal sealed class TarkovJsonObjectiveIdProtectionHandler : DelegatingHandler
{
    private const string ProtectedPrefix = "__tarkov_helper_objective_id__:";
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
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode ||
            request.Method != HttpMethod.Get ||
            response.Content == null)
        {
            return response;
        }

        var path = request.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
        if (!string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "regular/tasks_en", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "regular/tasks_ko", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

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

        if (string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase))
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

    private void ProtectObjectiveIds(JsonObject root)
    {
        lock (_sync)
        {
            _protectedIds.Clear();

            if (root["data"]?["tasks"] is not JsonObject tasks)
                return;

            foreach (var (taskKey, taskNode) in tasks)
            {
                if (taskNode is not JsonObject task || task["objectives"] is not JsonArray objectives)
                    continue;

                var taskId = ReadString(task["id"]) ?? taskKey;
                var seenIds = new HashSet<string>(StringComparer.Ordinal);

                for (var index = 0; index < objectives.Count; index++)
                {
                    if (objectives[index] is not JsonObject objective)
                        continue;

                    var originalId = ReadString(objective["id"]);
                    var canonicalId = string.IsNullOrWhiteSpace(originalId)
                        ? $"{taskId}:objective:{index}"
                        : originalId;

                    if (!seenIds.Add(canonicalId))
                        canonicalId = $"{canonicalId}:duplicate:{index}";

                    var protectedId = ProtectedPrefix + canonicalId;
                    _protectedIds[protectedId] = canonicalId;
                    objective["id"] = protectedId;
                }
            }
        }
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
