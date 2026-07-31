using System.Text.Json.Nodes;

namespace TarkovHelper.Services;

internal sealed partial class TarkovDataDatabaseBuilder
{
    private static void EnrichAmmoSourcesFromStaticTaskRewards(
        IEnumerable<ApiItem> items,
        JsonObject taskRoot,
        IReadOnlyDictionary<string, ApiNamedEntity> traderLookup,
        IReadOnlyDictionary<string, ApiNamedEntity> stationLookup)
    {
        var itemLookup = UniqueById(items);
        var data = RequiredObject(taskRoot, "data", "퀘스트");
        var taskObjects = RequiredObject(data, "tasks", "퀘스트");
        var enrichedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (taskKey, taskNode) in taskObjects)
        {
            if (taskNode is not JsonObject taskObject)
                continue;

            var taskId = GetString(taskObject, "id") ?? taskKey;
            var fallbackTraderId = ReferenceId(taskObject["trader"]);

            foreach (var rewardName in new[] { "startRewards", "finishRewards" })
            {
                if (taskObject[rewardName] is not JsonObject rewards)
                    continue;

                AddTaskRewardSources(
                    rewards["items"],
                    itemLookup,
                    enrichedItems,
                    "task reward");

                if (rewards["offerUnlock"] is JsonArray offers)
                {
                    foreach (var offerNode in offers.OfType<JsonObject>())
                    {
                        var itemId = ReferenceId(offerNode["item"]);
                        var traderId = ReferenceId(offerNode["trader"]) ?? fallbackTraderId;
                        var traderName = ResolveSourceName(traderId, traderLookup, "trader");
                        AddAmmoSource(
                            itemId,
                            itemLookup,
                            enrichedItems,
                            $"{traderName} trader offer");
                    }
                }

                if (rewards["craftUnlock"] is JsonArray crafts)
                {
                    foreach (var craftNode in crafts.OfType<JsonObject>())
                    {
                        var itemId = ReferenceId(craftNode["item"]);
                        var stationId = ReferenceId(craftNode["station"]);
                        var stationName = ResolveSourceName(stationId, stationLookup, "hideout");
                        AddAmmoSource(
                            itemId,
                            itemLookup,
                            enrichedItems,
                            $"{stationName} craft");
                    }
                }
            }
        }

        Log.Info($"Ammo acquisition sources enriched from static task rewards: {enrichedItems.Count}");
    }

    private static void AddTaskRewardSources(
        JsonNode? node,
        IReadOnlyDictionary<string, ApiItem> itemLookup,
        ISet<string> enrichedItems,
        string source)
    {
        if (node is not JsonArray rewards)
            return;

        foreach (var rewardNode in rewards)
        {
            var itemId = rewardNode is JsonObject rewardObject
                ? ReferenceId(rewardObject["item"])
                : ReferenceId(rewardNode);
            AddAmmoSource(itemId, itemLookup, enrichedItems, source);
        }
    }

    private static void AddAmmoSource(
        string? itemId,
        IReadOnlyDictionary<string, ApiItem> itemLookup,
        ISet<string> enrichedItems,
        string source)
    {
        if (string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(source) ||
            !itemLookup.TryGetValue(itemId, out var item) ||
            item.Properties == null)
        {
            return;
        }

        var sources = SplitAcquisitionSources(item.Properties.AcquisitionSource);
        if (sources.Add(source.Trim()))
        {
            item.Properties.AcquisitionSource = string.Join(" · ", sources);
            enrichedItems.Add(itemId);
        }
    }

    private static HashSet<string> SplitAcquisitionSources(string? source)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(source))
            return result;

        foreach (var value in source.Split(
                     '·',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.Equals(value, "raid/other", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, "레이드 획득/기타", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string ResolveSourceName(
        string? id,
        IReadOnlyDictionary<string, ApiNamedEntity> lookup,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(id) && lookup.TryGetValue(id, out var value))
        {
            var name = value.Name ?? value.NormalizedName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return fallback;
    }
}
