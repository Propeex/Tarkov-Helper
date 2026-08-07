using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Services;

internal sealed partial class TarkovDataDatabaseBuilder
{
    private const string JsonApiBaseUrl = "https://json.tarkov.dev/";
    private const int MaxJsonApiAttempts = 3;
    private static readonly TimeSpan JsonRequestTimeout = TimeSpan.FromSeconds(45);

    internal async Task<DatabaseBuildResult> BuildPreferredAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("데이터베이스 경로가 비어 있습니다.", nameof(databasePath));
        if (!File.Exists(databasePath))
            throw new FileNotFoundException(
                "기존 tarkov_data.db가 없습니다. 지도와 수동 보정 데이터를 보존하려면 번들 데이터베이스가 필요합니다.",
                databasePath);

        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("데이터베이스 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);

        var tempPath = databasePath + ".rebuild.tmp";
        var backupPath = databasePath + ".rebuild.bak";
        var staticDataReady = false;

        CleanupFile(tempPath);
        SqliteConnection.ClearAllPools();
        File.Copy(databasePath, tempPath, overwrite: true);

        try
        {
            Report("API", "정적 JSON API 연결 중", 1, 0, null);
            var data = await FetchStaticJsonDataAsync(cancellationToken);
            staticDataReady = true;

            Report("DB", "기존 데이터베이스 구조를 확인하는 중", 65, 0, null);
            var counts = await RewriteDatabaseAsync(tempPath, data, cancellationToken);

            Report("검증", "아이템 연결과 데이터 무결성을 검사하는 중", 93, 0, null);
            await ValidateDatabaseAsync(tempPath, counts, cancellationToken);

            Report("교체", "검증된 데이터베이스로 교체하는 중", 98, 0, null);
            ReplaceDatabaseAtomically(tempPath, databasePath, backupPath);

            Report("완료", "데이터베이스 생성 완료", 100, counts.TotalRows, counts.TotalRows);
            return new DatabaseBuildResult(
                counts.Items,
                counts.Ammo,
                counts.Quests,
                counts.QuestRequiredItems,
                counts.HideoutStations,
                counts.HideoutItemRequirements,
                backupPath);
        }
        catch (OperationCanceledException)
        {
            CleanupFile(tempPath);
            throw;
        }
        catch (QuestCatalogOverlayException overlayException)
        {
            CleanupFile(tempPath);
            throw new InvalidOperationException(
                "현재 퀘스트 목록 보정 데이터를 검증하지 못해 DB 업데이트를 중단했습니다.",
                overlayException);
        }
        catch (Exception staticException) when (!staticDataReady)
        {
            CleanupFile(tempPath);
            throw new InvalidOperationException(
                "현재 퀘스트 목록은 검증된 정적 JSON API와 보정 데이터 경로가 필요합니다. " +
                "보정되지 않은 퀘스트 목록으로 되돌아갈 수 있는 GraphQL 예비 경로는 사용하지 않습니다. " +
                $"JSON: {CompactApiError(staticException.Message)}",
                staticException);
        }
        catch
        {
            CleanupFile(tempPath);
            throw;
        }
    }

    private async Task<MergedApiData> FetchStaticJsonDataAsync(
        CancellationToken cancellationToken)
    {
        var itemDocuments = await FetchLocalizedJsonAsync(
            "regular/items",
            "아이템",
            1,
            18,
            cancellationToken);
        var itemsEn = ParseItems(itemDocuments.English);
        var itemsKo = ParseItems(itemDocuments.Korean);
        var itemLookupEn = UniqueById(itemsEn);
        var itemLookupKo = UniqueById(itemsKo);

        var traderDocuments = await FetchLocalizedJsonAsync(
            "regular/traders",
            "상인 참조",
            18,
            23,
            cancellationToken);
        var tradersEn = ParseNamedLookup(traderDocuments.English, "traders");
        var tradersKo = ParseNamedLookup(traderDocuments.Korean, "traders");
        ResolveStaticTraderPurchases(itemsEn, tradersEn);

        if (_enrichAmmoSources)
        {
            var barterDocument = await DownloadJsonAsync(
                "regular/barters",
                "상인 교환 데이터",
                23,
                27,
                cancellationToken);
            EnrichAmmoSourcesFromStaticBarters(itemsEn, barterDocument, tradersEn);
        }

        var mapDocuments = await FetchLocalizedJsonAsync(
            "regular/maps",
            "지도 참조",
            27,
            32,
            cancellationToken);
        var mapsEn = ParseNamedLookup(mapDocuments.English, "maps");
        var mapsKo = ParseNamedLookup(mapDocuments.Korean, "maps");

        var taskDocuments = await FetchLocalizedJsonAsync(
            "regular/tasks",
            "퀘스트",
            32,
            48,
            cancellationToken);

        Report("API", "현재 퀘스트 목록 보정 데이터를 받는 중", 48, 0, null);
        var (questOverlay, _) = await DownloadQuestCatalogOverlayAsync(cancellationToken);
        var questOverlayInfo = ApplyQuestCatalogOverlay(
            taskDocuments.English,
            taskDocuments.Korean,
            questOverlay);

        var tasksEn = ParseTasks(taskDocuments.English, itemLookupEn, tradersEn, mapsEn);
        var tasksKo = ParseTasks(taskDocuments.Korean, itemLookupKo, tradersKo, mapsKo);

        var hideoutDocuments = await FetchLocalizedJsonAsync(
            "regular/hideout",
            "은신처",
            52,
            60,
            cancellationToken);
        var hideoutStationsEn = ParseNamedLookup(hideoutDocuments.English);
        var hideoutEn = ParseHideout(hideoutDocuments.English, itemLookupEn, tradersEn);
        var hideoutKo = ParseHideout(hideoutDocuments.Korean, itemLookupKo, tradersKo);

        if (_enrichAmmoSources)
        {
            var craftDocument = await DownloadJsonAsync(
                "regular/crafts",
                "제작 데이터",
                60,
                65,
                cancellationToken);
            EnrichAmmoSourcesFromStaticCrafts(itemsEn, craftDocument, hideoutStationsEn);
        }

        if (itemsEn.Count == 0 || tasksEn.Count == 0 || hideoutEn.Count == 0)
            throw new InvalidDataException("tarkov.dev 정적 JSON API 데이터가 비어 있습니다.");

        return MergeApiData(itemsEn, itemsKo, tasksEn, tasksKo, hideoutEn, hideoutKo)
            with
            {
                Source = $"tarkov.dev + tarkov-data-overlay {questOverlayInfo.Version}",
                Transport = "static-json+overlay"
            };
    }

