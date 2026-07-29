using TarkovHelper.Services;

namespace TarkovHelper;

public partial class MainWindow
{
    /// <summary>
    /// Recreates profile-bound services and pages after tarkov_data.db has
    /// been replaced. The active profile is always PVP.
    /// </summary>
    internal async Task ReloadAfterDatabaseRebuildAsync()
    {
        // A completion clicked immediately before a manual DB rebuild may still be
        // waiting for durable user-progress confirmation. Finish that operation before
        // profile-bound inventory services are reset and recreated.
        await InventoryConsumptionService.FlushExistingAsync();
        await ItemInventoryService.FlushExistingAsync();

        // DatabaseUpdated is dispatched only after this method completes. Reload the
        // reference-data caches first so newly constructed pages cannot capture the
        // pre-rebuild item lookup or an empty quest snapshot.
        await ItemDbService.Instance.LoadItemsAsync();
        await QuestDbService.Instance.LoadQuestsAsync();
        await RefreshCurrentProfileDataAsync();
    }
}
