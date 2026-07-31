using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;

Console.OutputEncoding = Encoding.UTF8;
return await IconDownloader.RunAsync(args);

internal static class IconDownloader
{
    private const int MaxAttempts = 3;
    private static readonly object ConsoleLock = new();

    public static async Task<int> RunAsync(string[] args)
    {
        DownloadOptions options;
        try
        {
            options = DownloadOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"인수 오류: {exception.Message}");
            DownloadOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            DownloadOptions.PrintHelp();
            return 0;
        }

        try
        {
            var databasePath = ResolveDatabasePath(options.DatabasePath);
            var outputPath = Path.GetFullPath(options.OutputPath ??
                Path.Combine(Path.GetDirectoryName(databasePath)!, "icons"));
            Directory.CreateDirectory(outputPath);

            var items = await LoadItemsAsync(databasePath, options.DownloadAll);
            if (items.Count == 0)
            {
                Console.WriteLine("다운로드할 아이콘이 없습니다.");
                return 0;
            }

            Console.WriteLine($"데이터베이스: {databasePath}");
            Console.WriteLine($"저장 폴더: {outputPath}");
            Console.WriteLine($"대상: {(options.DownloadAll ? "전체 아이템" : "퀘스트·은신처·탄약 아이템")} {items.Count:N0}개");
            Console.WriteLine($"동시 다운로드: {options.Concurrency}개");

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = options.Concurrency,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper-IconDownloader/1.0");

            var completed = 0;
            var downloaded = 0;
            var skipped = 0;
            var failed = 0;
            var failures = new ConcurrentBag<string>();

            try
            {
                await Parallel.ForEachAsync(
                    items,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Concurrency,
                        CancellationToken = cancellation.Token
                    },
                    async (item, token) =>
                    {
                        var targetPath = Path.Combine(outputPath, SanitizeFileName(item.Id) + ".png");
                        try
                        {
                            if (!options.Force && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                            {
                                Interlocked.Increment(ref skipped);
                            }
                            else
                            {
                                await DownloadAndConvertAsync(httpClient, item, targetPath, token);
                                Interlocked.Increment(ref downloaded);
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            Interlocked.Increment(ref failed);
                            failures.Add($"{item.Id}\t{item.IconUrl}\t{CompactError(exception.Message)}");
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref completed);
                            if (current == items.Count || current % 10 == 0)
                            {
                                lock (ConsoleLock)
                                {
                                    Console.WriteLine(
                                        $"[{current:N0}/{items.Count:N0}] 다운로드 {Volatile.Read(ref downloaded):N0} · " +
                                        $"건너뜀 {Volatile.Read(ref skipped):N0} · 실패 {Volatile.Read(ref failed):N0}");
                                }
                            }
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("사용자 요청으로 아이콘 다운로드를 중단했습니다. 다시 실행하면 완료된 파일은 건너뜁니다.");
                await WriteFailureLogAsync(outputPath, failures);
                return 130;
            }

            await WriteFailureLogAsync(outputPath, failures);

            Console.WriteLine();
            Console.WriteLine($"완료: 다운로드 {downloaded:N0}개, 기존 파일 {skipped:N0}개, 실패 {failed:N0}개");
            Console.WriteLine($"아이콘 폴더: {outputPath}");

            if (failed > 0)
            {
                Console.WriteLine("실패 목록은 icon-download-failures.txt에 저장했습니다. 같은 명령을 다시 실행하면 실패 항목을 재시도합니다.");
                return 1;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"아이콘 다운로드 도구 실행 실패: {exception.Message}");
            return 1;
        }
    }

    private static async Task<List<DownloadItem>> LoadItemsAsync(string databasePath, bool downloadAll)
    {
        var result = new List<DownloadItem>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = downloadAll
            ? """
              SELECT DISTINCT Id, IconUrl
              FROM Items
              WHERE IconUrl IS NOT NULL AND TRIM(IconUrl) != ''
              ORDER BY Id;
              """
            : """
              SELECT DISTINCT i.Id, i.IconUrl
              FROM Items i
              WHERE i.IconUrl IS NOT NULL
                AND TRIM(i.IconUrl) != ''
                AND (
                    EXISTS (
                        SELECT 1
                        FROM QuestRequiredItems q
                        WHERE q.ItemId = i.Id
                          AND LOWER(COALESCE(q.RequirementType, '')) != 'sellitem'
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM HideoutItemRequirements h
                        WHERE h.ItemId = i.BsgId
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM Ammo a
                        WHERE a.ItemId = i.BsgId OR a.ItemId = i.Id
                    )
                )
              ORDER BY i.Id;
              """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

            var id = reader.GetString(0);
            var iconUrl = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(id) && Uri.TryCreate(iconUrl, UriKind.Absolute, out _))
                result.Add(new DownloadItem(id, iconUrl));
        }

        return result;
    }

