using TarkovHelper.Models;

namespace TarkovHelper.Services.Scanner;

/// <summary>
/// 스캔된 아이템을 현재 프로필의 퀘스트·은신처·보유 데이터에 연결합니다.
/// 매 호출마다 현재 싱글턴을 조회하므로 프로필 전환 및 데이터베이스 갱신 후에도
/// 오래된 서비스 인스턴스를 보관하지 않습니다.
/// </summary>
internal static class ScannerItemContextService
{
    public static async Task<ScannerRequirementContext> BuildAsync(TarkovItem item)
    {
        var requirements = await ItemsDataService.Instance.GetAggregatedItemsAsync(ItemDbService.Instance.GetItemLookup());
        var aggregate = requirements.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemId, item.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.ItemNormalizedName, item.NormalizedName, StringComparison.OrdinalIgnoreCase));

        var inventory = ItemInventoryService.Instance;
        var owned = inventory.GetTotalQuantity(item.NormalizedName);
        var questRequired = aggregate?.QuestCount ?? 0;
        var hideoutRequired = aggregate?.HideoutCount ?? 0;
        var additionalNeeded = Math.Max(0, questRequired + hideoutRequired - owned);

        var questProgress = QuestProgressService.Instance;
        var kappaRequired = ItemsDataService.Instance
            .GetQuestSources(item.NormalizedName)
            .Any(source => source.Task?.ReqKappa == true &&
                source.Task != null &&
                questProgress.GetStatus(source.Task) is not (QuestStatus.Done or QuestStatus.Failed or QuestStatus.Unavailable));

        return new ScannerRequirementContext(
            questRequired,
            hideoutRequired,
            owned,
            additionalNeeded,
            kappaRequired);
    }
}

internal sealed record ScannerRequirementContext(
    int QuestRequired,
    int HideoutRequired,
    int Owned,
    int AdditionalNeeded,
    bool IsKappaRequired);
