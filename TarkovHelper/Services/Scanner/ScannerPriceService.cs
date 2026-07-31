using System.Net.Http;
using System.Text.Json;
using TarkovHelper.Debug;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services.Scanner;

/// <summary>
/// tarkov.dev 정적 JSON 데이터에서 RatScanner와 동일한 기준의 플리마켓 평균가와
/// 최고 상인 판매가를 읽습니다. 마지막 정상 응답은 로컬 캐시에 보존합니다.
/// </summary>
internal sealed class ScannerPriceService
{
    private const string ItemsEndpoint = "https://json.tarkov.dev/regular/items";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly IReadOnlyDictionary<string, string> TraderNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["54cb50c76803fa8b248b4571"] = "프라퍼",
            ["54cb57776803fa99248b456e"] = "테라피스트",
            ["579dc571d53a0658a154fbec"] = "펜스",
            ["58330581ace78e27b8b10cee"] = "스키어",
            ["5935c25fb3acc3127c3d8cd9"] = "피스키퍼",
            ["5a7c2eca46aef81a7ca2145d"] = "메카닉",
            ["5ac3b934156ae10c4430e83c"] = "라그맨",
            ["5c0647fdd443bc2504c2d371"] = "예거",
            ["6617beeaa9cfa777ca915b7c"] = "라이트키퍼",
            ["656f0f98d80a697f855d34b1"] = "리플렉스"
        };

    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<ScannerPriceService>();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<string, ScannerPriceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt;
    private bool _cacheLoaded;

    private static string CachePath => Path.Combine(AppEnv.CachePath, "scanner-prices.json");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadCacheAsync(cancellationToken);
        _ = RefreshAsync(force: false, CancellationToken.None);
    }

    public ScannerPriceEntry? Get(string itemId)
    {
        return _entries.TryGetValue(itemId, out var entry) ? entry : null;
    }

    public void RequestRefresh()
    {
        _ = RefreshAsync(force: true, CancellationToken.None);
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (_cacheLoaded)
            return;

        _cacheLoaded = true;
        try
        {
            if (!File.Exists(CachePath))
                return;

            await using var stream = File.OpenRead(CachePath);
            var cache = await JsonSerializer.DeserializeAsync<ScannerPriceCache>(stream, cancellationToken: cancellationToken);
            if (cache?.Items == null || cache.Items.Count == 0)
                return;

            _entries = new Dictionary<string, ScannerPriceEntry>(cache.Items, StringComparer.OrdinalIgnoreCase);
            _loadedAt = cache.UpdatedAt;
            Log.Info($"Loaded scanner price cache: {_entries.Count} items");
        }
        catch (Exception ex)
        {
            Log.Warning($"Unable to load scanner price cache: {ex.Message}");
        }
    }

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await LoadCacheAsync(cancellationToken);
        if (!force && _entries.Count > 0 && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime)
            return;

        if (!await _refreshGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            using var response = await HttpClient.GetAsync(ItemsEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("tarkov.dev 아이템 가격 데이터 형식이 올바르지 않습니다.");
            }

            var refreshed = new Dictionary<string, ScannerPriceEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in items.EnumerateObject())
            {
                var item = property.Value;
                var id = ReadString(item, "id") ?? property.Name;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var width = Math.Max(1, ReadInt(item, "width") ?? 1);
                var height = Math.Max(1, ReadInt(item, "height") ?? 1);
                var average = PositiveOrNull(ReadInt(item, "avg24hPrice"));
                var updatedAt = ReadDate(item, "updated");

                int? bestPrice = null;
                string? bestTrader = null;
                if (item.TryGetProperty("sellToTrader", out var sales) && sales.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sale in sales.EnumerateArray())
                    {
                        var price = PositiveOrNull(ReadInt(sale, "priceRUB"));
                        if (!price.HasValue || (bestPrice.HasValue && price.Value <= bestPrice.Value))
                            continue;

                        bestPrice = price;
                        var traderId = ReadString(sale, "trader");
                        bestTrader = traderId != null && TraderNames.TryGetValue(traderId, out var localized)
                            ? localized
                            : "상인";
                    }
                }

                refreshed[id] = new ScannerPriceEntry(
                    average,
                    average.HasValue ? Math.Max(1, average.Value / (width * height)) : null,
                    bestTrader,
                    bestPrice,
                    updatedAt);
            }

            if (refreshed.Count < 1000)
                throw new InvalidDataException($"가격 데이터가 예상보다 적습니다: {refreshed.Count}");

            _entries = refreshed;
            _loadedAt = DateTimeOffset.UtcNow;
            await SaveCacheAsync(cancellationToken);
            Log.Info($"Refreshed scanner prices: {refreshed.Count} items");
        }
        catch (Exception ex)
        {
            // 가격은 부가 정보입니다. 마지막 정상 캐시를 유지하고 스캐너 자체는 중단하지 않습니다.
            Log.Warning($"Unable to refresh scanner prices; using cached data: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(AppEnv.CachePath);
            var tempPath = CachePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ScannerPriceCache(_loadedAt, _entries),
                    cancellationToken: cancellationToken);
            }
            File.Move(tempPath, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Unable to save scanner price cache: {ex.Message}");
        }
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string propertyName)
    {
        var text = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private sealed record ScannerPriceCache(
        DateTimeOffset UpdatedAt,
        Dictionary<string, ScannerPriceEntry> Items);
}

internal sealed record ScannerPriceEntry(
    int? AverageFleaPrice,
    int? FleaPricePerSlot,
    string? BestTraderName,
    int? BestTraderPrice,
    DateTimeOffset? UpdatedAt);
