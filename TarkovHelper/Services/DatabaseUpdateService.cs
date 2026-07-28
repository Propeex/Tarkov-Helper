using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds tarkov_data.db from the current tarkov.dev PVP data set.
/// Automatic background replacement is intentionally disabled; rebuilding is
/// only performed after the user presses the data update button.
/// </summary>
public sealed class DatabaseUpdateService : IDisposable
{
    private static readonly ILogger _log = Log.For<DatabaseUpdateService>();
    private static DatabaseUpdateService? _instance;
    public static DatabaseUpdateService Instance => _instance ??= new DatabaseUpdateService();

    private const string LocalVersionFile = "db_version.txt";
    private const string DatabaseFile = "tarkov_data.db";

    private readonly string _assetsPath;
    private readonly string _databasePath;
    private readonly string _versionFilePath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private bool _isUpdating;
    private bool _disposed;

    public string DatabasePath => _databasePath;
    public string? LocalVersion { get; private set; }
    public string? RemoteVersion { get; private set; }
    public bool IsUpdating => _isUpdating;

    public event EventHandler? DatabaseUpdated;
    public event EventHandler? UpdateCheckStarted;
    public event EventHandler<UpdateCheckResult>? UpdateCheckCompleted;
    public event EventHandler<DatabaseBuildProgress>? ProgressChanged;

    private DatabaseUpdateService()
    {
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        _assetsPath = Path.Combine(appDirectory, "Assets");
        _databasePath = Path.Combine(_assetsPath, DatabaseFile);
        _versionFilePath = Path.Combine(_assetsPath, LocalVersionFile);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.5.7 database-builder");

        LoadLocalVersion();
    }

    private void LoadLocalVersion()
    {
        try
        {
            LocalVersion = File.Exists(_versionFilePath)
                ? File.ReadAllText(_versionFilePath).Trim()
                : null;
        }
        catch (Exception exception)
        {
            _log.Warning($"Failed to read local database version: {exception.Message}");
            LocalVersion = null;
        }
    }

    /// <summary>
    /// Kept for compatibility with the existing startup path. API database
    /// generation is manual because it rewrites the complete data set.
    /// </summary>
    public void StartBackgroundUpdates()
    {
        _log.Info("Automatic database rebuild is disabled; waiting for manual update request.");
    }

    public void StopBackgroundUpdates()
    {
        // Manual-only service. Nothing to stop.
    }

    /// <summary>
    /// Creates a new validated database from tarkov.dev PVP data.
    /// The existing file remains untouched unless all API, write, and
    /// referential-integrity checks have succeeded.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndUpdateAsync()
    {
        ThrowIfDisposed();

        if (!await _buildGate.WaitAsync(0))
            return new UpdateCheckResult(false, false, "데이터베이스 생성이 이미 진행 중입니다.");

        _isUpdating = true;
        UpdateCheckStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            Directory.CreateDirectory(_assetsPath);
            ReportProgress(new DatabaseBuildProgress(
                "준비",
                "PVP 데이터베이스 생성을 준비하는 중",
                0,
                0,
                null,
                TimeSpan.Zero,
                null));

            var builder = new TarkovDataDatabaseBuilder(_httpClient, ReportProgress);
            var result = await builder.BuildAsync(_databasePath);

            var version = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            try
            {
                await File.WriteAllTextAsync(_versionFilePath, version);
                LocalVersion = version;
            }
            catch (Exception exception)
            {
                _log.Warning($"Database was rebuilt but version file could not be written: {exception.Message}");
            }

            OnDatabaseUpdated();

            var message = $"데이터베이스 생성 완료: 아이템 {result.ItemCount:N0}개, " +
                          $"퀘스트 {result.QuestCount:N0}개, 은신처 {result.HideoutStationCount:N0}개";
            var success = new UpdateCheckResult(true, true, message);
            UpdateCheckCompleted?.Invoke(this, success);
            return success;
        }
        catch (OperationCanceledException)
        {
            const string message = "데이터베이스 생성이 취소되었습니다. 기존 데이터베이스는 유지됩니다.";
            _log.Warning(message);
            UpdateUiStatus(message);
            var result = new UpdateCheckResult(false, false, message);
            UpdateCheckCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception exception)
        {
            _log.Error("Database rebuild failed", exception);
            var message = $"데이터베이스 생성 실패: {exception.Message} 기존 데이터베이스는 유지됩니다.";
            UpdateUiStatus(message);
            var result = new UpdateCheckResult(false, false, message);
            UpdateCheckCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            _isUpdating = false;
            _buildGate.Release();
        }
    }

    public Task<UpdateCheckResult> ForceUpdateCheckAsync()
    {
        return CheckAndUpdateAsync();
    }

    private void ReportProgress(DatabaseBuildProgress progress)
    {
        ProgressChanged?.Invoke(this, progress);
        UpdateUiStatus(progress.ToDisplayText());
        _log.Debug($"Database rebuild: {progress.Percent:F1}% - {progress.Message}");
    }

    /// <summary>
    /// Updates the existing settings status label without requiring the large
    /// MainWindow code-behind to own the rebuild pipeline. ProgressChanged is
    /// also exposed for a future dedicated progress view.
    /// </summary>
    private static void UpdateUiStatus(string text)
    {
        var application = Application.Current;
        if (application?.Dispatcher == null)
            return;

        application.Dispatcher.BeginInvoke(() =>
        {
            if (application.MainWindow?.FindName("TxtApiUpdateStatus") is TextBlock statusText)
                statusText.Text = text;
        });
    }

    private void OnDatabaseUpdated()
    {
        SqliteConnection.ClearAllPools();

        var application = Application.Current;
        if (application?.Dispatcher != null)
        {
            application.Dispatcher.BeginInvoke(() =>
            {
                DatabaseUpdated?.Invoke(this, EventArgs.Empty);
            });
        }
        else
        {
            DatabaseUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DatabaseUpdateService));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
        _buildGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class UpdateCheckResult
{
    public bool Success { get; }
    public bool WasUpdated { get; }
    public string Message { get; }

    public UpdateCheckResult(bool success, bool wasUpdated, string message)
    {
        Success = success;
        WasUpdated = wasUpdated;
        Message = message;
    }
}
