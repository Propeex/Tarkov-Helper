from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, path: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:120]!r}")
    return text.replace(old, new, 1)


store_path = "TarkovHelper/Services/ProfileScopedObjectiveProgressStore.cs"
write(store_path, '''using Microsoft.Data.Sqlite;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Persists objective progress against an explicitly captured profile.
/// </summary>
public sealed class ProfileScopedObjectiveProgressStore
{
    private static ProfileScopedObjectiveProgressStore? _instance;
    public static ProfileScopedObjectiveProgressStore Instance =>
        _instance ??= new ProfileScopedObjectiveProgressStore();

    private readonly UserDataDbService _database = UserDataDbService.Instance;

    private ProfileScopedObjectiveProgressStore()
    {
    }

    public async Task SaveAsync(
        string id,
        string? questId,
        bool isCompleted,
        ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql = """
            INSERT INTO ObjectiveProgress (Id, ProfileType, QuestId, IsCompleted, UpdatedAt)
            VALUES (@id, @profileType, @questId, @isCompleted, @updatedAt)
            ON CONFLICT(Id, ProfileType) DO UPDATE SET
                QuestId = @questId,
                IsCompleted = @isCompleted,
                UpdatedAt = @updatedAt;
            """;

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        command.Parameters.AddWithValue("@questId", questId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@isCompleted", isCompleted ? 1 : 0);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "DELETE FROM ObjectiveProgress WHERE Id = @id AND ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task ClearAllAsync(ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "DELETE FROM ObjectiveProgress WHERE ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<Dictionary<string, bool>> LoadAsync(ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection(readOnly: true);
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "SELECT Id, IsCompleted FROM ObjectiveProgress WHERE ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@profileType", (int)profile);

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetInt32(1) == 1;

        return result;
    }

    private SqliteConnection CreateConnection(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}
''')

path = "TarkovHelper/Services/ObjectiveProgressService.cs"
text = read(path)
text = replace_once(
    text,
    '''        private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;
        private readonly PersistenceWriteQueue _persistenceQueue = new();
''',
    '''        private readonly PersistenceWriteQueue _persistenceQueue = new();
        private readonly ProfileScopedObjectiveProgressStore _store =
            ProfileScopedObjectiveProgressStore.Instance;
        private ProfileType _loadedProfile = ProfileType.Pvp;
''',
    path,
)
text = replace_once(
    text,
    '''            // Fire-and-forget async save - don't block UI
            _ = _persistenceQueue.Enqueue(() => SaveObjectiveProgressBatchAsync(keysToSave));
''',
    '''            // Capture the loaded profile at mutation time. The queued write may
            // execute after the application profile has changed.
            var targetProfile = _loadedProfile;
            _ = _persistenceQueue.Enqueue(() =>
                SaveObjectiveProgressBatchAsync(keysToSave, targetProfile));
''',
    path,
)
text = replace_once(
    text,
    '''            // Fire-and-forget async save - don't block UI
            _ = _persistenceQueue.Enqueue(() => SaveObjectiveProgressBatchAsync(keysToSave));
''',
    '''            // Capture the loaded profile at mutation time. The queued write may
            // execute after the application profile has changed.
            var targetProfile = _loadedProfile;
            _ = _persistenceQueue.Enqueue(() =>
                SaveObjectiveProgressBatchAsync(keysToSave, targetProfile));
''',
    path,
)
text = replace_once(
    text,
    '''        public async Task ClearAllProgressAsync(ProfileType? profileType = null)
        {
            ResetInMemoryProgress();
            await _persistenceQueue.ResetAsync(() =>
                _userDataDb.ClearAllObjectiveProgressAsync());
        }
''',
    '''        public async Task ClearAllProgressAsync(ProfileType? profileType = null)
        {
            var targetProfile = profileType ?? _loadedProfile;
            ResetInMemoryProgress();
            await _persistenceQueue.ResetAsync(() =>
                _store.ClearAllAsync(targetProfile));
        }
''',
    path,
)
start = text.index("        public void SaveObjectiveProgress()")
end = text.index("        #endregion", start)
new_region = '''        public void SaveObjectiveProgress()
        {
            var snapshot = _objectiveProgress
                .Select(pair => (Key: pair.Key, QuestId: GetQuestId(pair.Key), IsCompleted: pair.Value))
                .ToList();
            var targetProfile = _loadedProfile;
            _ = _persistenceQueue.Enqueue(() =>
                SaveObjectiveProgressBatchAsync(snapshot, targetProfile));
        }

        private static string? GetQuestId(string key)
        {
            var separator = key.IndexOf(':');
            if (separator <= 0)
                return null;

            var prefix = key[..separator];
            return string.Equals(prefix, "id", StringComparison.Ordinal) ? null : prefix;
        }

        private async Task SaveObjectiveProgressBatchAsync(
            IReadOnlyCollection<(string Key, string? QuestId, bool IsCompleted)> items,
            ProfileType profile)
        {
            try
            {
                foreach (var item in items)
                {
                    if (item.IsCompleted)
                        await _store.SaveAsync(item.Key, item.QuestId, true, profile);
                    else
                        await _store.DeleteAsync(item.Key, profile);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ObjectiveProgressService] Batch save failed: {ex.Message}");
            }
        }

        public async Task LoadObjectiveProgressAsync(ProfileType? profileType = null)
        {
            _loadedProfile = profileType ?? ProfileService.Instance.CurrentProfile;
            await LoadObjectiveProgressFromDbAsync(_loadedProfile);
        }

        private void LoadObjectiveProgress()
        {
            _loadedProfile = ProfileService.Instance.CurrentProfile;
            _ = LoadObjectiveProgressFromDbAsync(_loadedProfile);
        }

        private async Task LoadObjectiveProgressFromDbAsync(ProfileType profile)
        {
            try
            {
                var dbProgress = await _store.LoadAsync(profile);
                _objectiveProgress.Clear();
                foreach (var kvp in dbProgress)
                    _objectiveProgress[kvp.Key] = kvp.Value;

                System.Diagnostics.Debug.WriteLine(
                    $"[ObjectiveProgressService] Loaded {_objectiveProgress.Count} objective progress from DB for {profile}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ObjectiveProgressService] Load failed: {ex.Message}");
                _objectiveProgress.Clear();
            }
        }

'''
text = text[:start] + new_region + text[end:]
write(path, text)

