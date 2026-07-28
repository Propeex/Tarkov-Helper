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
        // DatabaseUpdated is dispatched asynchronously. Load the newly replaced
        // quest database explicitly before rebuilding profile-bound services so
        // QuestProgressService cannot retain the pre-update empty snapshot.
        await QuestDbService.Instance.LoadQuestsAsync();
        await RefreshCurrentProfileDataAsync();
    }
}
