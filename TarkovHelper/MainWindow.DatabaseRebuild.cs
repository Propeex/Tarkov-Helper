namespace TarkovHelper;

public partial class MainWindow
{
    /// <summary>
    /// Recreates profile-bound services and pages after tarkov_data.db has
    /// been replaced. The active profile is always PVP.
    /// </summary>
    internal Task ReloadAfterDatabaseRebuildAsync()
    {
        return RefreshCurrentProfileDataAsync();
    }
}
