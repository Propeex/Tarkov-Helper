using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

internal sealed class ObjectiveIdCollisionFixtureHandler : DelegatingHandler
{
    internal const string SharedObjectiveId = "fixture-shared-objective";
    internal const string ScopedSecondObjectiveId =
        SharedObjectiveId + ":task:fixture-quest-second:objective:0";
    internal const string FirstHandOverObjectiveId = "fixture-bolts-handover-first";
    internal const string SecondHandOverObjectiveId = "fixture-bolts-handover-second";
    internal const string DogtagObjectiveId = "fixture-dogtag-objective";
    internal const string DogtagStandardItemId = "fixture-dogtag-usec-standard";
    internal const string DogtagPrestigeItemId = "fixture-dogtag-usec-prestige";

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
        if (!path.StartsWith("regular/tasks", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("regular/items", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            ReplaceContent(response, json);
            return response;
        }

        if (string.Equals(path, "regular/tasks", StringComparison.OrdinalIgnoreCase))
            AddCompatibilityCases(root);
        else if (string.Equals(path, "regular/tasks_en", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslation(root, "Hand over syringe", false);
        else if (string.Equals(path, "regular/tasks_ko", StringComparison.OrdinalIgnoreCase))
            AddCollisionTranslation(root, "주사기 건네주기", true);
        else if (string.Equals(path, "regular/items", StringComparison.OrdinalIgnoreCase))
            AddDogtagItems(root);
        else if (string.Equals(path, "regular/items_en", StringComparison.OrdinalIgnoreCase))
            AddDogtagItemTranslations(root, false);
        else if (string.Equals(path, "regular/items_ko", StringComparison.OrdinalIgnoreCase))
            AddDogtagItemTranslations(root, true);

        ReplaceContent(response, root.ToJsonString());
        return response;
    }

    private static void AddCompatibilityCases(JsonObject root)
    {
        if (root["data"]?["tasks"] is not JsonObject tasks ||
            tasks["fixture-quest-first"] is not JsonObject firstTask ||
            firstTask["objectives"] is not JsonArray firstObjectives ||
            firstObjectives.Count == 0 ||
            firstObjectives[0] is not JsonObject firstObjective ||
            tasks["fixture-quest-second"] is not JsonObject secondTask ||
            secondTask["objectives"] is not JsonArray secondObjectives)
        {
            throw new InvalidDataException("Objective compatibility fixture could not locate both quests.");
        }

        firstTask["factionName"] = "Any Target";
        secondTask["factionName"] = "Any";

        // Keep paired acquisition objectives for display and objective-ID collision
        // coverage. They must not be written into QuestRequiredItems.
        firstObjective["id"] = SharedObjectiveId;
        firstObjective["type"] = "findItem";
        firstObjective["description"] = SharedObjectiveId;

        var secondObjective = firstObjective.DeepClone().AsObject();
        secondObjective["id"] = SharedObjectiveId;
        secondObjective["description"] = SharedObjectiveId;
        secondObjectives.Add(secondObjective);

        // Add the actual submission side of each pair. Exactly these rows should
        // become consumable quest requirements.
        var firstHandOverObjective = firstObjective.DeepClone().AsObject();
        firstHandOverObjective["id"] = FirstHandOverObjectiveId;
        firstHandOverObjective["type"] = "giveItem";
        firstHandOverObjective["description"] = FirstHandOverObjectiveId + " Description";
        firstObjectives.Add(firstHandOverObjective);

        var secondHandOverObjective = firstHandOverObjective.DeepClone().AsObject();
        secondHandOverObjective["id"] = SecondHandOverObjectiveId;
        secondHandOverObjective["description"] = SecondHandOverObjectiveId + " Description";
        secondObjectives.Add(secondHandOverObjective);

        secondObjectives.Add(new JsonObject
        {
            ["id"] = DogtagObjectiveId,
            ["type"] = "giveItem",
            ["description"] = DogtagObjectiveId + " Description",
            ["optional"] = false,
            ["maps"] = new JsonArray(),
            ["items"] = new JsonArray(DogtagStandardItemId, DogtagPrestigeItemId),
            ["count"] = 7,
            ["foundInRaid"] = false,
            ["dogTagLevel"] = 50
        });

        firstObjectives.Add(new JsonObject
        {
            ["id"] = "fixture-sell-objective",
            ["type"] = "sellItem",
            ["description"] = "fixture-sell-objective Description",
            ["optional"] = false,
            ["maps"] = new JsonArray(),
            ["items"] = new JsonArray("fixture-item-bolts", "fixture-item-wire"),
            ["count"] = 1,
            ["foundInRaid"] = false,
            ["dogTagLevel"] = null
        });
    }

    private static void AddDogtagItems(JsonObject root)
    {
        if (root["data"]?["items"] is not JsonObject items)
            throw new InvalidDataException("Dogtag fixture item payload is invalid.");

        items[DogtagStandardItemId] = CreateDogtagItem(
            DogtagStandardItemId,
            "dogtag-usec",
            "https://example.invalid/dogtag-usec-standard.png");
        items[DogtagPrestigeItemId] = CreateDogtagItem(
            DogtagPrestigeItemId,
            "dogtag-usec-prestige",
            "https://example.invalid/dogtag-usec-prestige.png");
    }

    private static JsonObject CreateDogtagItem(string id, string normalizedName, string iconLink)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = id + " Name",
            ["normalizedName"] = normalizedName,
            ["shortName"] = id + " ShortName",
            ["description"] = id + " Description",
            ["iconLink"] = iconLink,
            ["wikiLink"] = "https://example.invalid/" + normalizedName,
            ["categories"] = new JsonArray("fixture-category-barter")
        };
    }

    private static void AddCollisionTranslation(
        JsonObject root,
        string translatedDescription,
        bool korean)
    {
        if (root["data"] is not JsonObject translations)
            throw new InvalidDataException("Objective collision fixture translation payload is invalid.");

        translations[SharedObjectiveId] = translatedDescription;
        translations[FirstHandOverObjectiveId + " Description"] = korean
            ? "볼트를 건네주기"
            : "Hand over bolts";
        translations[SecondHandOverObjectiveId + " Description"] = korean
            ? "볼트를 다시 건네주기"
            : "Hand over bolts again";
        translations["fixture-sell-objective Description"] = korean
            ? "판매 목록 테스트"
            : "Sell catalogue fixture";
        translations[DogtagObjectiveId + " Description"] = korean
            ? "레벨 50 이상 USEC 인식표를 건네주기"
            : "Hand over level 50+ USEC dogtags";
    }

    private static void AddDogtagItemTranslations(JsonObject root, bool korean)
    {
        if (root["data"] is not JsonObject translations)
            throw new InvalidDataException("Dogtag fixture translation payload is invalid.");

        AddDogtagItemTranslation(translations, DogtagStandardItemId, korean, false);
        AddDogtagItemTranslation(translations, DogtagPrestigeItemId, korean, true);
    }

    private static void AddDogtagItemTranslation(
        JsonObject translations,
        string id,
        bool korean,
        bool prestige)
    {
        var name = korean
            ? prestige ? "USEC 인식표 (프레스티지)" : "USEC 인식표"
            : prestige ? "Dogtag USEC (Prestige)" : "Dogtag USEC";

        translations[id + " Name"] = name;
        translations[id + " ShortName"] = korean ? "USEC 인식표" : "USEC dogtag";
        translations[id + " Description"] = korean
            ? "USEC PMC 인식표"
            : "USEC PMC dogtag";
    }

    private static void ReplaceContent(HttpResponseMessage response, string json)
    {
        response.Content.Dispose();
        response.Content = new StringContent(json, Encoding.UTF8, "application/json");
    }
}
