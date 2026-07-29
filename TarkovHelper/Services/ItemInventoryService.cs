using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for managing user's item inventory quantities (FIR/Non-FIR)
    /// </summary>
    public class ItemInventoryService : IDisposable
    {
        private static readonly ILogger _log = Log.For<ItemInventoryService>();
        private static readonly object InstanceLock = new();
        private static ItemInventoryService? _instance;

        public static ItemInventoryService Instance
        {
            get
            {
                lock (InstanceLock)
                    return _instance ??= new ItemInventoryService();
            }
        }

        /// <summary>
        /// Flushes the current instance without creating a new one.
        /// </summary>
        public static Task FlushExistingAsync()
        {
            lock (InstanceLock)
                return _instance?.FlushPendingSavesAsync() ?? Task.CompletedTask;
        }

        /// <summary>
        /// Flushes and disposes the old singleton before a refreshed instance is used.
        /// </summary>
        public static void ResetInstance()
        {
            ItemInventoryService? previous;
            lock (InstanceLock)
            {
                previous = _instance;
                _instance = null;
            }

            previous?.Dispose();
        }

        private ProfileType _loadedProfile = ProfileType.Pvp;
        private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;
        private ItemInventoryData _inventoryData = new();
        private readonly object _lock = new();

        private System.Timers.Timer? _saveTimer;
        private readonly HashSet<string> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);
        private readonly PersistenceWriteQueue _persistenceQueue = new();
        private bool _disposed;

        public event EventHandler? InventoryChanged;

        private ItemInventoryService()
        {
            InitializeSaveTimer();
        }

        private void InitializeSaveTimer()
        {
            _saveTimer = new System.Timers.Timer(500)
            {
                AutoReset = false
            };
            _saveTimer.Elapsed += (_, _) => SavePendingItems();
        }

        private void SavePendingItems()
        {
            var itemsToSave = DrainPendingItems();
            if (itemsToSave.Count == 0)
                return;

            QueueSaveBatch(itemsToSave, _loadedProfile);
        }

        private List<string> DrainPendingItems()
        {
            lock (_lock)
            {
                if (_pendingSaves.Count == 0)
                    return [];

                var items = _pendingSaves.ToList();
                _pendingSaves.Clear();
                return items;
            }
        }

        private void QueueSaveBatch(IReadOnlyCollection<string> itemNames, ProfileType profile)
        {
            if (itemNames.Count == 0)
                return;

            var distinctNames = itemNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctNames.Length == 0)
                return;

            QueuePersistence(() => SaveItemsAsync(distinctNames, profile));
        }

        private void QueuePersistence(Func<Task> operation)
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

        private async Task SaveItemsAsync(
            IReadOnlyCollection<string> itemNames,
            ProfileType profile)
        {
            foreach (var itemName in itemNames)
            {
                int firQuantity;
                int nonFirQuantity;
                lock (_lock)
                {
                    if (_inventoryData.Items.TryGetValue(itemName, out var inventory))
                    {
                        firQuantity = inventory.FirQuantity;
                        nonFirQuantity = inventory.NonFirQuantity;
                    }
                    else
                    {
                        firQuantity = 0;
                        nonFirQuantity = 0;
                    }
                }

                await _userDataDb.SaveItemInventoryAsync(
                    itemName,
                    firQuantity,
                    nonFirQuantity,
                    profile).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Persists all queued changes and waits for previously queued saves.
        /// </summary>
        public async Task FlushPendingSavesAsync()
        {
            _saveTimer?.Stop();

            while (true)
            {
                var pendingItems = DrainPendingItems();
                if (pendingItems.Count > 0)
                    QueueSaveBatch(pendingItems, _loadedProfile);

                await _persistenceQueue.FlushAsync().ConfigureAwait(false);

                lock (_lock)
                {
                    if (_pendingSaves.Count == 0)
                        return;
                }
            }
        }

        /// <summary>
        /// Get inventory for a specific item
        /// </summary>
        public ItemInventory GetInventory(string itemNormalizedName)
        {
            lock (_lock)
            {
                if (_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                    return inventory;

                return new ItemInventory { ItemNormalizedName = itemNormalizedName };
            }
        }

        public int GetFirQuantity(string itemNormalizedName) =>
            GetInventory(itemNormalizedName).FirQuantity;

        public int GetNonFirQuantity(string itemNormalizedName) =>
            GetInventory(itemNormalizedName).NonFirQuantity;

        public int GetTotalQuantity(string itemNormalizedName) =>
            GetInventory(itemNormalizedName).TotalQuantity;

        public void SetFirQuantity(string itemNormalizedName, int quantity)
        {
            quantity = Math.Max(0, quantity);
            var changed = false;

            lock (_lock)
            {
                if (_disposed)
                    return;

                if (!_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                {
                    inventory = new ItemInventory { ItemNormalizedName = itemNormalizedName };
                    _inventoryData.Items[itemNormalizedName] = inventory;
                }

                if (inventory.FirQuantity != quantity)
                {
                    inventory.FirQuantity = quantity;
                    CleanupEmptyInventory(itemNormalizedName);
                    ScheduleSave(itemNormalizedName);
                    changed = true;
                }
            }

            if (changed)
                InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetNonFirQuantity(string itemNormalizedName, int quantity)
        {
            quantity = Math.Max(0, quantity);
            var changed = false;

            lock (_lock)
            {
                if (_disposed)
                    return;

                if (!_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                {
                    inventory = new ItemInventory { ItemNormalizedName = itemNormalizedName };
                    _inventoryData.Items[itemNormalizedName] = inventory;
                }

                if (inventory.NonFirQuantity != quantity)
                {
                    inventory.NonFirQuantity = quantity;
                    CleanupEmptyInventory(itemNormalizedName);
                    ScheduleSave(itemNormalizedName);
                    changed = true;
                }
            }

            if (changed)
                InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AdjustFirQuantity(string itemNormalizedName, int delta)
        {
            var current = GetFirQuantity(itemNormalizedName);
            SetFirQuantity(itemNormalizedName, current + delta);
        }

        public void AdjustNonFirQuantity(string itemNormalizedName, int delta)
        {
            var current = GetNonFirQuantity(itemNormalizedName);
            SetNonFirQuantity(itemNormalizedName, current + delta);
        }

        /// <summary>
        /// Consume several item requirements atomically in memory and persist each
        /// affected item through the serialized save queue. General requirements
        /// consume non-FIR stock first; FIR-only requirements consume FIR stock only.
        /// Quantities never become negative.
        /// </summary>
        public InventoryConsumptionResult ConsumeBatch(
            IEnumerable<InventoryConsumptionRequirement> requirements)
        {
            var requested = 0;
            var consumed = 0;
            var changed = false;

            lock (_lock)
            {
                if (_disposed)
                {
                    requested = requirements
                        .Where(requirement => requirement.Quantity > 0)
                        .Sum(requirement => requirement.Quantity);
                    return new InventoryConsumptionResult(requested, 0, requested);
                }

                foreach (var requirement in requirements)
                {
                    if (string.IsNullOrWhiteSpace(requirement.ItemNormalizedName) ||
                        requirement.Quantity <= 0)
                    {
                        continue;
                    }

                    requested += requirement.Quantity;
                    if (!_inventoryData.Items.TryGetValue(
                            requirement.ItemNormalizedName,
                            out var inventory))
                    {
                        continue;
                    }

                    var remaining = requirement.Quantity;
                    if (requirement.FirOnly)
                    {
                        var fromFir = Math.Min(inventory.FirQuantity, remaining);
                        inventory.FirQuantity -= fromFir;
                        remaining -= fromFir;
                        consumed += fromFir;
                    }
                    else
                    {
                        var fromNonFir = Math.Min(inventory.NonFirQuantity, remaining);
                        inventory.NonFirQuantity -= fromNonFir;
                        remaining -= fromNonFir;
                        consumed += fromNonFir;

                        var fromFir = Math.Min(inventory.FirQuantity, remaining);
                        inventory.FirQuantity -= fromFir;
                        remaining -= fromFir;
                        consumed += fromFir;
                    }

                    if (remaining != requirement.Quantity)
                    {
                        changed = true;
                        CleanupEmptyInventory(requirement.ItemNormalizedName);
                        _pendingSaves.Add(requirement.ItemNormalizedName);
                    }
                }

                if (changed)
                {
                    _inventoryData.LastUpdated = DateTime.UtcNow;
                    _saveTimer?.Stop();
                    _saveTimer?.Start();
                }
            }

            if (changed)
                InventoryChanged?.Invoke(this, EventArgs.Empty);

            return new InventoryConsumptionResult(
                requested,
                consumed,
                Math.Max(0, requested - consumed));
        }

        private void CleanupEmptyInventory(string itemNormalizedName)
        {
            if (_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory) &&
                inventory.FirQuantity == 0 && inventory.NonFirQuantity == 0)
            {
                _inventoryData.Items.Remove(itemNormalizedName);
            }
        }

        public ItemFulfillmentInfo GetFulfillmentInfo(
            string itemNormalizedName,
            int requiredTotal,
            int requiredFir)
        {
            var inventory = GetInventory(itemNormalizedName);

            return new ItemFulfillmentInfo
            {
                ItemNormalizedName = itemNormalizedName,
                RequiredTotal = requiredTotal,
                RequiredFir = requiredFir,
                OwnedFir = inventory.FirQuantity,
                OwnedNonFir = inventory.NonFirQuantity
            };
        }

        public IReadOnlyDictionary<string, ItemInventory> GetAllInventory()
        {
            lock (_lock)
            {
                return _inventoryData.Items.ToDictionary(
                    pair => pair.Key,
                    pair => new ItemInventory
                    {
                        ItemNormalizedName = pair.Value.ItemNormalizedName,
                        FirQuantity = pair.Value.FirQuantity,
                        NonFirQuantity = pair.Value.NonFirQuantity
                    },
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public (int TotalItems, int TotalFirCount, int TotalNonFirCount) GetStatistics()
        {
            lock (_lock)
            {
                var totalFir = _inventoryData.Items.Values.Sum(item => item.FirQuantity);
                var totalNonFir = _inventoryData.Items.Values.Sum(item => item.NonFirQuantity);
                return (_inventoryData.Items.Count, totalFir, totalNonFir);
            }
        }

        public async Task ResetAllInventoryAsync(ProfileType? profileType = null)
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

        internal void ResetInMemoryInventory()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _saveTimer?.Stop();
                _pendingSaves.Clear();
                _inventoryData = new ItemInventoryData();
            }

            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ResetAllInventory()
        {
            ResetAllInventoryAsync().GetAwaiter().GetResult();
        }

        public async Task ReloadInventoryAsync()
        {
            await FlushPendingSavesAsync();
            await LoadInventoryFromDbAsync();
            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ScheduleSave(string itemNormalizedName)
        {
            if (_disposed)
                return;

            _pendingSaves.Add(itemNormalizedName);
            _inventoryData.LastUpdated = DateTime.UtcNow;
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        public async Task InitializeAsync()
        {
            _loadedProfile = ProfileService.Instance.CurrentProfile;
            await LoadInventoryFromDbAsync();
        }

        public async Task LoadInventoryAsync()
        {
            await LoadInventoryFromDbAsync();
        }

        private void LoadInventory()
        {
            _ = LoadInventoryFromDbAsync();
        }

        private async Task LoadInventoryFromDbAsync()
        {
            var profile = _loadedProfile;
            try
            {
                var items = await _userDataDb.LoadItemInventoryAsync(profile).ConfigureAwait(false);
                var newData = new ItemInventoryData
                {
                    LastUpdated = DateTime.UtcNow,
                    Items = new Dictionary<string, ItemInventory>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var pair in items)
                {
                    newData.Items[pair.Key] = new ItemInventory
                    {
                        ItemNormalizedName = pair.Key,
                        FirQuantity = pair.Value.FirQuantity,
                        NonFirQuantity = pair.Value.NonFirQuantity
                    };
                }

                lock (_lock)
                    _inventoryData = newData;
            }
            catch (Exception ex)
            {
                _log.Error($"Load failed: {ex.Message}");
                lock (_lock)
                    _inventoryData = new ItemInventoryData();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _saveTimer?.Stop();
            }

            try
            {
                FlushPendingSavesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to flush item inventory during disposal", ex);
            }

            _saveTimer?.Dispose();
            _saveTimer = null;
            GC.SuppressFinalize(this);
        }
    }
}