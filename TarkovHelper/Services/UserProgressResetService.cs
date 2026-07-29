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

            await Task.WhenAll(
                QuestProgressService.Instance.ResetAllProgressAsync(profile),
                HideoutProgressService.Instance.ResetAllProgressAsync(profile));
            await ItemInventoryService.Instance.ResetAllInventoryAsync(profile);

            ResetAllInMemoryServices();
            var counts = await GetRowCountsAsync(profile);
            if (counts != (0, 0, 0, 0))
            {
                throw new InvalidDataException(
                    $"Reset verification failed: quests={counts.Quests}, " +
                    $"objectives={counts.Objectives}, hideout={counts.Hideout}, " +
                    $"inventory={counts.Inventory}.");
            }
        }
        finally
        {
            _resetGate.Release();
        }
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
