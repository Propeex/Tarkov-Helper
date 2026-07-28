using System.Collections;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds the API-managed portion of tarkov_data.db from tarkov.dev.
/// The existing database is copied to a temporary file first so map assets,
/// hand-maintained coordinates, and unsupported columns remain intact.
/// </summary>
internal sealed partial class TarkovDataDatabaseBuilder
{
    private const string ApiUrl = "https://api.tarkov.dev/graphql";
    private const int PageSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<TarkovDataDatabaseBuilder>();

    private readonly HttpClient _httpClient;
    private readonly Action<DatabaseBuildProgress> _report;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private double _lastPercent;

    public TarkovDataDatabaseBuilder(
        HttpClient httpClient,
        Action<DatabaseBuildProgress> report)
    {
        _httpClient = httpClient;
        _report = report;
    }

    public async Task<DatabaseBuildResult> BuildAsync(
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

        CleanupFile(tempPath);
        SqliteConnection.ClearAllPools();
        File.Copy(databasePath, tempPath, overwrite: true);

        try
        {
            Report("API", "영문 아이템 데이터를 받는 중", 1, 0, null);
            var itemsEn = await FetchItemsAsync("en", 1, 13, cancellationToken);

            Report("API", "한국어 아이템 데이터를 받는 중", 13, 0, null);
            var itemsKo = await FetchItemsAsync("ko", 13, 24, cancellationToken);

            Report("API", "영문 퀘스트 데이터를 받는 중", 24, 0, null);
            var tasksEn = await FetchTasksAsync("en", 24, 38, cancellationToken);

            Report("API", "한국어 퀘스트 데이터를 받는 중", 38, 0, null);
            var tasksKo = await FetchTasksAsync("ko", 38, 50, cancellationToken);

            Report("API", "영문 은신처 데이터를 받는 중", 50, 0, null);
            var hideoutEn = await FetchHideoutAsync("en", 50, 58, cancellationToken);

            Report("API", "한국어 은신처 데이터를 받는 중", 58, 0, null);
            var hideoutKo = await FetchHideoutAsync("ko", 58, 65, cancellationToken);

            var data = MergeApiData(itemsEn, itemsKo, tasksEn, tasksKo, hideoutEn, hideoutKo);

            Report("DB", "기존 데이터베이스 구조를 확인하는 중", 65, 0, null);
            var counts = await RewriteDatabaseAsync(tempPath, data, cancellationToken);

            Report("검증", "아이템 연결과 데이터 무결성을 검사하는 중", 93, 0, null);
            await ValidateDatabaseAsync(tempPath, counts, cancellationToken);

            Report("교체", "검증된 데이터베이스로 교체하는 중", 98, 0, null);
            ReplaceDatabaseAtomically(tempPath, databasePath, backupPath);

            Report("완료", "데이터베이스 생성 완료", 100, counts.TotalRows, counts.TotalRows);
            return new DatabaseBuildResult(
                counts.Items,
                counts.Quests,
                counts.QuestRequiredItems,
                counts.HideoutStations,
                counts.HideoutItemRequirements,
                backupPath);
        }
        catch
        {
            CleanupFile(tempPath);
            throw;
        }
    }

    private async Task<List<ApiItem>> FetchItemsAsync(
        string language,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        const string query = """
            query Items($limit: Int!, $offset: Int!) {
              items(lang: LANGUAGE, gameMode: regular, limit: $limit, offset: $offset) {
                id
                name
                normalizedName
                shortName
                description
                iconLink
                wikiLink
                category { name normalizedName }
                categories { name normalizedName }
              }
            }
            """;

        return await FetchPagedAsync<ApiItem>(
            query.Replace("LANGUAGE", language, StringComparison.Ordinal),
            "items",
            language == "ko" ? "한국어 아이템" : "영문 아이템",
            phaseStart,
            phaseEnd,
            cancellationToken);
    }

    private async Task<List<ApiTask>> FetchTasksAsync(
        string language,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        const string query = """
            query Tasks($limit: Int!, $offset: Int!) {
              tasks(lang: LANGUAGE, gameMode: regular, limit: $limit, offset: $offset) {
                id
                name
                normalizedName
                wikiLink
                minPlayerLevel
                factionName
                kappaRequired
                trader { id name normalizedName }
                map { id name normalizedName }
                requiredPrestige { prestigeLevel }
                taskRequirements {
                  task { id }
                  status
                }
                objectives {
                  __typename
                  id
                  type
                  description
                  optional
                  maps { id name normalizedName }
                  ... on TaskObjectiveItem {
                    items { id name normalizedName iconLink }
                    count
                    foundInRaid
                    dogTagLevel
                  }
                }
              }
            }
            """;

        return await FetchPagedAsync<ApiTask>(
            query.Replace("LANGUAGE", language, StringComparison.Ordinal),
            "tasks",
            language == "ko" ? "한국어 퀘스트" : "영문 퀘스트",
            phaseStart,
            phaseEnd,
            cancellationToken);
    }