    private static void ResolveStaticTraderPurchases(
        IEnumerable<ApiItem> items,
        IReadOnlyDictionary<string, ApiNamedEntity> traderLookup)
    {
        foreach (var item in items)
        {
            foreach (var purchase in item.BuyFor)
            {
                var traderId = purchase.Vendor?.Id;
                if (string.IsNullOrWhiteSpace(traderId) ||
                    !traderLookup.TryGetValue(traderId, out var trader))
                {
                    continue;
                }

                purchase.Vendor = CloneNamedEntity(trader);
            }
        }
    }

    private static void EnrichAmmoSourcesFromStaticBarters(
        IEnumerable<ApiItem> items,
        JsonObject barterRoot,
        IReadOnlyDictionary<string, ApiNamedEntity> traderLookup)
    {
        var itemLookup = UniqueById(items);
        if (barterRoot["data"] is not JsonArray barters)
            throw new InvalidDataException("tarkov.dev 상인 교환 응답에 data 배열이 없습니다.");

        var enriched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var barter in barters.OfType<JsonObject>())
        {
            var offeredItemId = barter["offeredItem"] is JsonObject offeredItem
                ? ReferenceId(offeredItem["item"])
                : null;
            if (string.IsNullOrWhiteSpace(offeredItemId) ||
                !itemLookup.TryGetValue(offeredItemId, out var item) ||
                item.Properties == null)
            {
                continue;
            }

            var traderId = ReferenceId(barter["trader"]);
            var trader = ResolveStaticNamedEntity(traderId, traderLookup, "trader");
            if (IsFleaMarket(trader.Name) || IsFleaMarket(trader.NormalizedName))
                continue;

            item.BartersFor.Add(new ApiSourceReference
            {
                Trader = trader,
                Level = Math.Max(1, GetInt(barter, "minTraderLevel") ?? 1)
            });
            enriched.Add(item.Id);
        }