    private static async Task DownloadAndConvertAsync(
        HttpClient httpClient,
        DownloadItem item,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, item.IconUrl);
                request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/*;q=0.8");

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                        null,
                        response.StatusCode);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0)
                    throw new InvalidDataException("빈 이미지 응답입니다.");

                using var image = Image.Load(bytes);
                await using (var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await image.SaveAsPngAsync(output, cancellationToken);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CleanupFile(tempPath);
                throw;
            }
            catch (Exception exception)
            {
                CleanupFile(tempPath);
                lastException = exception;
                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{item.Id} 아이콘을 {MaxAttempts}회 시도했지만 받지 못했습니다.",
            lastException);
    }

    private static string ResolveDatabasePath(string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var explicitPath = Path.GetFullPath(requestedPath);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException("지정한 tarkov_data.db를 찾을 수 없습니다.", explicitPath);
            return explicitPath;
        }

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            var releaseDatabase = Path.Combine(
                directory.FullName,
                "TarkovHelper",
                "bin",
                "Release",
                "net8.0-windows",
                "Assets",
                "tarkov_data.db");
            if (File.Exists(releaseDatabase))
                return Path.GetFullPath(releaseDatabase);

            var sourceDatabase = Path.Combine(directory.FullName, "TarkovHelper", "Assets", "tarkov_data.db");
            if (File.Exists(sourceDatabase))
                return Path.GetFullPath(sourceDatabase);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "tarkov_data.db를 자동으로 찾지 못했습니다. --database 옵션으로 경로를 지정하십시오.");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        return builder.ToString();
    }

    private static async Task WriteFailureLogAsync(string outputPath, IEnumerable<string> failures)
    {
        var logPath = Path.Combine(outputPath, "icon-download-failures.txt");
        var lines = failures.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (lines.Length == 0)
        {
            CleanupFile(logPath);
            return;
        }

        await File.WriteAllLinesAsync(logPath, lines, Encoding.UTF8);
    }

    private static void CleanupFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A later run can clean up an abandoned temporary file.
        }
    }

    private static string CompactError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "알 수 없는 오류";

        var compact = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }
}

internal sealed record DownloadItem(string Id, string IconUrl);

internal sealed class DownloadOptions
{
    public string? DatabasePath { get; private set; }
    public string? OutputPath { get; private set; }
    public bool DownloadAll { get; private set; }
    public bool Force { get; private set; }
    public bool ShowHelp { get; private set; }
    public int Concurrency { get; private set; } = 8;

    public static DownloadOptions Parse(string[] args)
    {
        var options = new DownloadOptions();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--database":
                    options.DatabasePath = ReadValue(args, ref index, "--database");
                    break;
                case "--output":
                    options.OutputPath = ReadValue(args, ref index, "--output");
                    break;
                case "--all":
                    options.DownloadAll = true;
                    break;
                case "--force":
                    options.Force = true;
                    break;
                case "--concurrency":
                    var value = ReadValue(args, ref index, "--concurrency");
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency) ||
                        concurrency is < 1 or > 32)
                    {
                        throw new ArgumentException("--concurrency 값은 1~32 사이의 정수여야 합니다.");
                    }
                    options.Concurrency = concurrency;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"알 수 없는 옵션입니다: {args[index]}");
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Tarkov Helper 아이템 아이콘 다운로드 도구");
        Console.WriteLine();
        Console.WriteLine("사용법:");
        Console.WriteLine("  dotnet run --project TarkovHelper.IconDownloader --configuration Release -- [옵션]");
        Console.WriteLine();
        Console.WriteLine("옵션:");
        Console.WriteLine("  --database <경로>     사용할 tarkov_data.db 경로");
        Console.WriteLine("  --output <폴더>       PNG 저장 폴더. 기본값은 DB 옆의 icons 폴더");
        Console.WriteLine("  --all                 필요 아이템뿐 아니라 Items 전체를 다운로드");
        Console.WriteLine("  --force               이미 존재하는 PNG도 다시 다운로드");
        Console.WriteLine("  --concurrency <1~32>  동시 다운로드 수. 기본값 8");
        Console.WriteLine("  --help                도움말 표시");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"{option} 뒤에 값이 필요합니다.");

        index++;
        return args[index];
    }
}
