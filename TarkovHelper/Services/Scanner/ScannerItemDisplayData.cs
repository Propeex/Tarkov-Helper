namespace TarkovHelper.Services.Scanner;

public sealed record ScannerItemDisplayData(
    string ItemId,
    string OfficialKoreanName,
    int? AverageFleaPrice,
    int? FleaPricePerSlot,
    string? BestTraderName,
    int? BestTraderPrice,
    bool IsKappaRequired,
    int QuestRequired,
    int HideoutRequired,
    int Owned,
    int AdditionalNeeded,
    DateTimeOffset? PriceUpdatedAt);
