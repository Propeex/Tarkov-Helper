using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TarkovHelper.Models;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for managing quest objective completion progress
    /// </summary>
    public class ObjectiveProgressService
    {
        private static ObjectiveProgressService? _instance;
        public static ObjectiveProgressService Instance => _instance ??= new ObjectiveProgressService();

        private readonly PersistenceWriteQueue _persistenceQueue = new();
        private readonly ProfileScopedObjectiveProgressStore _store =
            ProfileScopedObjectiveProgressStore.Instance;
        private ProfileType _loadedProfile = ProfileType.Pvp;

        // Objective progress: key = "questNormalizedName:objectiveIndex" or "id:objectiveId", value = completed
        private Dictionary<string, bool> _objectiveProgress = new();

        public event EventHandler<ObjectiveProgressChangedEventArgs>? ObjectiveProgressChanged;

        private ObjectiveProgressService()
        {
            // 초기화는 외부에서 비동기적으로 호출되어야 합니다.
        }

        #region Objective Status

        /// <summary>
        /// Get objective completion status by quest name and index
        /// </summary>
        public bool IsObjectiveCompleted(string questNormalizedName, int objectiveIndex)
        {
            var key = $"{questNormalizedName}:{objectiveIndex}";
            return _objectiveProgress.TryGetValue(key, out var completed) && completed;
        }

        /// <summary>
        /// Get objective completion status by objective ID
        /// </summary>
        public bool IsObjectiveCompletedById(string objectiveId)
        {
            var key = $"id:{objectiveId}";
            return _objectiveProgress.TryGetValue(key, out var completed) && completed;
        }

        /// <summary>
        /// Set objective completion status (index 기반 - Quests 탭용)
        /// ObjectiveId도 함께 저장하여 Map Tracker와 동기화
        /// </summary>
        public void SetObjectiveCompleted(string questNormalizedName, int objectiveIndex, bool completed, string? objectiveId = null)
        {
            var indexKey = $"{questNormalizedName}:{objectiveIndex}";
            var keysToSave = new List<(string Key, string? QuestId, bool IsCompleted)>();

            if (completed)
            {
                _objectiveProgress[indexKey] = true;
                keysToSave.Add((indexKey, questNormalizedName, true));
                // ObjectiveId도 함께 저장 (동기화)
                if (!string.IsNullOrEmpty(objectiveId))
                {
                    _objectiveProgress[$"id:{objectiveId}"] = true;
                    keysToSave.Add(($"id:{objectiveId}", null, true));
                }
            }
            else
            {
                _objectiveProgress.Remove(indexKey);
                keysToSave.Add((indexKey, questNormalizedName, false));
                // ObjectiveId도 함께 제거 (동기화)
                if (!string.IsNullOrEmpty(objectiveId))
                {
                    _objectiveProgress.Remove($"id:{objectiveId}");
                    keysToSave.Add(($"id:{objectiveId}", null, false));
                }
            }

            // Capture the loaded profile at mutation time. The queued write may
            // execute after the application profile has changed.
            var targetProfile = _loadedProfile;
            _ = _persistenceQueue.Enqueue(() =>
                SaveObjectiveProgressBatchAsync(keysToSave, targetProfile));
            ObjectiveProgressChanged?.Invoke(this, new ObjectiveProgressChangedEventArgs(questNormalizedName, objectiveIndex, completed));
        }

        /// <summary>
        /// Set objective completion status by objective ID (Map Tracker용)
        /// Index 기반 키도 함께 저장하여 Quests 탭과 동기화
        /// </summary>
        public void SetObjectiveCompletedById(string objectiveId, bool completed, string? questNormalizedName = null, int objectiveIndex = -1)
        {
            var idKey = $"id:{objectiveId}";
            var keysToSave = new List<(string Key, string? QuestId, bool IsCompleted)>();

            if (completed)
            {
                _objectiveProgress[idKey] = true;
                keysToSave.Add((idKey, null, true));
                // Index 기반 키도 함께 저장 (동기화)
                if (!string.IsNullOrEmpty(questNormalizedName) && objectiveIndex >= 0)
                {
                    _objectiveProgress[$"{questNormalizedName}:{objectiveIndex}"] = true;
                    keysToSave.Add(($"{questNormalizedName}:{objectiveIndex}", questNormalizedName, true));
                }
            }
            else
            {
                _objectiveProgress.Remove(idKey);
                keysToSave.Add((idKey, null, false));
                // Index 기반 키도 함께 제거 (동기화)
                if (!string.IsNullOrEmpty(questNormalizedName) && objectiveIndex >= 0)
                {
                    _objectiveProgress.Remove($"{questNormalizedName}:{objectiveIndex}");
                    keysToSave.Add(($"{questNormalizedName}:{objectiveIndex}", questNormalizedName, false));
                }
            }

            // Capture the loaded profile at mutation time. The queued write may
            // execute after the application profile has changed.
            var targetProfile = _loadedProfile;
            _ = _persistenceQueue.Enqueue(() =>
                SaveObjectiveProgressBatchAsync(keysToSave, targetProfile));
            ObjectiveProgressChanged?.Invoke(this, new ObjectiveProgressChangedEventArgs(objectiveId, objectiveIndex, completed));
        }

        /// <summary>
        /// Get all completed objective indices for a quest
        /// </summary>
        public HashSet<int> GetCompletedObjectives(string questNormalizedName)
        {
            var result = new HashSet<int>();
            var prefix = $"{questNormalizedName}:";

            foreach (var kvp in _objectiveProgress)
            {
                if (kvp.Key.StartsWith(prefix) && kvp.Value)
                {
                    var indexStr = kvp.Key.Substring(prefix.Length);
                    if (int.TryParse(indexStr, out var index))
                    {
                        result.Add(index);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get objective completion count for a quest
        /// </summary>
        public (int Completed, int Total) GetObjectiveProgress(TarkovTask task)
        {
            if (task.NormalizedName == null || task.Objectives == null)
                return (0, 0);

            var completedSet = GetCompletedObjectives(task.NormalizedName);
            return (completedSet.Count, task.Objectives.Count);
        }

        /// <summary>
        /// Clear all objective progress for a quest
        /// </summary>
        public void ClearObjectiveProgress(string questNormalizedName)
        {
            var prefix = $"{questNormalizedName}:";
            var keysToRemove = _objectiveProgress.Keys.Where(k => k.StartsWith(prefix)).ToList();

            foreach (var key in keysToRemove)
            {
                _objectiveProgress.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                SaveObjectiveProgress();
                ObjectiveProgressChanged?.Invoke(this, new ObjectiveProgressChangedEventArgs(questNormalizedName, -1, false));
            }
        }

        /// <summary>
        /// Clear all objective progress across all quests
        /// </summary>
        public async Task ClearAllProgressAsync(ProfileType? profileType = null)
        {
            var targetProfile = profileType ?? _loadedProfile;
            ResetInMemoryProgress();
            await _persistenceQueue.ResetAsync(() =>
                _store.ClearAllAsync(targetProfile));
        }

        internal void ResetInMemoryProgress()
        {
            _objectiveProgress.Clear();
            ObjectiveProgressChanged?.Invoke(
                this,
                new ObjectiveProgressChangedEventArgs(string.Empty, -1, false));
        }

        public void ClearAllProgress()
        {
            ClearAllProgressAsync().GetAwaiter().GetResult();
        }

        public Task FlushPersistenceAsync() => _persistenceQueue.FlushAsync();

        internal Task BeginPersistenceResetAsync() => _persistenceQueue.BeginResetAsync();

        internal void EndPersistenceReset() => _persistenceQueue.EndReset();

        #endregion

        #region Persistence

        public void SaveObjectiveProgress()
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

        #endregion
    }

    /// <summary>
    /// Event args for objective progress changes
    /// </summary>
    public class ObjectiveProgressChangedEventArgs : EventArgs
    {
        public string? QuestNormalizedName { get; }
        public string? ObjectiveId { get; }
        public int ObjectiveIndex { get; }
        public bool IsCompleted { get; }

        public ObjectiveProgressChangedEventArgs(string questNormalizedNameOrId, int objectiveIndex, bool isCompleted)
        {
            if (questNormalizedNameOrId.StartsWith("id:"))
            {
                ObjectiveId = questNormalizedNameOrId.Substring(3);
                QuestNormalizedName = null;
            }
            else
            {
                QuestNormalizedName = questNormalizedNameOrId;
                ObjectiveId = null;
            }
            ObjectiveIndex = objectiveIndex;
            IsCompleted = isCompleted;
        }
    }
}
