from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, path: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:80]!r}")
    return text.replace(old, new, 1)


queue_path = "TarkovHelper/Services/PersistenceWriteQueue.cs"
write(queue_path, '''namespace TarkovHelper.Services;

/// <summary>
/// Serializes fire-and-forget persistence writes and provides an explicit reset barrier.
/// Writes queued before a reset either finish before the barrier or are discarded.
/// Writes requested while the barrier is held are discarded so they cannot recreate
/// rows after the database has been cleared.
/// </summary>
public sealed class PersistenceWriteQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _generation;
    private bool _resetInProgress;

    public Task Enqueue(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            if (_resetInProgress)
                return Task.CompletedTask;

            var generation = _generation;
            _tail = ExecuteWriteAsync(_tail, generation, operation);
            return _tail;
        }
    }

    public Task BeginResetAsync()
    {
        lock (_sync)
        {
            if (_resetInProgress)
                return _tail;

            _generation++;
            _resetInProgress = true;
            _tail = ObservePreviousAsync(_tail);
            return _tail;
        }
    }

    public void EndReset()
    {
        lock (_sync)
            _resetInProgress = false;
    }

    public async Task ResetAsync(Func<Task> clearOperation)
    {
        ArgumentNullException.ThrowIfNull(clearOperation);

        await BeginResetAsync().ConfigureAwait(false);
        try
        {
            await clearOperation().ConfigureAwait(false);
        }
        finally
        {
            EndReset();
        }
    }

    public Task FlushAsync()
    {
        lock (_sync)
            return _tail;
    }

    private async Task ExecuteWriteAsync(Task previous, long generation, Func<Task> operation)
    {
        await ObservePreviousAsync(previous).ConfigureAwait(false);

        lock (_sync)
        {
            if (_resetInProgress || generation != _generation)
                return;
        }

        await operation().ConfigureAwait(false);
    }

    private static async Task ObservePreviousAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A later persistence operation or reset must not be blocked by a
            // previous failed write. Individual services log their own failures.
        }
    }
}
''')

for path in [
    "TarkovHelper/Services/QuestProgressService.cs",
    "TarkovHelper/Services/ObjectiveProgressService.cs",
    "TarkovHelper/Services/HideoutProgressService.cs",
]:
    text = read(path)
    old = "        public Task FlushPersistenceAsync() => _persistenceQueue.FlushAsync();\n"
    new = '''        public Task FlushPersistenceAsync() => _persistenceQueue.FlushAsync();

        internal Task BeginPersistenceResetAsync() => _persistenceQueue.BeginResetAsync();

        internal void EndPersistenceReset() => _persistenceQueue.EndReset();
'''
    text = replace_once(text, old, new, path)
    write(path, text)

inventory_path = "TarkovHelper/Services/ItemInventoryService.cs"
text = read(inventory_path)
text = replace_once(
    text,
    '''        private readonly object _saveTaskLock = new();
        private Task _saveTask = Task.CompletedTask;
''',
    '''        private readonly PersistenceWriteQueue _persistenceQueue = new();
''',
    inventory_path,
)
text = replace_once(
    text,
    '''        private void QueuePersistence(Func<Task> operation)
        {
            lock (_saveTaskLock)
            {
                _saveTask = _saveTask.ContinueWith(
                    async previous =>
                    {
                        if (previous.IsFaulted)
                            _log.Error("Previous inventory persistence operation failed", previous.Exception!);

                        try
                        {
                            await operation().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.Error("Inventory persistence operation failed", ex);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }
''',
    '''        private void QueuePersistence(Func<Task> operation)
        {
            _ = _persistenceQueue.Enqueue(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("Inventory persistence operation failed", ex);
                }
            });
        }
''',
    inventory_path,
)
text = replace_once(
    text,
    '''                Task pendingTask;
                lock (_saveTaskLock)
                    pendingTask = _saveTask;

                await pendingTask.ConfigureAwait(false);
''',
    '''                await _persistenceQueue.FlushAsync().ConfigureAwait(false);
''',
    inventory_path,
)
text = replace_once(
    text,
    '''        public async Task ResetAllInventoryAsync(ProfileType? profileType = null)
        {
            ProfileType actualProfile;
            Task pendingSave;

            lock (_lock)
            {
                if (_disposed)
                    return;

                actualProfile = profileType ?? _loadedProfile;
                _saveTimer?.Stop();
                _pendingSaves.Clear();
                _inventoryData = new ItemInventoryData();
            }

            lock (_saveTaskLock)
                pendingSave = _saveTask;

            await pendingSave.ConfigureAwait(false);
            await _userDataDb.ClearAllItemInventoryAsync(actualProfile).ConfigureAwait(false);
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }
''',
    '''        public async Task ResetAllInventoryAsync(ProfileType? profileType = null)
        {
            ProfileType actualProfile;
            lock (_lock)
            {
                if (_disposed)
                    return;

                actualProfile = profileType ?? _loadedProfile;
                _saveTimer?.Stop();
                _pendingSaves.Clear();
                _inventoryData = new ItemInventoryData();
            }

            await _persistenceQueue.ResetAsync(() =>
                _userDataDb.ClearAllItemInventoryAsync(actualProfile)).ConfigureAwait(false);
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        internal async Task BeginPersistenceResetAsync()
        {
            var barrier = _persistenceQueue.BeginResetAsync();
            lock (_lock)
            {
                if (_disposed)
                    return;

                _saveTimer?.Stop();
                _pendingSaves.Clear();
            }

            await barrier.ConfigureAwait(false);

            // Catch item changes or a timer callback that raced with reset entry.
            lock (_lock)
            {
                _saveTimer?.Stop();
                _pendingSaves.Clear();
            }
        }

        internal void EndPersistenceReset() => _persistenceQueue.EndReset();
''',
    inventory_path,
)
write(inventory_path, text)

