using System.Net;
using System.Text;
using System.Text.Json;

internal sealed class FixtureTarkovApiHandler : HttpMessageHandler
{
    public int StaticRequestCount { get; private set; }
    public int GraphQlRequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            StaticRequestCount++;
            return HandleStaticJsonRequest(request.RequestUri?.AbsolutePath ?? string.Empty);
        }

        GraphQlRequestCount++;
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

    private static HttpResponseMessage HandleStaticJsonRequest(string path)
    {
        var normalized = path.Trim('/');

        return normalized switch
        {
            "regular/items" => JsonResponse(HttpStatusCode.OK, CreateStaticItems()),
            "regular/items_en" => TranslationResponse(EnglishTranslations()),
            "regular/items_ko" => TranslationResponse(KoreanTranslations()),

            "regular/traders" => JsonResponse(HttpStatusCode.OK, CreateStaticTraders()),
            "regular/traders_en" => TranslationResponse(EnglishTranslations()),
            "regular/traders_ko" => TranslationResponse(KoreanTranslations()),

            "regular/maps" => JsonResponse(HttpStatusCode.OK, CreateStaticMaps()),
            "regular/maps_en" => TranslationResponse(EnglishTranslations()),
            "regular/maps_ko" => TranslationResponse(KoreanTranslations()),

            "regular/tasks" => JsonResponse(HttpStatusCode.OK, CreateStaticTasks()),
            "regular/tasks_en" => TranslationResponse(EnglishTranslations()),
            "regular/tasks_ko" => TranslationResponse(KoreanTranslations()),

            "regular/hideout" => JsonResponse(HttpStatusCode.OK, CreateStaticHideout()),
            "regular/hideout_en" => TranslationResponse(EnglishTranslations()),
            "regular/hideout_ko" => TranslationResponse(KoreanTranslations()),

            _ => JsonResponse(
                HttpStatusCode.NotFound,
                new { error = $"Unknown fixture path: {path}" })
        };
    }

    private static object CreateStaticItems()
    {
        return new
        {
            data = new
            {
                items = new Dictionary<string, object>
                {
                    ["fixture-item-bolts"] = new
                    {
                        id = "fixture-item-bolts",
                        name = "fixture-item-bolts Name",
                        normalizedName = "bolts",
                        shortName = "fixture-item-bolts ShortName",
                        description = "fixture-item-bolts Description",
                        iconLink = "https://example.invalid/bolts.png",
                        wikiLink = "https://example.invalid/bolts",
                        categories = new[] { "fixture-category-barter" },
                        properties = new
                        {
                            caliber = "Caliber762x39",
                            projectileCount = 1,
                            damage = 58,
                            armorDamage = 47,
                            fragmentationChance = 0.12,
                            penetrationPower = 32,
                            accuracyModifier = 0.0,
                            recoilModifier = 0.05,
                            lightBleedModifier = 0.1,
                            heavyBleedModifier = 0.0,
                            initialSpeed = 700.0
                        },
                        buyFor = new[] { new { vendor = new { name = "Prapor" } } }
                    },
                    ["fixture-item-wire"] = new
                    {
                        id = "fixture-item-wire",
                        name = "fixture-item-wire Name",
                        normalizedName = "wire",
                        shortName = "fixture-item-wire ShortName",
                        description = "fixture-item-wire Description",
                        iconLink = "https://example.invalid/wire.png",
                        wikiLink = "https://example.invalid/wire",
                        categories = new[] { "fixture-category-barter" }
                    }
                },
                itemCategories = new Dictionary<string, object>
                {
                    ["fixture-category-barter"] = new
                    {
                        id = "fixture-category-barter",
                        name = "fixture-category-barter Name",
                        normalizedName = "barter-item"
                    }
                }
            },
            translations = Array.Empty<string>()
        };
    }

    private static object CreateStaticTraders()
    {
        return new
        {
            data = new Dictionary<string, object>
            {
                ["fixture-trader"] = new
                {
                    id = "fixture-trader",
                    name = "fixture-trader Name",
                    normalizedName = "trader"
                }
            },
            translations = Array.Empty<string>()
        };
    }

    private static object CreateStaticMaps()
    {
        return new
        {
            data = new
            {
                maps = new Dictionary<string, object>
                {
                    ["fixture-map"] = new
                    {
                        id = "fixture-map",
                        name = "fixture-map Name",
                        normalizedName = "fixture-map",
                        nameId = "fixture-map"
                    }
                }
            },
            translations = Array.Empty<string>()
        };
    }

    private static object CreateStaticTasks()
    {
        return new
        {
            data = new
            {
                tasks = new Dictionary<string, object>
                {
                    ["fixture-quest-first"] = new
                    {
                        id = "fixture-quest-first",
                        name = "fixture-quest-first Name",
                        normalizedName = "first-fixture-quest",
                        wikiLink = "https://example.invalid/quest-first",
                        minPlayerLevel = 1,
                        factionName = "Any",
                        kappaRequired = true,
                        trader = "fixture-trader",
                        map = (string?)null,
                        requiredPrestige = (string?)null,
                        taskRequirements = Array.Empty<object>(),
                        objectives = new object[]
                        {
                            new
                            {
                                id = "fixture-objective-bolts",
                                type = "findItem",
                                description = "fixture-objective-bolts Description",
                                optional = false,
                                maps = Array.Empty<string>(),
                                items = new[] { "fixture-item-bolts" },
                                count = 2,
                                foundInRaid = true,
                                dogTagLevel = (int?)null
                            }
                        }
                    },
                    ["fixture-quest-second"] = new
                    {
                        id = "fixture-quest-second",
                        name = "fixture-quest-second Name",
                        normalizedName = "second-fixture-quest",
                        wikiLink = "https://example.invalid/quest-second",
                        minPlayerLevel = 2,
                        factionName = "Any",
                        kappaRequired = false,
                        trader = "fixture-trader",
                        map = "fixture-map",
                        requiredPrestige = (string?)null,
                        taskRequirements = new[]
                        {
                            new
                            {
                                task = "fixture-quest-first",
                                status = new[] { "complete" }
                            }
                        },
                        objectives = Array.Empty<object>()
                    }
                },
                prestige = new Dictionary<string, object>()
            },
            translations = Array.Empty<string>()
        };
    }

    private static object CreateStaticHideout()
    {
        return new
        {
            data = new Dictionary<string, object>
            {
                ["fixture-station-workbench"] = new
                {
                    id = "fixture-station-workbench",
                    name = "fixture-station-workbench Name",
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
                                    id = "fixture-hideout-item-wire",
                                    item = "fixture-item-wire",
                                    count = 3
                                }
                            },
                            stationLevelRequirements = Array.Empty<object>(),
                            traderRequirements = Array.Empty<object>(),
                            skillRequirements = Array.Empty<object>()
                        }
                    }
                }
            },
            translations = Array.Empty<string>()
        };
    }

    private static Dictionary<string, string> EnglishTranslations()
    {
        return new Dictionary<string, string>
        {
            ["fixture-item-bolts Name"] = "Bolts",
            ["fixture-item-bolts ShortName"] = "Bolts",
            ["fixture-item-bolts Description"] = "Fastening bolts",
            ["fixture-item-wire Name"] = "Wire",
            ["fixture-item-wire ShortName"] = "Wire",
            ["fixture-item-wire Description"] = "Electrical wire",
            ["fixture-category-barter Name"] = "Barter item",
            ["fixture-trader Name"] = "Trader",
            ["fixture-map Name"] = "Fixture Map",
            ["fixture-quest-first Name"] = "First Fixture Quest",
            ["fixture-quest-second Name"] = "Second Fixture Quest",
            ["fixture-objective-bolts Description"] = "Obtain bolts",
            ["fixture-station-workbench Name"] = "Workbench"
        };
    }

    private static Dictionary<string, string> KoreanTranslations()
    {
        return new Dictionary<string, string>
        {
            ["fixture-item-bolts Name"] = "볼트",
            ["fixture-item-bolts ShortName"] = "볼트",
            ["fixture-item-bolts Description"] = "고정용 볼트",
            ["fixture-item-wire Name"] = "전선",
            ["fixture-item-wire ShortName"] = "전선",
            ["fixture-item-wire Description"] = "전기 배선용 전선",
            ["fixture-category-barter Name"] = "교환 아이템",
            ["fixture-trader Name"] = "상인",
            ["fixture-map Name"] = "테스트 지도",
            ["fixture-quest-first Name"] = "첫 번째 퀘스트",
            ["fixture-quest-second Name"] = "두 번째 퀘스트",
            ["fixture-objective-bolts Description"] = "볼트를 획득하십시오",
            ["fixture-station-workbench Name"] = "작업대"
        };
    }

    private static HttpResponseMessage TranslationResponse(
        Dictionary<string, string> translations)
    {
        return JsonResponse(HttpStatusCode.OK, new { data = translations });
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
