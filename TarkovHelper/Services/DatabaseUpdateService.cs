using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private const string ProgressBarTag = "ApiDatabaseUpdateProgress";
    private const double ProgressAreaMaxWidth = 340;
    private const double ProgressBarMaxWidth = 320;

    private readonly string _assetsPath;
    private readonly string _databasePath;
    private readonly string _versionFilePath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleLock = new();
    private volatile bool _isUpdating;
    private volatile bool _disposed;
    private int _resourcesDisposed;

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

        _httpClient = new HttpClient(
            new TarkovJsonObjectiveIdProtectionHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.5.7 database-builder");

        EnsureUiResources();
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
        EnsureUiResources();

        if (!await _buildGate.WaitAsync(0))
            return new UpdateCheckResult(false, false, "데이터베이스 생성이 이미 진행 중입니다.");

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                _buildGate.Release();
                throw new ObjectDisposedException(nameof(DatabaseUpdateService));
            }

            _isUpdating = true;
        }

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
            var cancellationToken = _disposeCancellation.Token;

            // SQLite table rewriting performs substantial synchronous work between
            // awaits. Run the complete build off the dispatcher thread so progress
            // text and the rest of the window remain responsive.
            var result = await Task.Run(
                () => builder.BuildPreferredAsync(_databasePath, cancellationToken),
                cancellationToken);

            var version = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            try
            {
                await File.WriteAllTextAsync(_versionFilePath, version, cancellationToken);
                LocalVersion = version;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Warning($"Database was rebuilt but version file could not be written: {exception.Message}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Progress services aggregate quest and hideout data. Recreate their
            // PVP snapshots after the reference database has been replaced. Complete
            // this reload before publishing DatabaseUpdated so subscribers cannot race
            // the main-window refresh against the same SQLite file.
            if (Application.Current?.MainWindow is TarkovHelper.MainWindow mainWindow)
                await mainWindow.ReloadAfterDatabaseRebuildAsync();

            OnDatabaseUpdated();

            var message = $"데이터베이스 생성 완료: 아이템 {result.ItemCount:N0}개, " +
                          $"퀘스트 {result.QuestCount:N0}개, 은신처 {result.HideoutStationCount:N0}개";
            UpdateUiStatus(message, 100, false);
            var success = new UpdateCheckResult(true, true, message);
            UpdateCheckCompleted?.Invoke(this, success);
            return success;
        }
        catch (OperationCanceledException)
        {
            const string message = "데이터베이스 생성이 취소되었습니다. 기존 데이터베이스는 유지됩니다.";
            _log.Warning(message);
            if (!_disposed)
            {
                EnsureUiResources();
                UpdateUiStatus(message, 0, false);
                var result = new UpdateCheckResult(false, false, message);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            return new UpdateCheckResult(false, false, message);
        }
        catch (Exception exception)
        {
            _log.Error("Database rebuild failed", exception);
            var message = $"데이터베이스 생성 실패: {exception.Message} 기존 데이터베이스는 유지됩니다.";
            if (!_disposed)
            {
                EnsureUiResources();
                UpdateUiStatus(message, 0, false);
                var result = new UpdateCheckResult(false, false, message);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            return new UpdateCheckResult(false, false, message);
        }
        finally
        {
            var disposeResources = false;
            lock (_lifecycleLock)
            {
                _isUpdating = false;
                disposeResources = _disposed;
            }

            if (!_disposed)
                SetUpdateButtonEnabled(true);

            _buildGate.Release();

            if (disposeResources)
                DisposeResources();
        }
    }

    public Task<UpdateCheckResult> ForceUpdateCheckAsync()
    {
        return CheckAndUpdateAsync();
    }

    private void ReportProgress(DatabaseBuildProgress progress)
    {
        if (_disposed)
            return;

        ProgressChanged?.Invoke(this, progress);
        UpdateUiStatus(
            progress.ToDisplayText(),
            progress.Percent,
            progress.Percent < 100);
        _log.Debug($"Database rebuild: {progress.Percent:F1}% - {progress.Message}");
    }

    private static void UpdateUiStatus(
        string text,
        double? percent = null,
        bool isActive = false)
    {
        var application = Application.Current;
        if (application?.Dispatcher == null)
            return;

        application.Dispatcher.BeginInvoke(() =>
        {
            if (application.MainWindow?.FindName("TxtApiUpdateStatus") is not TextBlock statusText)
                return;

            statusText.Text = text;
            statusText.TextWrapping = TextWrapping.Wrap;
            statusText.HorizontalAlignment = HorizontalAlignment.Left;
            statusText.VerticalAlignment = VerticalAlignment.Center;
            statusText.Margin = new Thickness(0, 8, 0, 4);
            statusText.MaxWidth = ProgressAreaMaxWidth;
            statusText.ToolTip = text;

            if (statusText.Parent is StackPanel statusPanel)
            {
                statusPanel.Orientation = Orientation.Vertical;
                statusPanel.HorizontalAlignment = HorizontalAlignment.Left;
                statusPanel.MaxWidth = ProgressAreaMaxWidth;

                var progressBar = statusPanel.Children
                    .OfType<ProgressBar>()
                    .FirstOrDefault(value => Equals(value.Tag, ProgressBarTag));

                if (progressBar == null)
                {
                    progressBar = new ProgressBar
                    {
                        Tag = ProgressBarTag,
                        Minimum = 0,
                        Maximum = 100,
                        Height = 10,
                        Margin = new Thickness(0, 4, 0, 8),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        MaxWidth = ProgressBarMaxWidth,
                        Width = ProgressBarMaxWidth
                    };
                    statusPanel.Children.Add(progressBar);
                }

                progressBar.MaxWidth = ProgressBarMaxWidth;
                progressBar.Width = ProgressBarMaxWidth;
                progressBar.IsIndeterminate = isActive && !percent.HasValue;
                if (percent.HasValue)
                    progressBar.Value = Math.Clamp(percent.Value, 0, 100);
                progressBar.Visibility = Visibility.Visible;
            }

            if (application.MainWindow.FindName("BtnUpdateApiData") is Button updateButton)
            {
                updateButton.HorizontalAlignment = HorizontalAlignment.Left;
                updateButton.Margin = new Thickness(0, 0, 0, 0);
                updateButton.IsEnabled = !isActive;
            }
        });
    }

    private static void EnsureUiResources()
    {
        var application = Application.Current;
        if (application == null)
            return;

        void Ensure()
        {
            if (application.Resources.Contains("ErrorBrush"))
                return;

            var brush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            brush.Freeze();
            application.Resources["ErrorBrush"] = brush;
        }

        if (application.Dispatcher.CheckAccess())
            Ensure();
        else
            application.Dispatcher.Invoke(Ensure);
    }

    private static void SetUpdateButtonEnabled(bool enabled)
    {
        var application = Application.Current;
        if (application?.Dispatcher == null)
            return;

        application.Dispatcher.BeginInvoke(() =>
        {
            if (application.MainWindow?.FindName("BtnUpdateApiData") is Button updateButton)
                updateButton.IsEnabled = enabled;
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
        var disposeResources = false;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _disposeCancellation.Cancel();
            disposeResources = !_isUpdating;
        }

        if (disposeResources)
            DisposeResources();

        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        _httpClient.Dispose();
        _disposeCancellation.Dispose();

        // _buildGate intentionally remains undisposed. A build that was already in
        // its finally block must always be able to release the gate during shutdown.
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