        Log.Info($"Ammo barter sources enriched from static API: {enriched.Count}");
    }

    private static void EnrichAmmoSourcesFromStaticCrafts(
        IEnumerable<ApiItem> items,
        JsonObject craftRoot,
        IReadOnlyDictionary<string, ApiNamedEntity> stationLookup)
    {
        var itemLookup = UniqueById(items);
        if (craftRoot["data"] is not JsonArray crafts)
            throw new InvalidDataException("tarkov.dev 제작 응답에 data 배열이 없습니다.");

        var enriched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var craft in crafts.OfType<JsonObject>())
        {
            var productItemId = craft["productItem"] is JsonObject productItem
                ? ReferenceId(productItem["item"])
                : null;
            if (string.IsNullOrWhiteSpace(productItemId) ||
                !itemLookup.TryGetValue(productItemId, out var item) ||
                item.Properties == null)
            {
                continue;
            }

            var stationId = ReferenceId(craft["station"]);
            item.CraftsFor.Add(new ApiSourceReference
            {
                Station = ResolveStaticNamedEntity(stationId, stationLookup, "hideout"),
                Level = Math.Max(1, GetInt(craft, "level") ?? 1)
            });
            enriched.Add(item.Id);
        }

        Log.Info($"Ammo craft sources enriched from static API: {enriched.Count}");
    }

    private static ApiNamedEntity ResolveStaticNamedEntity(
        string? id,
        IReadOnlyDictionary<string, ApiNamedEntity> lookup,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(id) && lookup.TryGetValue(id, out var known))
            return CloneNamedEntity(known);

        return new ApiNamedEntity
        {
            Id = id ?? string.Empty,
            Name = id ?? fallback,
            NormalizedName = id ?? fallback
        };
    }

    private static ApiNamedEntity CloneNamedEntity(ApiNamedEntity source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        NormalizedName = source.NormalizedName
    };

    private async Task<LocalizedJsonDocuments> FetchLocalizedJsonAsync(
        string path,
        string label,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        var span = phaseEnd - phaseStart;
        var baseEnd = phaseStart + span * 0.70;
        var englishEnd = phaseStart + span * 0.85;

        var baseDocument = await DownloadJsonAsync(
            path,
            $"{label} 기본 데이터",
            phaseStart,
            baseEnd,
            cancellationToken);
        var englishTranslations = ExtractTranslations(await DownloadJsonAsync(
            path + "_en",
            $"{label} 영문 번역",
            baseEnd,
            englishEnd,
            cancellationToken));
        var koreanTranslations = ExtractTranslations(await DownloadJsonAsync(
            path + "_ko",
            $"{label} 한국어 번역",
            englishEnd,
            phaseEnd,
            cancellationToken));

        var english = TranslateDocument(baseDocument, englishTranslations, null);
        var korean = TranslateDocument(baseDocument, koreanTranslations, englishTranslations);

        Report("API", $"{label} 데이터 준비 완료", phaseEnd, 0, null);
        return new LocalizedJsonDocuments(english, korean);
    }

    private async Task<JsonObject> DownloadJsonAsync(
        string relativePath,
        string label,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxJsonApiAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(JsonRequestTimeout);
            var requestToken = timeoutSource.Token;

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(new Uri(JsonApiBaseUrl), relativePath));
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestToken);

                if (!response.IsSuccessStatusCode)
                {
                    var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                    (int)response.StatusCode >= 500;
                    if (!retryable || attempt == MaxJsonApiAttempts)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync(requestToken);
                        throw new HttpRequestException(
                            $"JSON API 요청 실패 ({(int)response.StatusCode} {response.ReasonPhrase}): " +
                            TrimError(errorBody),
                            null,
                            response.StatusCode);
                    }

                    var delay = GetRetryDelay(response, attempt);
                    Report(
                        "API",
                        $"{label} 서버 지연 · {delay.TotalSeconds:F0}초 후 재시도 ({attempt}/{MaxJsonApiAttempts})",
                        Math.Max(phaseStart, _lastPercent),
                        0,
                        null);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(requestToken);
                await using var memory = new MemoryStream();
                var totalBytes = response.Content.Headers.ContentLength;
                var buffer = new byte[64 * 1024];
                long bytesReadTotal = 0;
                var lastReportedPercent = phaseStart - 1;

                while (true)
                {
                    var read = await responseStream.ReadAsync(buffer, requestToken);
                    if (read == 0)
                        break;

                    await memory.WriteAsync(buffer.AsMemory(0, read), requestToken);
                    bytesReadTotal += read;

                    var byteFraction = totalBytes is > 0
                        ? Math.Clamp(bytesReadTotal / (double)totalBytes.Value, 0, 1)
                        : Math.Clamp(1 - Math.Exp(-bytesReadTotal / 2_000_000d), 0, 0.95);
                    var percent = phaseStart + (phaseEnd - phaseStart) * byteFraction;

                    if (percent - lastReportedPercent >= 0.25)
                    {
                        Report("API", $"{label} 받는 중", percent, 0, null);
                        lastReportedPercent = percent;
                    }
                }

                memory.Position = 0;
                var node = await JsonNode.ParseAsync(
                    memory,
                    cancellationToken: requestToken);
                if (node is not JsonObject document)
                    throw new InvalidDataException($"{label} 응답이 JSON 객체가 아닙니다.");

                Report("API", $"{label} 수신 완료", phaseEnd, 0, null);
                return document;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"{label} 요청이 {JsonRequestTimeout.TotalSeconds:F0}초 안에 완료되지 않았습니다.",
                    exception);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidDataException)
            {
                lastException = exception;
            }

            if (attempt == MaxJsonApiAttempts)
                break;

            var retryDelay = TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, attempt)));
            Report(
                "API",
                $"{label} 재시도 · {retryDelay.TotalSeconds:F0}초 후 ({attempt}/{MaxJsonApiAttempts})",
                Math.Max(phaseStart, _lastPercent),
                0,
                null);
            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new HttpRequestException(
            $"{label} 요청이 반복적으로 실패했습니다: {CompactApiError(lastException?.Message)}",
            lastException);
    }

    private static Dictionary<string, string> ExtractTranslations(JsonObject document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document["data"] is not JsonObject data)
            return result;

        foreach (var (key, value) in data)
        {
            var translated = NodeString(value);
            if (!string.IsNullOrWhiteSpace(key) && translated is not null)
                result[key] = translated;
        }

        return result;
    }

    private static JsonObject TranslateDocument(
        JsonObject baseDocument,
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string>? fallback)
    {
        var clone = baseDocument.DeepClone();
        var translated = TranslateNode(clone, primary, fallback);
        return translated as JsonObject
            ?? throw new InvalidDataException("번역된 tarkov.dev 응답이 JSON 객체가 아닙니다.");
    }

    private static JsonNode? TranslateNode(
        JsonNode? node,
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string>? fallback)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(pair => pair.Key).ToArray())
                {
                    var original = jsonObject[key];
                    var translatedChild = TranslateNode(original, primary, fallback);
                    if (!ReferenceEquals(original, translatedChild))
                        jsonObject[key] = translatedChild;
                }
                return jsonObject;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var original = jsonArray[index];
                    var translatedChild = TranslateNode(original, primary, fallback);
                    if (!ReferenceEquals(original, translatedChild))
                        jsonArray[index] = translatedChild;
                }
                return jsonArray;

            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                if (primary.TryGetValue(text, out var translated))
                    return JsonValue.Create(translated);
                if (fallback != null && fallback.TryGetValue(text, out translated))
                    return JsonValue.Create(translated);
                return JsonValue.Create(text);

            default:
                return node;
        }
    }

    private static List<ApiItem> ParseItems(JsonObject root)
    {
        var data = RequiredObject(root, "data", "아이템");
        var itemObjects = RequiredObject(data, "items", "아이템");
        var categoryLookup = data["itemCategories"] is JsonObject categories
            ? ParseNamedDictionary(categories)
            : new Dictionary<string, ApiNamedEntity>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ApiItem>(itemObjects.Count);

        foreach (var (key, node) in itemObjects)
        {
            if (node is not JsonObject itemObject)
                continue;

            var id = GetString(itemObject, "id") ?? key;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var itemCategories = ResolveNamedList(itemObject["categories"], categoryLookup);
            result.Add(new ApiItem
            {
                Id = id,
                Name = GetString(itemObject, "name"),
                NormalizedName = GetString(itemObject, "normalizedName"),
                ShortName = GetString(itemObject, "shortName"),
                Description = GetString(itemObject, "description"),
                IconLink = GetString(itemObject, "iconLink"),
                WikiLink = GetString(itemObject, "wikiLink"),
                Category = itemCategories.FirstOrDefault(),
                Categories = itemCategories,
                Properties = ParseAmmoProperties(itemObject),
                BuyFor = ParseStaticTraderPurchases(itemObject["buyFromTrader"] as JsonArray),
                SourceJson = itemObject.ToJsonString()
            });
        }

        return result;
    }

    private static List<ApiItemPrice> ParseStaticTraderPurchases(JsonArray? purchases)
    {
        var result = new List<ApiItemPrice>();
        if (purchases == null)
            return result;

        foreach (var purchase in purchases.OfType<JsonObject>())
        {
            var traderId = ReferenceId(purchase["trader"]);
            if (string.IsNullOrWhiteSpace(traderId))
                continue;

            var level = Math.Max(1, GetInt(purchase, "minTraderLevel") ?? 1);
            result.Add(new ApiItemPrice
            {
                PriceRUB = GetInt(purchase, "priceRUB"),
                Vendor = new ApiNamedEntity { Id = traderId },
                Requirements =
                [
                    new ApiPriceRequirement
                    {
                        Type = "loyaltyLevel",
                        Value = level
                    }
                ]
            });
        }

        return result;
    }

    private static ApiAmmoProperties? ParseAmmoProperties(JsonObject itemObject)
    {
        if (itemObject["properties"] is not JsonObject properties)
            return null;

        var caliber = GetString(properties, "caliber");
        var damage = GetInt(properties, "damage");
        var penetration = GetInt(properties, "penetrationPower");
        if (string.IsNullOrWhiteSpace(caliber) || (!damage.HasValue && !penetration.HasValue))
            return null;

        return new ApiAmmoProperties
        {
            Caliber = caliber,
            ProjectileCount = GetInt(properties, "projectileCount") ?? 1,
            Damage = damage ?? 0,
            ArmorDamage = GetInt(properties, "armorDamage") ?? 0,
            FragmentationChance = GetDouble(properties, "fragmentationChance") ?? 0,
            PenetrationPower = penetration ?? 0,
            AccuracyModifier = GetDouble(properties, "accuracyModifier", "accuracy") ?? 0,
            RecoilModifier = GetDouble(properties, "recoilModifier", "recoil") ?? 0,
            LightBleedModifier = GetDouble(properties, "lightBleedModifier") ?? 0,
            HeavyBleedModifier = GetDouble(properties, "heavyBleedModifier") ?? 0,
            InitialSpeed = GetDouble(properties, "initialSpeed") ?? 0,
            RicochetChance = GetDouble(properties, "ricochetChance") ?? 0,
            PenetrationChance = GetDouble(properties, "penetrationChance") ?? 0,
            BulletMassGrams = GetDouble(properties, "bulletMassGrams") ?? 0,
            BulletDiameterMillimeters = GetDouble(properties, "bulletDiameterMilimeters", "bulletDiameterMillimeters") ?? 0,
            BallisticCoefficient = GetDouble(properties, "ballisticCoeficient", "ballisticCoefficient") ?? 0,
            DurabilityBurnFactor = GetDouble(properties, "durabilityBurnFactor") ?? 0,
            HeatFactor = GetDouble(properties, "heatFactor") ?? 0,
            MisfireChance = GetDouble(properties, "misfireChance") ?? 0,
            FailureToFeedChance = GetDouble(properties, "failureToFeedChance") ?? 0,
            Tracer = GetBool(properties, "tracer") ?? false,
            TracerColor = GetString(properties, "tracerColor"),
            AmmoType = GetString(properties, "ammoType"),
            AcquisitionSource = ParseAcquisitionSource(itemObject),
            SourceJson = properties.ToJsonString()
        };
    }

    private static string? ParseAcquisitionSource(JsonObject itemObject)
    {
        var sources = new List<string>();
        AddTraderPurchaseSources(itemObject["buyFor"] as JsonArray, sources);
        AddStructuredSources(itemObject["bartersFor"] as JsonArray, "trader", "trader", sources, includeLevel: true);
        AddStructuredSources(itemObject["craftsFor"] as JsonArray, "station", "craft", sources, includeLevel: true);
        return sources.Count == 0
            ? null
            : string.Join(" · ", sources.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void AddTraderPurchaseSources(JsonArray? values, ICollection<string> sources)
    {
        if (values == null)
            return;

        foreach (var value in values.OfType<JsonObject>())
        {
            if (value["vendor"] is not JsonObject vendor)
                continue;

            var name = GetString(vendor, "name", "normalizedName");
            if (string.IsNullOrWhiteSpace(name) || IsFleaMarket(name))
                continue;

            var level = 1;
            if (value["requirements"] is JsonArray requirements)
            {
                foreach (var requirement in requirements.OfType<JsonObject>())
                {
                    if (!string.Equals(GetString(requirement, "type"), "loyaltyLevel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    level = Math.Max(1, GetInt(requirement, "value") ?? 1);
                    break;
                }
            }

            sources.Add($"trader:{name}:level:{level}");
        }
    }

    private static void AddStructuredSources(
        JsonArray? values,
        string entityKey,
        string sourceType,
        ICollection<string> sources,
        bool includeLevel)
    {
        if (values == null)
            return;

        foreach (var value in values.OfType<JsonObject>())
        {
            if (value[entityKey] is not JsonObject sourceObject)
                continue;

            var name = GetString(sourceObject, "name", "normalizedName");
            if (string.IsNullOrWhiteSpace(name) ||
                (sourceType.Equals("trader", StringComparison.OrdinalIgnoreCase) && IsFleaMarket(name)))
            {
                continue;
            }

            var source = $"{sourceType}:{name}";
            var level = includeLevel ? GetInt(value, "level") : null;
            if (includeLevel)
                source += $":level:{Math.Max(1, level ?? 1)}";
            sources.Add(source);
        }
    }

    private static Dictionary<string, ApiNamedEntity> ParseNamedLookup(
        JsonObject root,
        params string[] containerNames)
    {
        var data = RequiredObject(root, "data", "참조");
        JsonObject values = data;

        foreach (var containerName in containerNames)
        {
            if (data[containerName] is JsonObject container)
            {
                values = container;
                break;
            }
        }

        return ParseNamedDictionary(values);
    }

    private static Dictionary<string, ApiNamedEntity> ParseNamedDictionary(JsonObject values)
    {
        var result = new Dictionary<string, ApiNamedEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, node) in values)
        {
            if (node is not JsonObject value)
                continue;

            var id = GetString(value, "id") ?? key;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            result[id] = new ApiNamedEntity
            {
                Id = id,
                Name = GetString(value, "name", "traderName", "locationName"),
                NormalizedName = GetString(value, "normalizedName", "nameId")
            };
        }

        return result;
    }

    private static List<ApiTask> ParseTasks(
        JsonObject root,
        IReadOnlyDictionary<string, ApiItem> itemLookup,
        IReadOnlyDictionary<string, ApiNamedEntity> traderLookup,
        IReadOnlyDictionary<string, ApiNamedEntity> mapLookup)
    {
        var data = RequiredObject(root, "data", "퀘스트");
        var taskObjects = RequiredObject(data, "tasks", "퀘스트");
        var prestigeLookup = data["prestige"];
        var result = new List<ApiTask>(taskObjects.Count);

        foreach (var (key, node) in taskObjects)
        {
            if (node is not JsonObject taskObject)
                continue;

            var id = GetString(taskObject, "id") ?? key;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var task = new ApiTask
            {
                Id = id,
                Name = GetString(taskObject, "name"),
                NormalizedName = GetString(taskObject, "normalizedName"),
                WikiLink = GetString(taskObject, "wikiLink"),
                MinPlayerLevel = GetInt(taskObject, "minPlayerLevel"),
                FactionName = GetString(taskObject, "factionName", "faction"),
                KappaRequired = GetBool(taskObject, "kappaRequired") ?? false,
                LightkeeperRequired = GetBool(taskObject, "lightkeeperRequired") ?? false,
                Restartable = GetBool(taskObject, "restartable") ?? false,
                GameModes = StringList(taskObject["gameMode"]),
                AvailableDelaySecondsMin = GetInt(taskObject, "availableDelaySecondsMin"),
                AvailableDelaySecondsMax = GetInt(taskObject, "availableDelaySecondsMax"),
                TaskImageLink = GetString(taskObject, "taskImageLink"),
                NeededKeysJson = taskObject["neededKeys"]?.ToJsonString(),
                OtherRequirementsJson = taskObject["otherRequirements"]?.ToJsonString(),
                StartRewardsJson = taskObject["startRewards"]?.ToJsonString(),
                FinishRewardsJson = taskObject["finishRewards"]?.ToJsonString(),
                FailureOutcomeJson = taskObject["failureOutcome"]?.ToJsonString(),
                SourceJson = taskObject.ToJsonString(),
                Trader = ResolveNamed(taskObject["trader"], traderLookup),
                Map = ResolveNamed(taskObject["map"], mapLookup),
                RequiredPrestige = ResolvePrestige(taskObject["requiredPrestige"], prestigeLookup)
            };

            if (taskObject["taskRequirements"] is JsonArray requirements)
            {
                foreach (var requirementNode in requirements)
                {
                    if (requirementNode is not JsonObject requirementObject)
                        continue;

                    var requiredTaskId = ReferenceId(requirementObject["task"]);
                    if (string.IsNullOrWhiteSpace(requiredTaskId))
                        continue;

                    task.TaskRequirements.Add(new ApiTaskRequirement
                    {
                        Id = GetString(requirementObject, "id"),
                        Task = new ApiIdReference { Id = requiredTaskId },
                        Status = StringList(requirementObject["status"]),
                        Notes = GetString(requirementObject, "notes"),
                        SourceJson = requirementObject.ToJsonString()
                    });
                }
            }

            if (taskObject["traderRequirements"] is JsonArray traderRequirements)
            {
                foreach (var requirementNode in traderRequirements)
                {
                    if (requirementNode is not JsonObject requirementObject)
                        continue;

                    var trader = ResolveNamed(requirementObject["trader"], traderLookup);
                    if (trader == null)
                        continue;

                    task.TraderRequirements.Add(new ApiTraderRequirement
                    {
                        Id = GetString(requirementObject, "id"),
                        Trader = trader,
                        RequirementType = GetString(requirementObject, "requirementType"),
                        CompareMethod = GetString(requirementObject, "compareMethod"),
                        Value = GetDouble(requirementObject, "value"),
                        Level = GetInt(requirementObject, "level"),
                        SourceJson = requirementObject.ToJsonString()
                    });
                }
            }

            if (taskObject["objectives"] is JsonArray objectives)
            {
                foreach (var objectiveNode in objectives)
                {
                    if (objectiveNode is not JsonObject objectiveObject)
                        continue;

                    task.Objectives.Add(ParseObjective(
                        objectiveObject,
                        itemLookup,
                        mapLookup));
                }
            }

            result.Add(task);
        }

        return result;
    }

    private static ApiTaskObjective ParseObjective(
        JsonObject objectiveObject,
        IReadOnlyDictionary<string, ApiItem> itemLookup,
        IReadOnlyDictionary<string, ApiNamedEntity> mapLookup)
    {
        var items = ResolveItemList(objectiveObject["items"], itemLookup);
        if (items.Count == 0 && objectiveObject["item"] is not null)
        {
            var singleItem = ResolveItem(objectiveObject["item"], itemLookup);
            if (singleItem != null)
                items.Add(singleItem);
        }
        if (items.Count == 0 && objectiveObject["markerItem"] is not null)
        {
            var markerItem = ResolveItem(objectiveObject["markerItem"], itemLookup);
            if (markerItem != null)
                items.Add(markerItem);
        }
        if (items.Count == 0 && objectiveObject["useAny"] is not null)
            items.AddRange(ResolveItemList(objectiveObject["useAny"], itemLookup));

        var type = GetString(objectiveObject, "type");
        var typeName = GetString(objectiveObject, "__typename");
        if (items.Count > 0 && string.IsNullOrWhiteSpace(typeName))
            typeName = "TaskObjectiveItem";

        return new ApiTaskObjective
        {
            TypeName = typeName,
            Id = GetString(objectiveObject, "id"),
            Type = type,
            Description = GetString(objectiveObject, "description"),
            Optional = GetBool(objectiveObject, "optional") ?? false,
            Maps = ResolveNamedList(
                objectiveObject["maps"] ?? objectiveObject["map_ids"],
                mapLookup),
            Items = items,
            Count = GetInt(objectiveObject, "count"),
            FoundInRaid = GetBool(objectiveObject, "foundInRaid"),
            DogTagLevel = GetInt(objectiveObject, "dogTagLevel"),
            MinDurability = GetDouble(objectiveObject, "minDurability"),
            MaxDurability = GetDouble(objectiveObject, "maxDurability"),
            SourceJson = objectiveObject.ToJsonString()
        };
    }

    private static List<ApiHideoutStation> ParseHideout(
        JsonObject root,
        IReadOnlyDictionary<string, ApiItem> itemLookup,
        IReadOnlyDictionary<string, ApiNamedEntity> traderLookup)
    {
        var stationObjects = RequiredObject(root, "data", "은신처");
        var stationLookup = ParseNamedDictionary(stationObjects);
        var result = new List<ApiHideoutStation>(stationObjects.Count);

        foreach (var (key, node) in stationObjects)
        {
            if (node is not JsonObject stationObject)
                continue;

            var stationId = GetString(stationObject, "id") ?? key;
            if (string.IsNullOrWhiteSpace(stationId))
                continue;

            var station = new ApiHideoutStation
            {
                Id = stationId,
                Name = GetString(stationObject, "name"),
                NormalizedName = GetString(stationObject, "normalizedName"),
                ImageLink = GetString(stationObject, "imageLink")
            };

            if (stationObject["levels"] is JsonArray levels)
            {
                foreach (var levelNode in levels)
                {
                    if (levelNode is not JsonObject levelObject)
                        continue;

                    var level = new ApiHideoutLevel
                    {
                        Id = GetString(levelObject, "id"),
                        Level = GetInt(levelObject, "level") ?? 0,
                        ConstructionTime = GetInt(levelObject, "constructionTime") ?? 0,
                        SourceJson = levelObject.ToJsonString()
                    };

                    if (levelObject["itemRequirements"] is JsonArray itemRequirements)
                    {
                        foreach (var requirementNode in itemRequirements)
                        {
                            if (requirementNode is not JsonObject requirementObject)
                                continue;

                            var item = ResolveItem(requirementObject["item"], itemLookup);
                            if (item == null)
                                continue;

                            level.ItemRequirements.Add(new ApiHideoutItemRequirement
                            {
                                Item = item,
                                Count = GetInt(requirementObject, "count"),
                                Quantity = GetInt(requirementObject, "quantity"),
                                AttributesJson = requirementObject["attributes"]?.ToJsonString(),
                                SourceJson = requirementObject.ToJsonString()
                            });
                        }
                    }

                    if (levelObject["stationLevelRequirements"] is JsonArray stationRequirements)
                    {
                        foreach (var requirementNode in stationRequirements)
                        {
                            if (requirementNode is not JsonObject requirementObject)
                                continue;

                            var requiredStation = ResolveNamed(
                                requirementObject["station"],
                                stationLookup);
                            if (requiredStation == null)
                                continue;

                            level.StationLevelRequirements.Add(new ApiStationRequirement
                            {
                                Station = requiredStation,
                                Level = GetInt(requirementObject, "level") ?? 0
                            });
                        }
                    }

                    if (levelObject["traderRequirements"] is JsonArray traderRequirements)
                    {
                        foreach (var requirementNode in traderRequirements)
                        {
                            if (requirementNode is not JsonObject requirementObject)
                                continue;

                            var trader = ResolveNamed(
                                requirementObject["trader"],
                                traderLookup);
                            if (trader == null)
                                continue;

                            level.TraderRequirements.Add(new ApiTraderRequirement
                            {
                                Id = GetString(requirementObject, "id"),
                                Trader = trader,
                                RequirementType = GetString(requirementObject, "requirementType"),
                                CompareMethod = GetString(requirementObject, "compareMethod"),
                                Value = GetDouble(requirementObject, "value"),
                                Level = GetInt(requirementObject, "level"),
                                SourceJson = requirementObject.ToJsonString()
                            });
                        }
                    }

                    if (levelObject["skillRequirements"] is JsonArray skillRequirements)
                    {
                        foreach (var requirementNode in skillRequirements)
                        {
                            if (requirementNode is not JsonObject requirementObject)
                                continue;

                            var skillName = NodeString(requirementObject["skill"]) ??
                                            GetString(requirementObject, "name");
                            var requirementId = GetString(requirementObject, "id");
                            if (string.IsNullOrWhiteSpace(skillName) &&
                                string.IsNullOrWhiteSpace(requirementId))
                                continue;

                            level.SkillRequirements.Add(new ApiSkillRequirement
                            {
                                Name = skillName,
                                Skill = new ApiNamedEntity
                                {
                                    Id = requirementId ?? skillName!,
                                    Name = skillName,
                                    NormalizedName = null
                                },
                                Level = GetInt(requirementObject, "level") ?? 0
                            });
                        }
                    }

                    station.Levels.Add(level);
                }
            }

            result.Add(station);
        }

        return result;
    }

    private static ApiPrestige? ResolvePrestige(
        JsonNode? node,
        JsonNode? prestigeLookup)
    {
        var directLevel = NodeInt(node);
        if (directLevel.HasValue)
            return new ApiPrestige { PrestigeLevel = directLevel };

        if (node is JsonObject prestigeObject)
            return new ApiPrestige { PrestigeLevel = GetInt(prestigeObject, "prestigeLevel") };

        var id = NodeString(node);
        if (!string.IsNullOrWhiteSpace(id) &&
            ResolvePrestigeObject(prestigeLookup, id) is JsonObject lookupObject)
        {
            return new ApiPrestige
            {
                PrestigeLevel = GetInt(lookupObject, "prestigeLevel")
            };
        }

        return null;
    }

    private static List<ApiNamedEntity> ResolveNamedList(
        JsonNode? node,
        IReadOnlyDictionary<string, ApiNamedEntity> lookup)
    {
        var result = new List<ApiNamedEntity>();
        if (node is JsonArray array)
        {
            foreach (var entry in array)
            {
                var resolved = ResolveNamed(entry, lookup);
                if (resolved != null)
                    result.Add(resolved);
            }
        }
        else
        {
            var resolved = ResolveNamed(node, lookup);
            if (resolved != null)
                result.Add(resolved);
        }

        return result;
    }

    private static ApiNamedEntity? ResolveNamed(
        JsonNode? node,
        IReadOnlyDictionary<string, ApiNamedEntity> lookup)
    {
        var id = ReferenceId(node);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (lookup.TryGetValue(id, out var known))
        {
            return new ApiNamedEntity
            {
                Id = known.Id,
                Name = known.Name,
                NormalizedName = known.NormalizedName
            };
        }

        if (node is JsonObject value)
        {
            return new ApiNamedEntity
            {
                Id = id,
                Name = GetString(value, "name", "traderName", "locationName"),
                NormalizedName = GetString(value, "normalizedName", "nameId")
            };
        }

        return new ApiNamedEntity
        {
            Id = id,
            Name = id,
            NormalizedName = id
        };
    }

    private static List<ApiItemReference> ResolveItemList(
        JsonNode? node,
        IReadOnlyDictionary<string, ApiItem> lookup)
    {
        var result = new List<ApiItemReference>();
        if (node is JsonArray array)
        {
            foreach (var entry in array)
            {
                var item = ResolveItem(entry, lookup);
                if (item != null)
                    result.Add(item);
            }
        }
        else
        {
            var item = ResolveItem(node, lookup);
            if (item != null)
                result.Add(item);
        }

        return result;
    }

    private static ApiItemReference? ResolveItem(
        JsonNode? node,
        IReadOnlyDictionary<string, ApiItem> lookup)
    {
        var id = ReferenceId(node);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (lookup.TryGetValue(id, out var known))
        {
            return new ApiItemReference
            {
                Id = known.Id,
                Name = known.Name,
                NormalizedName = known.NormalizedName,
                IconLink = known.IconLink
            };
        }

        if (node is JsonObject value)
        {
            return new ApiItemReference
            {
                Id = id,
                Name = GetString(value, "name"),
                NormalizedName = GetString(value, "normalizedName"),
                IconLink = GetString(value, "iconLink")
            };
        }

        return null;
    }

    private static JsonObject RequiredObject(
        JsonObject source,
        string propertyName,
        string label)
    {
        if (source[propertyName] is JsonObject value)
            return value;

        throw new InvalidDataException(
            $"tarkov.dev {label} 응답에 {propertyName} 객체가 없습니다.");
    }

    private static string? ReferenceId(JsonNode? node)
    {
        if (node is JsonObject value)
            return GetString(value, "id");

        return NodeString(node);
    }

    private static string? GetString(JsonObject source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = NodeString(source[propertyName]);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? GetInt(JsonObject source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = NodeInt(source[propertyName]);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static double? GetDouble(JsonObject source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source[propertyName] is not JsonValue value)
                continue;
            if (value.TryGetValue<double>(out var number))
                return number;
            if (value.TryGetValue<int>(out var integer))
                return integer;
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static bool? GetBool(JsonObject source, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source[propertyName] is not JsonValue value)
                continue;
            if (value.TryGetValue<bool>(out var boolean))
                return boolean;
            if (value.TryGetValue<int>(out var integer))
                return integer != 0;
            if (value.TryGetValue<string>(out var text) &&
                bool.TryParse(text, out boolean))
                return boolean;
        }

        return null;
    }

    private static int? NodeInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var integer))
            return integer;
        if (value.TryGetValue<long>(out var longValue) &&
            longValue is >= int.MinValue and <= int.MaxValue)
            return (int)longValue;
        if (value.TryGetValue<double>(out var doubleValue) &&
            doubleValue is >= int.MinValue and <= int.MaxValue)
            return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
        if (value.TryGetValue<string>(out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            return integer;

        return null;
    }

    private static string? NodeString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;

        return null;
    }

    private static List<string> StringList(JsonNode? node)
    {
        var result = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                var text = NodeString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }
        }
        else
        {
            var text = NodeString(node);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }

    private static string CompactApiError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "원인 정보 없음";

        var compact = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }

    private sealed record LocalizedJsonDocuments(
        JsonObject English,
        JsonObject Korean);
}