path = "TarkovHelper/Services/UserProgressResetService.cs"
text = read(path)
text = replace_once(
    text,
    "        await database.ClearAllObjectiveProgressAsync();\n",
    "        await ProfileScopedObjectiveProgressStore.Instance.ClearAllAsync(profile);\n",
    path,
)
text = replace_once(
    text,
    "        var objectives = await database.LoadObjectiveProgressAsync();\n",
    "        var objectives = await ProfileScopedObjectiveProgressStore.Instance.LoadAsync(profile);\n",
    path,
)
write(path, text)

path = "TarkovHelper.DatabaseSmoke/Program.cs"
text = read(path)
text = replace_once(
    text,
    '''    await RunPersistenceWriteQueueSmokeAsync();
    await RunUserProgressResetSmokeAsync();
''',
    '''    await RunPersistenceWriteQueueSmokeAsync();
    await RunObjectiveProfileIsolationSmokeAsync();
    await RunUserProgressResetSmokeAsync();
''',
    path,
)
marker = "static async Task RunUserProgressResetSmokeAsync()\n"
method = '''static async Task RunObjectiveProfileIsolationSmokeAsync()
{
    const string key = "objective-profile-isolation-smoke";
    var store = ProfileScopedObjectiveProgressStore.Instance;

    await store.ClearAllAsync(ProfileType.Pvp);
    await store.ClearAllAsync(ProfileType.Pve);
    await store.SaveAsync(key, "objective-profile-isolation-quest", true, ProfileType.Pvp);

    var pvp = await store.LoadAsync(ProfileType.Pvp);
    var pve = await store.LoadAsync(ProfileType.Pve);
    if (!pvp.TryGetValue(key, out var completed) || !completed || pve.ContainsKey(key))
    {
        throw new InvalidDataException(
            $"Objective profile isolation failed: pvp={pvp.ContainsKey(key)}, pve={pve.ContainsKey(key)}.");
    }

    await store.ClearAllAsync(ProfileType.Pvp);
    await store.ClearAllAsync(ProfileType.Pve);
}

'''
text = replace_once(text, marker, method + marker, path)
write(path, text)
