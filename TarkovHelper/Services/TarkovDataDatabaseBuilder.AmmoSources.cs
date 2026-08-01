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

                if (rewards["offerUnlock"] is JsonArray offers)
                {
                    foreach (var offerNode in offers.OfType<JsonObject>())
                    {
                        var itemId = ReferenceId(offerNode["item"]);
                        var traderId = ReferenceId(offerNode["trader"]) ?? fallbackTraderId;
                        var traderName = ResolveSourceName(traderId, traderLookup, "trader");
                        var level = GetInt(offerNode, "level");
                        var source = $"trader:{traderName}";
                        if (level.HasValue)
                            source += $":level:{Math.Max(1, level.Value)}";
                        AddAmmoSource(itemId, itemLookup, enrichedItems, source);
                    }
                }

                if (rewards["craftUnlock"] is JsonArray crafts)
                {
                    foreach (var craftNode in crafts.OfType<JsonObject>())
                    {
                        var itemId = ReferenceId(craftNode["item"]);
                        var stationId = ReferenceId(craftNode["station"]);
                        var stationName = ResolveSourceName(stationId, stationLookup, "hideout");
                        var level = GetInt(craftNode, "level");
                        var source = $"craft:{stationName}";
                        if (level.HasValue)
                            source += $":level:{Math.Max(1, level.Value)}";
                        AddAmmoSource(itemId, itemLookup, enrichedItems, source);
                    }
                }
            }
        }

        Log.Info($"Ammo acquisition sources enriched from static unlocks: {enrichedItems.Count}");
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
            if (value.StartsWith("trader:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("craft:", StringComparison.OrdinalIgnoreCase))
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