    private async Task<List<ApiHideoutStation>> FetchHideoutAsync(
        string language,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        const string query = """
            query Hideout($limit: Int!, $offset: Int!) {
              hideoutStations(lang: LANGUAGE, gameMode: regular, limit: $limit, offset: $offset) {
                id
                name
                normalizedName
                imageLink
                levels {
                  id
                  level
                  constructionTime
                  itemRequirements {
                    item { id name normalizedName iconLink }
                    count
                    quantity
                  }
                  stationLevelRequirements {
                    station { id name normalizedName }
                    level
                  }
                  traderRequirements {
                    trader { id name normalizedName }
                    requirementType
                    compareMethod
                    value
                    level
                  }
                  skillRequirements {
                    name
                    skill { id name }
                    level
                  }
                }
              }
            }
            """;

        return await FetchPagedAsync<ApiHideoutStation>(
            query.Replace("LANGUAGE", language, StringComparison.Ordinal),
            "hideoutStations",
            language == "ko" ? "한국어 은신처" : "영문 은신처",
            phaseStart,
            phaseEnd,
            cancellationToken);
    }

    private async Task<List<T>> FetchPagedAsync<T>(
        string query,
        string dataProperty,
        string label,
        double phaseStart,
        double phaseEnd,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        var offset = 0;
        var page = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageStartFraction = PageProgress(page);
            var pageEndFraction = PageProgress(page + 1);

            var payload = JsonSerializer.Serialize(new
            {
                query,
                variables = new { limit = PageSize, offset }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var memory = new MemoryStream();
            var totalBytes = response.Content.Headers.ContentLength;
            var buffer = new byte[64 * 1024];
            long bytesReadTotal = 0;

            while (true)
            {
                var read = await responseStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesReadTotal += read;

                var byteFraction = totalBytes is > 0
                    ? Math.Clamp(bytesReadTotal / (double)totalBytes.Value, 0, 1)
                    : Math.Clamp(1 - Math.Exp(-bytesReadTotal / 1_500_000d), 0, 0.9);
                var pageFraction = pageStartFraction + (pageEndFraction - pageStartFraction) * byteFraction;
                var percent = phaseStart + (phaseEnd - phaseStart) * pageFraction;
                Report("API", $"{label} 데이터를 받는 중", percent, result.Count, null);
            }

            memory.Position = 0;
            using var document = await JsonDocument.ParseAsync(memory, cancellationToken: cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(dataProperty, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"tarkov.dev 응답에 {dataProperty} 배열이 없습니다.");
            }

            var pageItems = JsonSerializer.Deserialize<List<T>>(array.GetRawText(), JsonOptions) ?? [];
            result.AddRange(pageItems);
            page++;

            var percentAfterPage = phaseStart + (phaseEnd - phaseStart) * PageProgress(page);
            Report("API", $"{label} {result.Count:N0}개 수신", percentAfterPage, result.Count, null);

            if (pageItems.Count < PageSize)
                break;

            offset += pageItems.Count;
        }

        Report("API", $"{label} {result.Count:N0}개 수신 완료", phaseEnd, result.Count, result.Count);
        return result;
    }

    private static double PageProgress(int completedPages)
    {
        if (completedPages <= 0)
            return 0;
        return Math.Min(0.94, 1 - 1d / (completedPages + 1));
    }

    private static void ThrowIfGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return;

        var messages = errors.EnumerateArray()
            .Select(error => error.TryGetProperty("message", out var message)
                ? message.GetString()
                : error.ToString())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        throw new InvalidDataException(
            messages.Count == 0
                ? "tarkov.dev API가 오류를 반환했습니다."
                : $"tarkov.dev API 오류: {string.Join(" | ", messages)}");
    }

    private static MergedApiData MergeApiData(
        IEnumerable<ApiItem> itemsEn,
        IEnumerable<ApiItem> itemsKo,
        IEnumerable<ApiTask> tasksEn,
        IEnumerable<ApiTask> tasksKo,
        IEnumerable<ApiHideoutStation> hideoutEn,
        IEnumerable<ApiHideoutStation> hideoutKo)
    {
        var koItems = UniqueById(itemsKo);
        var koTasks = UniqueById(tasksKo);
        var koHideout = UniqueById(hideoutKo);

        return new MergedApiData(
            UniqueById(itemsEn).Values
                .Select(item => new LocalizedItem(item, koItems.GetValueOrDefault(item.Id)))
                .OrderBy(item => item.English.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UniqueById(tasksEn).Values
                .Select(task => new LocalizedTask(task, koTasks.GetValueOrDefault(task.Id)))
                .OrderBy(task => task.English.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UniqueById(hideoutEn).Values
                .Select(station => new LocalizedHideoutStation(station, koHideout.GetValueOrDefault(station.Id)))
                .OrderBy(station => station.English.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static Dictionary<string, T> UniqueById<T>(IEnumerable<T> values) where T : IApiEntity
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value.Id))
                result[value.Id] = value;
        }
        return result;
    }
}