reset_path = "TarkovHelper/Services/UserProgressResetService.cs"
write(reset_path, '''using TarkovHelper.Models;

namespace TarkovHelper.Services;

public sealed class UserProgressResetService
{
    private static UserProgressResetService? _instance;
    public static UserProgressResetService Instance => _instance ??= new UserProgressResetService();

    private readonly SemaphoreSlim _resetGate = new(1, 1);

    private UserProgressResetService()
    {
    }

    public async Task ResetCurrentProfileAsync()
    {
        await _resetGate.WaitAsync();

        var questProgress = QuestProgressService.Instance;
        var objectiveProgress = ObjectiveProgressService.Instance;
        var hideoutProgress = HideoutProgressService.Instance;
        var inventory = ItemInventoryService.Instance;

        try
        {
            var profile = ProfileService.Instance.CurrentProfile;

            // Hold every persistence queue for the entire reset transaction. This is
            // deliberately wider than the individual table clears: no service can
            // reopen its queue while another table is still being cleared or verified.
            await Task.WhenAll(
                questProgress.BeginPersistenceResetAsync(),
                objectiveProgress.BeginPersistenceResetAsync(),
                hideoutProgress.BeginPersistenceResetAsync(),
                inventory.BeginPersistenceResetAsync());

            ResetAllInMemoryServices();
            await ClearAllDatabaseRowsAsync(profile);

            var counts = await GetRowCountsAsync(profile);
            if (!IsEmpty(counts))
            {
                throw new InvalidDataException(
                    $"Reset verification failed: quests={counts.Quests}, " +
                    $"objectives={counts.Objectives}, hideout={counts.Hideout}, " +
                    $"inventory={counts.Inventory}.");
            }
        }
        finally
        {
            // Discard any in-memory mutations raised while persistence was paused,
            // then reopen all queues together without an asynchronous gap.
            ResetAllInMemoryServices();
            questProgress.EndPersistenceReset();
            objectiveProgress.EndPersistenceReset();
            hideoutProgress.EndPersistenceReset();
            inventory.EndPersistenceReset();
            _resetGate.Release();
        }
    }

    private static bool IsEmpty((int Quests, int Objectives, int Hideout, int Inventory) counts)
        => counts == (0, 0, 0, 0);

    private static async Task ClearAllDatabaseRowsAsync(ProfileType profile)
    {
        var database = UserDataDbService.Instance;
        await database.ClearAllQuestProgressAsync(profile);
        await database.ClearAllObjectiveProgressAsync();
        await database.ClearAllHideoutProgressAsync(profile);
        await database.ClearAllItemInventoryAsync(profile);
    }

    private static async Task<(int Quests, int Objectives, int Hideout, int Inventory)> GetRowCountsAsync(
        ProfileType profile)
    {
        var database = UserDataDbService.Instance;
        var quests = await database.LoadQuestProgressAsync(profile);
        var objectives = await database.LoadObjectiveProgressAsync();
        var hideout = await database.LoadHideoutProgressAsync(profile);
        var inventory = await database.LoadItemInventoryAsync(profile);
        return (quests.Count, objectives.Count, hideout.Count, inventory.Count);
    }

    private static void ResetAllInMemoryServices()
    {
        QuestProgressService.Instance.ResetInMemoryProgress();
        ObjectiveProgressService.Instance.ResetInMemoryProgress();
        HideoutProgressService.Instance.ResetInMemoryProgress();
        ItemInventoryService.Instance.ResetInMemoryInventory();
    }
}
''')

