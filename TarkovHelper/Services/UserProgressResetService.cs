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
        await ProfileScopedObjectiveProgressStore.Instance.ClearAllAsync(profile);
        await database.ClearAllHideoutProgressAsync(profile);
        await database.ClearAllItemInventoryAsync(profile);
    }

    private static async Task<(int Quests, int Objectives, int Hideout, int Inventory)> GetRowCountsAsync(
        ProfileType profile)
    {
        var database = UserDataDbService.Instance;
        var quests = await database.LoadQuestProgressAsync(profile);
        var objectives = await ProfileScopedObjectiveProgressStore.Instance.LoadAsync(profile);
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
