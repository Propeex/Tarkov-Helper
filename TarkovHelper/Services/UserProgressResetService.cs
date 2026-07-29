using TarkovHelper.Models;

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
        try
        {
            var profile = ProfileService.Instance.CurrentProfile;

            await ItemInventoryService.Instance.ResetAllInventoryAsync(profile);
            await QuestProgressService.Instance.ResetAllProgressAsync(profile);
            await HideoutProgressService.Instance.ResetAllProgressAsync(profile);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                await ClearAllDatabaseRowsAsync(profile);
                ResetAllInMemoryServices();
                await Task.Delay(250);

                var firstCheck = await GetRowCountsAsync(profile);
                if (!IsEmpty(firstCheck))
                    continue;

                // A previously queued save can arrive just after the first empty read.
                // Require a second empty read after a stability window before reporting success.
                await Task.Delay(350);
                var stabilityCheck = await GetRowCountsAsync(profile);
                if (IsEmpty(stabilityCheck))
                    return;
            }

            var finalCounts = await GetRowCountsAsync(profile);
            throw new InvalidDataException(
                $"Reset verification failed: quests={finalCounts.Quests}, " +
                $"objectives={finalCounts.Objectives}, hideout={finalCounts.Hideout}, " +
                $"inventory={finalCounts.Inventory}.");
        }
        finally
        {
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