smoke_path = "TarkovHelper.DatabaseSmoke/Program.cs"
text = read(smoke_path)
start = text.index("static async Task RunPersistenceWriteQueueSmokeAsync()")
end = text.index("static async Task RunUserProgressResetSmokeAsync()", start)
new_smoke = '''static async Task RunPersistenceWriteQueueSmokeAsync()
{
    var queue = new PersistenceWriteQueue();
    var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var state = 0;

    _ = queue.Enqueue(async () =>
    {
        writeStarted.TrySetResult();
        await releaseWrite.Task;
        state = 1;
    });

    await writeStarted.Task;
    var resetBarrier = queue.BeginResetAsync();
    var queuedDuringDrain = queue.Enqueue(() =>
    {
        state = 2;
        return Task.CompletedTask;
    });

    releaseWrite.TrySetResult();
    await Task.WhenAll(resetBarrier, queuedDuringDrain);

    // Simulate the database clear while the reset barrier remains held.
    state = 0;
    await queue.Enqueue(() =>
    {
        state = 4;
        return Task.CompletedTask;
    });
    if (state != 0)
        throw new InvalidDataException($"Persistence reset hold failed: state={state}.");

    queue.EndReset();
    await queue.Enqueue(() =>
    {
        state = 3;
        return Task.CompletedTask;
    });
    if (state != 3)
        throw new InvalidDataException("Persistence queue did not resume after reset.");
}

'''
text = text[:start] + new_smoke + text[end:]

marker = '''    await database.SaveItemInventoryAsync("reset-smoke-item", 3, 4, profile);

    await UserProgressResetService.Instance.ResetCurrentProfileAsync();
'''
replacement = '''    await database.SaveItemInventoryAsync("reset-smoke-item", 3, 4, profile);

    // Exercise the real debounced inventory persistence path immediately before reset.
    // The timer would recreate a row after 500 ms if the coordinated barrier failed.
    ItemInventoryService.Instance.SetFirQuantity("reset-smoke-pending-item", 7);

    await UserProgressResetService.Instance.ResetCurrentProfileAsync();
    await Task.Delay(650);
'''
text = replace_once(text, marker, replacement, smoke_path)
write(smoke_path, text)

release_path = ".github/workflows/release.yml"
text = read(release_path)
old_notes = '''          ### v1.6.2 수정
          - 초기화 버튼이 퀘스트·목표·은신처 진행도와 보유 아이템을 모두 삭제하고 결과를 재검증하도록 수정
          - 초기화 후 퀘스트 목록이 숨겨지지 않도록 기본 상태 필터를 전체로 변경
          - 아이템 집계 작업을 백그라운드에서 직렬화하고 아이콘 로드를 소규모 배치로 분할
          - 지도 탭의 컴포넌트·정적 데이터 중복 초기화 및 동기 Dispatcher 대기 제거
          - 초기화 통합 smoke와 기존 DB·아이콘·배포 파일 무결성 검증 유지
'''
new_notes = '''          ### v1.6.3 수정
          - 초기화 전체 구간 동안 퀘스트·목표·은신처·보유 아이템 저장 큐를 동시에 정지
          - 초기화 전에 실행 중이던 저장 완료 후 DB를 삭제하고, 대기 중·초기화 중 저장은 폐기
          - DB 삭제 검증과 메모리 초기화가 모두 끝난 뒤 네 저장 큐를 함께 재개
          - 고정 시간 대기 기반 초기화 판정을 제거하고 결정론적 reset barrier 검사 추가
          - 초기화 후 퀘스트 기본 필터, 아이템 집계 및 지도 탭 멈춤 수정 유지
'''
text = replace_once(text, old_notes, new_notes, release_path)
write(release_path, text)
