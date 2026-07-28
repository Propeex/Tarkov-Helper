using System.Net;
using System.Text;
using System.Text.Json;

internal sealed class FixtureTarkovApiHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var query = document.RootElement.GetProperty("query").GetString() ?? string.Empty;
        var korean = query.Contains("lang: ko", StringComparison.Ordinal);

        object data;
        if (query.Contains("items(", StringComparison.Ordinal))
        {
            data = new { items = CreateItems(korean) };
        }
        else if (query.Contains("tasks(", StringComparison.Ordinal))
        {
            data = new { tasks = CreateTasks(korean) };
        }
        else if (query.Contains("hideoutStations(", StringComparison.Ordinal))
        {
            data = new { hideoutStations = CreateHideout(korean) };
        }
        else
        {
            return JsonResponse(
                HttpStatusCode.BadRequest,
                new { errors = new[] { new { message = "Unknown fixture query" } } });
        }

        return JsonResponse(HttpStatusCode.OK, new { data });
    }

    private static object[] CreateItems(bool korean)
    {
        return
        [
            new
            {
                id = "fixture-item-bolts",
                name = korean ? "볼트" : "Bolts",
                normalizedName = "bolts",
                shortName = korean ? "볼트" : "Bolts",
                description = korean ? "고정용 볼트" : "Fastening bolts",
                iconLink = "https://example.invalid/bolts.png",
                wikiLink = "https://example.invalid/bolts",
                category = new { name = "Barter item", normalizedName = "barter-item" },
                categories = new[] { new { name = "Barter item", normalizedName = "barter-item" } }
            },
            new
            {
                id = "fixture-item-wire",
                name = korean ? "전선" : "Wire",
                normalizedName = "wire",
                shortName = korean ? "전선" : "Wire",
                description = korean ? "전기 배선용 전선" : "Electrical wire",
                iconLink = "https://example.invalid/wire.png",
                wikiLink = "https://example.invalid/wire",
                category = new { name = "Barter item", normalizedName = "barter-item" },
                categories = new[] { new { name = "Barter item", normalizedName = "barter-item" } }
            }
        ];
    }

    private static object[] CreateTasks(bool korean)
    {
        var objective = new Dictionary<string, object?>
        {
            ["__typename"] = "TaskObjectiveItem",
            ["id"] = "fixture-objective-bolts",
            ["type"] = "item",
            ["description"] = korean ? "볼트를 획득하십시오" : "Obtain bolts",
            ["optional"] = false,
            ["maps"] = Array.Empty<object>(),
            ["items"] = new[]
            {
                new
                {
                    id = "fixture-item-bolts",
                    name = korean ? "볼트" : "Bolts",
                    normalizedName = "bolts",
                    iconLink = "https://example.invalid/bolts.png"
                }
            },
            ["count"] = 2,
            ["foundInRaid"] = true,
            ["dogTagLevel"] = null
        };

        return
        [
            new
            {
                id = "fixture-quest-first",
                name = korean ? "첫 번째 퀘스트" : "First Fixture Quest",
                normalizedName = "first-fixture-quest",
                wikiLink = "https://example.invalid/quest-first",
                minPlayerLevel = 1,
                factionName = "Any",
                kappaRequired = true,
                trader = new { id = "fixture-trader", name = korean ? "상인" : "Trader", normalizedName = "trader" },
                map = (object?)null,
                requiredPrestige = (object?)null,
                taskRequirements = Array.Empty<object>(),
                objectives = new object[] { objective }
            },
            new
            {
                id = "fixture-quest-second",
                name = korean ? "두 번째 퀘스트" : "Second Fixture Quest",
                normalizedName = "second-fixture-quest",
                wikiLink = "https://example.invalid/quest-second",
                minPlayerLevel = 2,
                factionName = "Any",
                kappaRequired = false,
                trader = new { id = "fixture-trader", name = korean ? "상인" : "Trader", normalizedName = "trader" },
                map = (object?)null,
                requiredPrestige = (object?)null,
                taskRequirements = new[]
                {
                    new
                    {
                        task = new { id = "fixture-quest-first" },
                        status = new[] { "complete" }
                    }
                },
                objectives = Array.Empty<object>()
            }
        ];
    }

    private static object[] CreateHideout(bool korean)
    {
        return
        [
            new
            {
                id = "fixture-station-workbench",
                name = korean ? "작업대" : "Workbench",
                normalizedName = "workbench",
                imageLink = "https://example.invalid/workbench.png",
                levels = new[]
                {
                    new
                    {
                        id = "fixture-station-workbench-level-1",
                        level = 1,
                        constructionTime = 60,
                        itemRequirements = new[]
                        {
                            new
                            {
                                item = new
                                {
                                    id = "fixture-item-wire",
                                    name = korean ? "전선" : "Wire",
                                    normalizedName = "wire",
                                    iconLink = "https://example.invalid/wire.png"
                                },
                                count = 3,
                                quantity = 3
                            }
                        },
                        stationLevelRequirements = Array.Empty<object>(),
                        traderRequirements = Array.Empty<object>(),
                        skillRequirements = Array.Empty<object>()
                    }
                }
            }
        ];
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object value)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }
}
