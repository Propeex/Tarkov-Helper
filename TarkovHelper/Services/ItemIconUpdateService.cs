using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Synchronizes locally displayed item icons with the URLs stored in the newly
/// rebuilt database. Existing valid files are reused and failed downloads never
/// delete a previously working icon.
/// </summary>
internal sealed class ItemIconUpdateService : IDisposable
{
    private const int MaxAttempts = 3;
    private const int MaxResponseBytes = 12 * 1024 * 1024;
    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<ItemIconUpdateService>();
    private readonly HttpClient _httpClient;
    private readonly Action<DatabaseBuildProgress> _report;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public ItemIconUpdateService(Action<DatabaseBuildProgress> report)
    {
        _report = report;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.8.5 content-updater");
    }

    public async Task<IconUpdateResult> SynchronizeAsync(
        string databasePath,
        string iconsPath,
        ContentUpdateManifest? previousManifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(iconsPath);
        var required = await LoadRequiredIconsAsync(databasePath, cancellationToken);
        var previousEntries = previousManifest?.Icons
            ?? new Dictionary<string, ContentIconManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var nextEntries = new ConcurrentDictionary<string, ContentIconManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();
        var downloaded = 0;
        var replaced = 0;
        var reused = 0;
        var completed = 0;

        Report("아이콘", "필요한 아이템 이미지 확인 중", 73, 0, required.Count);

        await Parallel.ForEachAsync(
            required,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                var targetPath = Path.Combine(
                    iconsPath,
                    ContentStorageService.SanitizeFileName(item.Id) + ".png");
                var hadValidFile = IsValidPng(targetPath);
                var canReuse = hadValidFile &&
                               previousEntries.TryGetValue(item.Id, out var previous) &&
                               string.Equals(previous.Url, item.Url, StringComparison.OrdinalIgnoreCase);

                if (canReuse)
                {
                    nextEntries[item.Id] = BuildManifestEntry(item.Url, targetPath);
                    Interlocked.Increment(ref reused);
                }
                else
                {
                    try
                    {
                        await DownloadAndConvertAsync(item.Url, targetPath, token);
                        nextEntries[item.Id] = BuildManifestEntry(item.Url, targetPath);
                        if (hadValidFile)
                            Interlocked.Increment(ref replaced);
                        else
                            Interlocked.Increment(ref downloaded);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var compact = CompactError(exception.Message);
                        failures.Add($"{item.Id}: {compact}");
                        Log.Warning($"Item icon update failed for {item.Id}: {compact}");

                        if (hadValidFile)
                        {
                            nextEntries[item.Id] = BuildManifestEntry(
                                previousEntries.TryGetValue(item.Id, out var old) ? old.Url : item.Url,
                                targetPath);
                            Interlocked.Increment(ref reused);
                        }
                    }
                }

                var current = Interlocked.Increment(ref completed);
                if (current == required.Count || current % 10 == 0)
                {
                    var fraction = required.Count == 0 ? 1 : current / (double)required.Count;
                    Report(
                        "아이콘",
                        $"아이템 이미지 동기화 중 · 신규 {Volatile.Read(ref downloaded):N0} · 교체 {Volatile.Read(ref replaced):N0}",
                        73 + fraction * 23,
                        current,
                        required.Count);
                }
            });

        var requiredIds = required
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var staleId in previousEntries.Keys.Where(id => !requiredIds.Contains(id)))
        {
            var stalePath = Path.Combine(
                iconsPath,
                ContentStorageService.SanitizeFileName(staleId) + ".png");
            try
            {
                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                    removed++;
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{staleId}: 사용하지 않는 아이콘 정리 실패 ({CompactError(exception.Message)})");
            }
        }

        var missing = required.Count(item => !nextEntries.ContainsKey(item.Id));
        return new IconUpdateResult(
            required.Count,
            downloaded,
            replaced,
            reused,
            removed,
            missing,
            nextEntries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<List<RequiredIcon>> LoadRequiredIconsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var result = new List<RequiredIcon>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT i.Id, i.IconUrl
            FROM Items i
            WHERE i.IconUrl IS NOT NULL
              AND TRIM(i.IconUrl) != ''
              AND (
                    EXISTS (
                        SELECT 1 FROM QuestRequiredItems q
                        WHERE q.ItemId = i.Id
                          AND LOWER(COALESCE(q.RequirementType, '')) != 'sellitem'
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM QuestRequiredItems q, json_each(q.AlternativeItemIds) alternative
                        WHERE q.IsAlternativeGroup = 1
                          AND alternative.value = i.Id
                          AND LOWER(COALESCE(q.RequirementType, '')) != 'sellitem'
                    )
                    OR EXISTS (
                        SELECT 1 FROM HideoutItemRequirements h
                        WHERE h.ItemId = i.BsgId OR h.ItemId = i.Id
                    )
                    OR EXISTS (
                        SELECT 1 FROM Ammo a
                        WHERE a.ItemId = i.BsgId OR a.ItemId = i.Id
                    )
              )
            ORDER BY i.Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var url = reader.GetString(1);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                result.Add(new RequiredIcon(id, url));
            }
        }

        return result;
    }

    private async Task DownloadAndConvertAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/*;q=0.8");
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                    throw new InvalidDataException("이미지 응답이 허용 크기를 초과했습니다.");

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.Length > MaxResponseBytes)
                    throw new InvalidDataException("이미지 응답이 비어 있거나 허용 크기를 초과했습니다.");

                using var bitmap = SKBitmap.Decode(bytes)
                    ?? throw new InvalidDataException("지원되지 않거나 손상된 이미지입니다.");
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidDataException("PNG 변환에 실패했습니다.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using (var output = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 useAsync: true))
                {
                    encoded.SaveTo(output);
                    await output.FlushAsync(cancellationToken);
                }

                if (!IsValidPng(tempPath))
                    throw new InvalidDataException("변환된 PNG 검증에 실패했습니다.");

                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                TryDelete(tempPath);
                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "아이콘 다운로드에 실패했습니다.", lastError);
    }

    private static bool IsValidPng(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 32)
                return false;
            using var bitmap = SKBitmap.Decode(path);
            return bitmap != null && bitmap.Width > 0 && bitmap.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static ContentIconManifestEntry BuildManifestEntry(string url, string path)
    {
        using var stream = File.OpenRead(path);
        return new ContentIconManifestEntry
        {
            Url = url,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            Size = stream.Length
        };
    }

    private void Report(string stage, string message, double percent, int current, int? total)
    {
        var elapsed = DateTime.UtcNow - _startedAt;
        TimeSpan? remaining = null;
        if (current > 0 && total is > 0 && current < total)
        {
            var secondsPerItem = elapsed.TotalSeconds / current;
            remaining = TimeSpan.FromSeconds(secondsPerItem * (total.Value - current));
        }

        _report(new DatabaseBuildProgress(stage, message, percent, current, total, elapsed, remaining));
    }

    private static string CompactError(string message)
    {
        var compact = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..177] + "...";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The next staging cleanup will retry.
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record RequiredIcon(string Id, string Url);
}

internal sealed record IconUpdateResult(
    int RequiredCount,
    int DownloadedCount,
    int ReplacedCount,
    int ReusedCount,
    int RemovedCount,
    int MissingCount,
    Dictionary<string, ContentIconManifestEntry> Entries,
    IReadOnlyList<string> Failures);
