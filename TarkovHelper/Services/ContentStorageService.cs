using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Owns mutable reference content downloaded by the installed application.
/// Bundled assets remain an offline-safe seed; successful updates are stored
/// under LocalAppData so replacing the application folder does not discard them.
/// </summary>
public sealed class ContentStorageService
{
    public const int CurrentManifestSchemaVersion = 1;
    private const string DatabaseFileName = "tarkov_data.db";
    private const string ManifestFileName = "content_manifest.json";
    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<ContentStorageService>();
    private static readonly Lazy<ContentStorageService> LazyInstance = new(() => new ContentStorageService());
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private bool _initialized;
    private bool _usingBundledFallback;
    private Exception? _initializationError;

    public static ContentStorageService Instance => LazyInstance.Value;

    public string RootPath { get; }
    public string CurrentPath => Path.Combine(RootPath, "current");
    public string StagingPath => Path.Combine(RootPath, "staging");
    public string PreviousPath => Path.Combine(RootPath, "previous");
    public string BundledAssetsPath { get; }
    public string BundledDatabasePath => Path.Combine(BundledAssetsPath, DatabaseFileName);
    public string BundledIconsPath => Path.Combine(BundledAssetsPath, "Icons");
    public string DatabasePath => _usingBundledFallback
        ? BundledDatabasePath
        : Path.Combine(CurrentPath, DatabaseFileName);
    public string IconsPath => _usingBundledFallback
        ? BundledIconsPath
        : Path.Combine(CurrentPath, "Icons");
    public string ManifestPath => Path.Combine(CurrentPath, ManifestFileName);
    public bool IsUsingBundledFallback => _usingBundledFallback;
    public bool HasPreviousContent => !_usingBundledFallback && File.Exists(Path.Combine(PreviousPath, DatabaseFileName));
    public string? InitializationError => _initializationError?.Message;

    private ContentStorageService()
    {
        BundledAssetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        var overrideRoot = Environment.GetEnvironmentVariable("TARKOV_CONTENT_ROOT");
        RootPath = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TarkovHelper",
                "Content")
            : Path.GetFullPath(overrideRoot);

        EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized)
                return;

            try
            {
                if (!File.Exists(BundledDatabasePath))
                    throw new FileNotFoundException("릴리스 기본 데이터베이스를 찾을 수 없습니다.", BundledDatabasePath);

                Directory.CreateDirectory(RootPath);
                EnsureCurrentSeeded();
                CleanupInterruptedStaging();
                _usingBundledFallback = false;
                _initialized = true;
            }
            catch (Exception exception)
            {
                _initializationError = exception;
                _usingBundledFallback = true;
                _initialized = true;
                Log.Error("Failed to initialize mutable content storage; bundled content will be used.", exception);
            }
        }
    }

    /// <summary>
    /// Creates an isolated update workspace from the current valid content set.
    /// The active content is not changed until CommitStaging is called.
    /// </summary>
    public ContentStagingPaths PrepareStaging()
    {
        lock (_gate)
        {
            EnsureWritable();
            DeleteDirectoryIfExists(StagingPath);
            Directory.CreateDirectory(StagingPath);

            var stagingDatabase = Path.Combine(StagingPath, DatabaseFileName);
            var stagingIcons = Path.Combine(StagingPath, "Icons");
            var stagingManifest = Path.Combine(StagingPath, ManifestFileName);

            File.Copy(DatabasePath, stagingDatabase, overwrite: true);
            CopyDirectory(IconsPath, stagingIcons, overwrite: true);
            if (File.Exists(ManifestPath))
                File.Copy(ManifestPath, stagingManifest, overwrite: true);

            return new ContentStagingPaths(StagingPath, stagingDatabase, stagingIcons, stagingManifest);
        }
    }

    /// <summary>
    /// Replaces the active content directory with a fully prepared staging set.
    /// If the final move fails, the previous active set is restored immediately.
    /// </summary>
    public void CommitStaging()
    {
        lock (_gate)
        {
            EnsureWritable();
            var stagedDatabase = Path.Combine(StagingPath, DatabaseFileName);
            if (!File.Exists(stagedDatabase))
                throw new InvalidDataException("스테이징 데이터베이스가 없어 업데이트를 적용할 수 없습니다.");

            ValidateDatabaseFile(stagedDatabase);
            ValidateManifestCompatibility(Path.Combine(StagingPath, ManifestFileName));
            Directory.CreateDirectory(Path.Combine(StagingPath, "Icons"));

            SqliteConnection.ClearAllPools();
            DeleteDirectoryIfExists(PreviousPath);

            var currentMoved = false;
            try
            {
                if (Directory.Exists(CurrentPath))
                {
                    Directory.Move(CurrentPath, PreviousPath);
                    currentMoved = true;
                }

                Directory.Move(StagingPath, CurrentPath);
            }
            catch
            {
                try
                {
                    if (!Directory.Exists(CurrentPath) && currentMoved && Directory.Exists(PreviousPath))
                        Directory.Move(PreviousPath, CurrentPath);
                }
                catch (Exception rollbackException)
                {
                    Log.Error("Content rollback failed after commit error.", rollbackException);
                }

                throw;
            }
        }
    }

    public async Task ResetToBundledAsync(CancellationToken cancellationToken = default)
    {
        ContentStagingPaths staging;
        lock (_gate)
        {
            EnsureWritable();
            DeleteDirectoryIfExists(StagingPath);
            Directory.CreateDirectory(StagingPath);
            staging = new ContentStagingPaths(
                StagingPath,
                Path.Combine(StagingPath, DatabaseFileName),
                Path.Combine(StagingPath, "Icons"),
                Path.Combine(StagingPath, ManifestFileName));

            File.Copy(BundledDatabasePath, staging.DatabasePath, overwrite: true);
            CopyDirectory(BundledIconsPath, staging.IconsPath, overwrite: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manifest = await CreateManifestFromContentAsync(
            staging.DatabasePath,
            staging.IconsPath,
            source: "bundled-release",
            cancellationToken);
        await SaveManifestAsync(staging.ManifestPath, manifest, cancellationToken);
        CommitStaging();
    }


    public void DiscardStaging()
    {
        lock (_gate)
        {
            try
            {
                DeleteDirectoryIfExists(StagingPath);
            }
            catch (Exception exception)
            {
                Log.Warning($"Failed to discard staging content: {exception.Message}");
            }
        }
    }

    public void RestorePrevious()
    {
        lock (_gate)
        {
            EnsureWritable();
            var previousDatabase = Path.Combine(PreviousPath, DatabaseFileName);
            if (!File.Exists(previousDatabase))
                throw new InvalidOperationException("복구할 이전 콘텐츠가 없습니다.");

            ValidateDatabaseFile(previousDatabase);
            var previousManifest = Path.Combine(PreviousPath, ManifestFileName);
            ValidateManifestCompatibility(previousManifest);
            ValidateManifestDatabaseHash(previousManifest, previousDatabase);
            var swapPath = Path.Combine(RootPath, "restore-swap");
            DeleteDirectoryIfExists(swapPath);
            SqliteConnection.ClearAllPools();

            var currentMoved = false;
            var previousMoved = false;
            try
            {
                Directory.Move(CurrentPath, swapPath);
                currentMoved = true;
                Directory.Move(PreviousPath, CurrentPath);
                previousMoved = true;
                Directory.Move(swapPath, PreviousPath);
            }
            catch (Exception exception)
            {
                try
                {
                    // If only the final move failed, the requested restore already
                    // became active. Retry preserving the old current set as the next
                    // rollback target and treat the operation as successful.
                    if (previousMoved && Directory.Exists(CurrentPath) && Directory.Exists(swapPath))
                    {
                        if (Directory.Exists(PreviousPath))
                            DeleteDirectoryIfExists(PreviousPath);
                        Directory.Move(swapPath, PreviousPath);
                        Log.Warning($"Previous content restore required a recovery move: {exception.Message}");
                        return;
                    }

                    if (currentMoved && !previousMoved &&
                        !Directory.Exists(CurrentPath) && Directory.Exists(swapPath))
                    {
                        Directory.Move(swapPath, CurrentPath);
                    }
                }
                catch (Exception rollbackException)
                {
                    Log.Error("Previous content restore rollback failed.", rollbackException);
                }

                throw;
            }
        }
    }

    public ContentUpdateManifest? LoadCurrentManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
                return null;

            var manifest = JsonSerializer.Deserialize<ContentUpdateManifest>(
                File.ReadAllText(ManifestPath),
                _jsonOptions);
            if (manifest == null)
                return null;

            manifest.Icons = new Dictionary<string, ContentIconManifestEntry>(
                manifest.Icons ?? new Dictionary<string, ContentIconManifestEntry>(),
                StringComparer.OrdinalIgnoreCase);
            manifest.Warnings ??= new List<string>();
            return manifest;
        }
        catch (Exception exception)
        {
            Log.Warning($"Failed to read content manifest: {exception.Message}");
            return null;
        }
    }

    public async Task SaveManifestAsync(
        string path,
        ContentUpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("콘텐츠 매니페스트 경로를 확인할 수 없습니다.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(manifest, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public long GetDownloadedContentSize()
    {
        if (_usingBundledFallback || !Directory.Exists(CurrentPath))
            return 0;

        return GetDirectorySize(CurrentPath);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void EnsureCurrentSeeded()
    {
        var currentDatabase = Path.Combine(CurrentPath, DatabaseFileName);
        var currentIcons = Path.Combine(CurrentPath, "Icons");
        var currentManifest = Path.Combine(CurrentPath, ManifestFileName);

        if (File.Exists(currentDatabase))
        {
            try
            {
                ValidateDatabaseFile(currentDatabase);
                if (File.Exists(currentManifest))
                {
                    ValidateManifestCompatibility(currentManifest);
                    ValidateManifestDatabaseHash(currentManifest, currentDatabase);
                }
            }
            catch (Exception exception)
            {
                Log.Warning($"Active content is invalid or incompatible and will be recovered: {exception.Message}");
                RecoverCurrentFromPreviousOrBundled();
            }
        }

        if (!File.Exists(currentDatabase))
            RecoverCurrentFromPreviousOrBundled();

        Directory.CreateDirectory(CurrentPath);
        if (!File.Exists(currentDatabase))
            File.Copy(BundledDatabasePath, currentDatabase, overwrite: false);

        CopyDirectory(BundledIconsPath, currentIcons, overwrite: false);
        ValidateDatabaseFile(currentDatabase);

        if (!File.Exists(currentManifest))
        {
            var manifest = CreateManifestFromContentAsync(
                    currentDatabase,
                    currentIcons,
                    source: "bundled-release",
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            SaveManifestAsync(currentManifest, manifest).GetAwaiter().GetResult();
        }
    }

    private void CleanupInterruptedStaging()
    {
        // An interrupted update must never become active implicitly. The current
        // directory is the only authoritative set until an explicit commit succeeds.
        DeleteDirectoryIfExists(StagingPath);
    }

    private void EnsureWritable()
    {
        EnsureInitialized();
        if (_usingBundledFallback)
        {
            throw new InvalidOperationException(
                "다운로드 콘텐츠 저장소를 사용할 수 없습니다. " +
                (_initializationError?.Message ?? "LocalAppData 경로를 확인해 주세요."));
        }
    }

    private static void ValidateDatabaseFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 128 * 1024)
            throw new InvalidDataException("콘텐츠 데이터베이스가 없거나 비정상적으로 작습니다.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var integrity = command.ExecuteScalar()?.ToString();
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"콘텐츠 데이터베이스 무결성 검사 실패: {integrity}");
    }

    private void RecoverCurrentFromPreviousOrBundled()
    {
        var previousDatabase = Path.Combine(PreviousPath, DatabaseFileName);
        var previousManifest = Path.Combine(PreviousPath, ManifestFileName);
        var previousUsable = false;
        if (File.Exists(previousDatabase))
        {
            try
            {
                ValidateDatabaseFile(previousDatabase);
                ValidateManifestCompatibility(previousManifest);
                ValidateManifestDatabaseHash(previousManifest, previousDatabase);
                previousUsable = true;
            }
            catch (Exception exception)
            {
                Log.Warning($"Previous content is also unusable: {exception.Message}");
            }
        }

        DeleteDirectoryIfExists(CurrentPath);
        if (previousUsable)
        {
            Directory.Move(PreviousPath, CurrentPath);
            Log.Warning("Recovered active content from the previous validated set.");
            return;
        }

        DeleteDirectoryIfExists(PreviousPath);
        Directory.CreateDirectory(CurrentPath);
        File.Copy(BundledDatabasePath, Path.Combine(CurrentPath, DatabaseFileName), overwrite: false);
        CopyDirectory(BundledIconsPath, Path.Combine(CurrentPath, "Icons"), overwrite: false);
        Log.Warning("Recovered active content from the bundled release seed.");
    }

    private void ValidateManifestCompatibility(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("콘텐츠 매니페스트가 없습니다.");

        var manifest = JsonSerializer.Deserialize<ContentUpdateManifest>(
            File.ReadAllText(path),
            _jsonOptions)
            ?? throw new InvalidDataException("콘텐츠 매니페스트를 읽을 수 없습니다.");
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            throw new InvalidDataException(
                $"지원하지 않는 콘텐츠 스키마입니다: {manifest.SchemaVersion} " +
                $"(지원 {CurrentManifestSchemaVersion})");
        }
    }

    private void ValidateManifestDatabaseHash(string manifestPath, string databasePath)
    {
        var manifest = JsonSerializer.Deserialize<ContentUpdateManifest>(
            File.ReadAllText(manifestPath),
            _jsonOptions)
            ?? throw new InvalidDataException("콘텐츠 매니페스트를 읽을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(manifest.DatabaseSha256))
            return;

        var actual = ComputeSha256(databasePath);
        if (!string.Equals(actual, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("콘텐츠 데이터베이스와 매니페스트 해시가 일치하지 않습니다.");
    }

    private static async Task<ContentUpdateManifest> CreateManifestFromContentAsync(
        string databasePath,
        string iconsPath,
        string source,
        CancellationToken cancellationToken)
    {
        var summary = await ContentDatabaseSummary.ReadAsync(databasePath, cancellationToken);
        var icons = new Dictionary<string, ContentIconManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, IconUrl FROM Items WHERE IconUrl IS NOT NULL AND TRIM(IconUrl) != '';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var url = reader.GetString(1);
            var filePath = Path.Combine(iconsPath, SanitizeFileName(id) + ".png");
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                continue;

            icons[id] = new ContentIconManifestEntry
            {
                Url = url,
                Sha256 = ComputeSha256(filePath),
                Size = new FileInfo(filePath).Length
            };
        }

        return new ContentUpdateManifest
        {
            SchemaVersion = CurrentManifestSchemaVersion,
            UpdatedAt = DateTimeOffset.UtcNow,
            Source = source,
            GameMode = "PVP",
            DatabaseSha256 = ComputeSha256(databasePath),
            ItemCount = summary.ItemCount,
            QuestCount = summary.QuestCount,
            HideoutStationCount = summary.HideoutStationCount,
            RequiredIconCount = icons.Count,
            MissingIconCount = 0,
            Icons = icons
        };
    }

    internal static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
            return;

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (overwrite || !File.Exists(target))
                File.Copy(file, target, overwrite);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        Directory.Delete(path, recursive: true);
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }
}

public sealed record ContentStagingPaths(
    string RootPath,
    string DatabasePath,
    string IconsPath,
    string ManifestPath);

public sealed class ContentUpdateManifest
{
    public int SchemaVersion { get; set; } = ContentStorageService.CurrentManifestSchemaVersion;
    public DateTimeOffset UpdatedAt { get; set; }
    public string Source { get; set; } = "tarkov.dev";
    public string GameMode { get; set; } = "PVP";
    public string DatabaseSha256 { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public int QuestCount { get; set; }
    public int HideoutStationCount { get; set; }
    public int RequiredIconCount { get; set; }
    public int MissingIconCount { get; set; }
    public Dictionary<string, ContentIconManifestEntry> Icons { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; set; } = new();
}

public sealed class ContentIconManifestEntry
{
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}
