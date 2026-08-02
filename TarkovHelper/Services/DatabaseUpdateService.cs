using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds the PVP reference database and synchronizes required item icons.
/// All mutable content is prepared in an isolated LocalAppData staging folder;
/// the active set is replaced only after validation succeeds.
/// </summary>
public sealed class DatabaseUpdateService : IDisposable
{
    private static readonly ILogger _log = Log.For<DatabaseUpdateService>();
    private static DatabaseUpdateService? _instance;
    public static DatabaseUpdateService Instance => _instance ??= new DatabaseUpdateService();

    private const string ProgressBarTag = "ApiDatabaseUpdateProgress";
    private const double ProgressAreaMaxWidth = 420;
    private const double ProgressBarMaxWidth = 400;

    private readonly ContentStorageService _storage;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleLock = new();
    private volatile bool _isUpdating;
    private volatile bool _disposed;
    private int _resourcesDisposed;

    public string DatabasePath => _storage.DatabasePath;
    public string IconsPath => _storage.IconsPath;
    public string? LocalVersion { get; private set; }
    public string? RemoteVersion { get; private set; }
    public bool IsUpdating => _isUpdating;
    public bool CanRestorePreviousContent => _storage.HasPreviousContent;

    public event EventHandler? DatabaseUpdated;
    public event EventHandler? UpdateCheckStarted;
    public event EventHandler<UpdateCheckResult>? UpdateCheckCompleted;
    public event EventHandler<DatabaseBuildProgress>? ProgressChanged;

