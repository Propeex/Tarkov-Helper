using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

internal sealed class ObjectiveIdCollisionFixtureHandler : DelegatingHandler
{
    internal const string SharedObjectiveId = "fixture-shared-objective";
    internal const string ScopedSecondObjectiveId =
        SharedObjectiveId + ":task:fixture-quest-second:objective:0";

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
            AddDuplicateObjectiveIds(root);
        else if (string.Equals(path, "regular/tasks_en", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslation(root, "Hand over syringe");
        else if (string.Equals(path, "regular/tasks_ko", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslation(root, "주사기 건네주기");

        ReplaceContent(response, root.ToJsonString());
        return response;
    }

    private static void AddDuplicateObjectiveIds(JsonObject root)
    {
        if (root["data"]?["tasks"] is not JsonObject tasks ||
            tasks["fixture-quest-first"]?["objectives"] is not JsonArray firstObjectives ||
            firstObjectives.Count == 0 ||
            firstObjectives[0] is not JsonObject firstObjective ||
            tasks["fixture-quest-second"]?["objectives"] is not JsonArray secondObjectives)
        {
            throw new InvalidDataException("Objective collision fixture could not locate both quests.");
        }

        firstObjective["id"] = SharedObjectiveId;
        firstObjective["description"] = SharedObjectiveId;

        var secondObjective = firstObjective.DeepClone().AsObject();
        secondObjective["id"] = SharedObjectiveId;
        secondObjective["description"] = SharedObjectiveId;
        secondObjectives.Add(secondObjective);
    }

    private static void AddCollisionTranslation(JsonObject root, string translatedDescription)
    {
        if (root["data"] is not JsonObject translations)
            throw new InvalidDataException("Objective collision fixture translation payload is invalid.");

        translations[SharedObjectiveId] = translatedDescription;
    }

    private static void ReplaceContent(HttpResponseMessage response, string json)
    {
        response.Content.Dispose();
        response.Content = new StringContent(json, Encoding.UTF8, "application/json");
    }
}
