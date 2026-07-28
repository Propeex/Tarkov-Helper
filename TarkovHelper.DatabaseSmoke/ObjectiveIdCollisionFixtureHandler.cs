using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

internal sealed class ObjectiveIdCollisionFixtureHandler : DelegatingHandler
{
    private const string FirstObjectiveId = "fixture-objective-bolts";
    private const string SecondObjectiveId = "fixture-objective-bolts-alt";

    public ObjectiveIdCollisionFixtureHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || request.Method != HttpMethod.Get || response.Content == null)
            return response;

        var path = request.RequestUri?.AbsolutePath.Trim('/') ?? string.Empty;
        if (!path.StartsWith("regular/tasks", StringComparison.OrdinalIgnoreCase))
            return response;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            ReplaceContent(response, json);
            return response;
        }

        if (string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase))
            AddCollisionObjectives(root);
        else if (string.Equals(path, "regular/tasks_en", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslations(root, "Hand over syringe");
        else if (string.Equals(path, "regular/tasks_ko", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslations(root, "주사기 건네주기");

        ReplaceContent(response, root.ToJsonString());
        return response;
    }

    private static void AddCollisionObjectives(JsonObject root)
    {
        if (root["data"]?["tasks"]?["fixture-quest-first"]?["objectives"] is not JsonArray objectives ||
            objectives.Count == 0 || objectives[0] is not JsonObject first)
        {
            throw new InvalidDataException("Objective collision fixture could not locate the first quest objective.");
        }

        first["id"] = FirstObjectiveId;
        first["description"] = FirstObjectiveId;

        var second = first.DeepClone().AsObject();
        second["id"] = SecondObjectiveId;
        second["description"] = SecondObjectiveId;
        objectives.Add(second);
    }

    private static void AddCollisionTranslations(JsonObject root, string translatedDescription)
    {
        if (root["data"] is not JsonObject translations)
            throw new InvalidDataException("Objective collision fixture translation payload is invalid.");

        translations[FirstObjectiveId] = translatedDescription;
        translations[SecondObjectiveId] = translatedDescription;
    }

    private static void ReplaceContent(HttpResponseMessage response, string json)
    {
        response.Content.Dispose();
        response.Content = new StringContent(json, Encoding.UTF8, "application/json");
    }
}