    private DatabaseUpdateService()
    {
        _storage = ContentStorageService.Instance;
        _httpClient = new HttpClient(
            new TarkovJsonObjectiveIdProtectionHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.8.5 content-database-builder");

        EnsureUiResources();
        LoadLocalVersion();
    }

    private void LoadLocalVersion()
    {
        var manifest = _storage.LoadCurrentManifest();
        LocalVersion = manifest?.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public void StartBackgroundUpdates()
    {
        _log.Info("Automatic content replacement is disabled; waiting for a manual PVP content update request.");
    }

    public void StopBackgroundUpdates()
    {
        // Manual-only service. Nothing to stop.
    }

    /// <summary>
    /// Downloads and validates the current tarkov.dev PVP data set, synchronizes
    /// required item icons, then atomically switches the active content directory.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndUpdateAsync()
    {
        ThrowIfDisposed();
        EnsureUiResources();

        if (!await _buildGate.WaitAsync(0))
            return new UpdateCheckResult(false, false, "콘텐츠 업데이트가 이미 진행 중입니다.");

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
        var committed = false;

        try
        {
            var cancellationToken = _disposeCancellation.Token;
            var previousSummary = await ContentDatabaseSummary.ReadAsync(DatabasePath, cancellationToken);
            var previousManifest = _storage.LoadCurrentManifest();
            var staging = _storage.PrepareStaging();

            ReportProgress(new DatabaseBuildProgress(
                "준비",
                "PVP 데이터와 아이템 이미지 업데이트 준비 중",
                0,
                0,
                null,
                TimeSpan.Zero,
                null));

            var builder = new TarkovDataDatabaseBuilder(_httpClient, ReportDatabaseBuildProgress);
            var buildResult = await Task.Run(
                () => builder.BuildPreferredAsync(staging.DatabasePath, cancellationToken),
                cancellationToken);

            var nextSummary = await ContentDatabaseSummary.ReadAsync(staging.DatabasePath, cancellationToken);
            previousSummary.EnsurePlausibleReplacement(nextSummary);
            var changes = previousSummary.CompareTo(nextSummary);

            using var iconUpdater = new ItemIconUpdateService(ReportProgress);
            var iconResult = await iconUpdater.SynchronizeAsync(
                staging.DatabasePath,
                staging.IconsPath,
                previousManifest,
                cancellationToken);

            var warnings = new List<string>();
            if (nextSummary.UnknownObjectiveTypes.Count > 0)
            {
                warnings.Add(
                    "지원 여부 확인이 필요한 퀘스트 목표 유형: " +
                    string.Join(", ", nextSummary.UnknownObjectiveTypes));
            }
            AddNewValueWarning(warnings, "새 아이템 분류", changes.NewItemCategoryValues);
            AddNewValueWarning(warnings, "새 상인 식별자", changes.NewTraderValues);
            AddNewValueWarning(warnings, "새 지도 식별자", changes.NewMapValues);
            if (iconResult.Failures.Count > 0)
            {
                warnings.Add(
                    $"아이콘 {iconResult.Failures.Count:N0}개를 갱신하지 못했습니다. " +
                    "기존 이미지가 있으면 유지되며 설정에서 다시 시도할 수 있습니다.");
            }

            var manifest = new ContentUpdateManifest
            {
                SchemaVersion = ContentStorageService.CurrentManifestSchemaVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
                Source = "tarkov.dev",
                GameMode = "PVP",
                DatabaseSha256 = ContentStorageService.ComputeSha256(staging.DatabasePath),
                ItemCount = nextSummary.ItemCount,
                QuestCount = nextSummary.QuestCount,
                HideoutStationCount = nextSummary.HideoutStationCount,
                RequiredIconCount = iconResult.RequiredCount,
                MissingIconCount = iconResult.MissingCount,
                Icons = iconResult.Entries,
                Warnings = warnings
            };
            await _storage.SaveManifestAsync(staging.ManifestPath, manifest, cancellationToken);

            TryDelete(buildResult.BackupPath);
            ReportProgress(new DatabaseBuildProgress(
                "적용",
                "검증된 콘텐츠를 적용하는 중",
                98,
                0,
                null,
                TimeSpan.Zero,
                null));

            await FlushBeforeContentSwapAsync();
            SqliteConnection.ClearAllPools();
            ImageCacheService.Instance.ClearMemoryCache();
            _storage.CommitStaging();
            committed = true;
            LocalVersion = manifest.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

            var reloadWarning = await ReloadApplicationDataAsync();
            OnDatabaseUpdated();

            var message = BuildCompletionMessage(changes, iconResult, warnings, reloadWarning);
            UpdateUiStatus(message, 100, false);
            var success = new UpdateCheckResult(true, true, message);
            UpdateCheckCompleted?.Invoke(this, success);
            return success;
        }
        catch (OperationCanceledException)
        {
            _storage.DiscardStaging();
            var message = committed
                ? "콘텐츠 적용은 완료됐지만 화면 새로고침이 취소되었습니다. 프로그램을 다시 실행해 주세요."
                : "콘텐츠 업데이트가 취소되었습니다. 기존 데이터와 이미지는 유지됩니다.";
            _log.Warning(message);
            return PublishFailure(message);
        }
        catch (Exception exception)
        {
            _storage.DiscardStaging();
            _log.Error("PVP content update failed", exception);
            var message = committed
                ? $"콘텐츠는 적용됐지만 화면 갱신 중 오류가 발생했습니다: {exception.Message} 프로그램을 다시 실행해 주세요."
                : $"콘텐츠 업데이트 실패: {exception.Message} 기존 데이터와 이미지는 유지됩니다.";
            return PublishFailure(message);
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
                SetUpdateButtonsEnabled(true);

            _buildGate.Release();
            if (disposeResources)
                DisposeResources();
        }
    }

    public async Task<UpdateCheckResult> ResetDownloadedContentAsync()
    {
        return await RunContentReplacementAsync(
            "릴리스 기본 콘텐츠로 복원 중",
            async cancellationToken => await _storage.ResetToBundledAsync(cancellationToken),
            "다운로드 콘텐츠를 삭제하고 릴리스 기본 데이터와 이미지로 복원했습니다.");
    }

    public async Task<UpdateCheckResult> RestorePreviousContentAsync()
    {
        if (!_storage.HasPreviousContent)
            return new UpdateCheckResult(false, false, "복구할 이전 콘텐츠가 없습니다.");

        return await RunContentReplacementAsync(
            "이전 콘텐츠로 복구 중",
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _storage.RestorePrevious();
                return Task.CompletedTask;
            },
            "이전 데이터와 아이템 이미지로 복구했습니다.");
    }

    public Task<UpdateCheckResult> ForceUpdateCheckAsync() => CheckAndUpdateAsync();

    private async Task<UpdateCheckResult> RunContentReplacementAsync(
        string progressMessage,
        Func<CancellationToken, Task> replaceAction,
        string successMessage)
    {
        ThrowIfDisposed();
        if (!await _buildGate.WaitAsync(0))
            return new UpdateCheckResult(false, false, "콘텐츠 작업이 이미 진행 중입니다.");

        lock (_lifecycleLock)
            _isUpdating = true;

        try
        {
            SetUpdateButtonsEnabled(false);
            UpdateUiStatus(progressMessage, null, true);
            await FlushBeforeContentSwapAsync();
            SqliteConnection.ClearAllPools();
            ImageCacheService.Instance.ClearMemoryCache();
            await replaceAction(_disposeCancellation.Token);
            LoadLocalVersion();
            var reloadWarning = await ReloadApplicationDataAsync();
            OnDatabaseUpdated();

            var message = string.IsNullOrWhiteSpace(reloadWarning)
                ? successMessage
                : successMessage + " " + reloadWarning;
            UpdateUiStatus(message, 100, false);
            return new UpdateCheckResult(true, true, message);
        }
        catch (Exception exception)
        {
            _log.Error("Content replacement failed", exception);
            var message = $"콘텐츠 작업 실패: {exception.Message} 기존 사용자 진행도는 변경되지 않았습니다.";
            UpdateUiStatus(message, 0, false);
            return new UpdateCheckResult(false, false, message);
        }
        finally
        {
            lock (_lifecycleLock)
                _isUpdating = false;
            SetUpdateButtonsEnabled(true);
            _buildGate.Release();
        }
    }

    private static async Task FlushBeforeContentSwapAsync()
    {
        await InventoryConsumptionService.FlushExistingAsync();
        await ItemInventoryService.FlushExistingAsync();
    }

    private static async Task<string?> ReloadApplicationDataAsync()
    {
        try
        {
            if (Application.Current?.MainWindow is TarkovHelper.MainWindow mainWindow)
                await mainWindow.ReloadAfterDatabaseRebuildAsync();
            return null;
        }
        catch (Exception exception)
        {
            _log.Error("Content was applied but application data reload failed", exception);
            return "화면 재로딩에 실패해 프로그램 재시작이 권장됩니다.";
        }
    }

    private void ReportDatabaseBuildProgress(DatabaseBuildProgress progress)
    {
        ReportProgress(progress with
        {
            Stage = progress.Stage == "완료" ? "데이터" : progress.Stage,
            Percent = Math.Clamp(progress.Percent * 0.70, 0, 70),
            Message = "PVP 데이터 · " + progress.Message
        });
    }

    private void ReportProgress(DatabaseBuildProgress progress)
    {
        if (_disposed)
            return;

        ProgressChanged?.Invoke(this, progress);
        UpdateUiStatus(progress.ToDisplayText(), progress.Percent, progress.Percent < 100);
        _log.Debug($"Content update: {progress.Percent:F1}% - {progress.Message}");
    }

    private UpdateCheckResult PublishFailure(string message)
    {
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

    private static string BuildCompletionMessage(
        ContentChangeSummary changes,
        IconUpdateResult icons,
        IReadOnlyCollection<string> warnings,
        string? reloadWarning)
    {
        var parts = new List<string>
        {
            $"업데이트 완료 · 아이템 {changes.ItemCount:N0}개 (+{changes.ItemsAdded:N0} / 변경 {changes.ItemsChanged:N0} / 삭제 {changes.ItemsRemoved:N0})",
            $"퀘스트 {changes.QuestCount:N0}개 (+{changes.QuestsAdded:N0} / 변경 {changes.QuestsChanged:N0} / 삭제 {changes.QuestsRemoved:N0})",
            $"은신처 {changes.HideoutStationCount:N0}개",
            $"아이콘 신규 {icons.DownloadedCount:N0} · 교체 {icons.ReplacedCount:N0} · 재사용 {icons.ReusedCount:N0} · 정리 {icons.RemovedCount:N0} · 누락 {icons.MissingCount:N0}"
        };

        if (warnings.Count > 0)
            parts.Add($"경고 {warnings.Count:N0}건");
        if (!string.IsNullOrWhiteSpace(reloadWarning))
            parts.Add(reloadWarning);
        return string.Join(Environment.NewLine, parts);
    }

    private static void AddNewValueWarning(
        ICollection<string> warnings,
        string label,
        IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
            return;

        const int displayLimit = 12;
        var display = string.Join(", ", values.Take(displayLimit));
        var remainder = values.Count > displayLimit
            ? $" 외 {values.Count - displayLimit:N0}개"
            : string.Empty;
        warnings.Add($"{label}: {display}{remainder}");
    }

    private static void UpdateUiStatus(string text, double? percent = null, bool isActive = false)
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

                progressBar.IsIndeterminate = isActive && !percent.HasValue;
                if (percent.HasValue)
                    progressBar.Value = Math.Clamp(percent.Value, 0, 100);
                progressBar.Visibility = Visibility.Visible;
            }

            SetUpdateButtonsEnabled(!isActive);
        });
    }

    private static void EnsureUiResources()
    {
        var application = Application.Current;
        if (application == null)
            return;

        void Ensure()
        {
            if (!application.Resources.Contains("ErrorBrush"))
            {
                var brush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
                brush.Freeze();
                application.Resources["ErrorBrush"] = brush;
            }
        }

        if (application.Dispatcher.CheckAccess())
            Ensure();
        else
            application.Dispatcher.Invoke(Ensure);
    }

    private static void SetUpdateButtonsEnabled(bool enabled)
    {
        var application = Application.Current;
        if (application?.Dispatcher == null)
            return;

        application.Dispatcher.BeginInvoke(() =>
        {
            foreach (var name in new[] { "BtnUpdateApiData", "BtnClearAllData" })
            {
                if (application.MainWindow?.FindName(name) is Button button)
                    button.IsEnabled = enabled;
            }

            if (application.MainWindow?.FindName("BtnRestoreContent") is Button restoreButton)
                restoreButton.IsEnabled = enabled && Instance.CanRestorePreviousContent;
        });
    }

    private void OnDatabaseUpdated()
    {
        SqliteConnection.ClearAllPools();
        var application = Application.Current;
        if (application?.Dispatcher != null)
            application.Dispatcher.BeginInvoke(() => DatabaseUpdated?.Invoke(this, EventArgs.Empty));
        else
            DatabaseUpdated?.Invoke(this, EventArgs.Empty);
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
            // Staging cleanup will remove it on the next operation.
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
