using TarkovHelper.Models;
using TarkovHelper.Pages;

internal static class ItemFulfillmentRegressionSmoke
{
    public static void Run()
    {
        VerifyMixedRequirementRejectsFirOnlyInventory();
        VerifyMixedRequirementAcceptsExactInventory();
        VerifySurplusFirCanSatisfyUnrestrictedRemainder();
        VerifyTotalQuantityCannotReplaceMissingFir();
        VerifyNonFirOnlyRequirementStillUsesTotalInventory();
    }

    private static void VerifyMixedRequirementRejectsFirOnlyInventory()
    {
        const int requiredTotal = 15;
        const int requiredFir = 5;

        var info = CreateInfo(requiredTotal, requiredFir, ownedFir: 5, ownedNonFir: 0);
        AssertStatus(info.Status, ItemFulfillmentStatus.PartiallyFulfilled,
            "Mixed requirement was completed by the FIR subset alone.");
        AssertClose(info.ProgressPercent, 100.0 / 3.0,
            "Mixed requirement progress double-counted FIR inventory.");

        var item = new AggregatedItemViewModel
        {
            TotalCount = requiredTotal,
            TotalFIRCount = requiredFir,
            OwnedFirQuantity = 5,
            OwnedNonFirQuantity = 0
        };
        AssertStatus(item.FulfillmentStatus, ItemFulfillmentStatus.PartiallyFulfilled,
            "Items page completed a mixed requirement by the FIR subset alone.");

        var collector = new CollectorItemViewModel
        {
            TotalCount = requiredTotal,
            TotalFIRCount = requiredFir,
            OwnedFirQuantity = 5,
            OwnedNonFirQuantity = 0
        };
        AssertStatus(collector.FulfillmentStatus, ItemFulfillmentStatus.PartiallyFulfilled,
            "Collector model completed a mixed requirement by the FIR subset alone.");

        var integrated = new IntegratedItemRequirement
        {
            QuestRequired = requiredTotal,
            QuestRequiredFir = requiredFir,
            OwnedFir = 5,
            OwnedNonFir = 0
        };
        if (integrated.IsFulfilled)
            throw new InvalidDataException(
                "Integrated item model completed a mixed requirement by the FIR subset alone.");
        AssertClose(integrated.Progress, 1.0 / 3.0,
            "Integrated item progress double-counted FIR inventory.");
    }

    private static void VerifyMixedRequirementAcceptsExactInventory()
    {
        var info = CreateInfo(requiredTotal: 15, requiredFir: 5, ownedFir: 5, ownedNonFir: 10);
        AssertStatus(info.Status, ItemFulfillmentStatus.Fulfilled,
            "Exact mixed inventory did not complete the requirement.");
        AssertClose(info.ProgressPercent, 100,
            "Exact mixed inventory did not reach 100 percent progress.");
    }

    private static void VerifySurplusFirCanSatisfyUnrestrictedRemainder()
    {
        var info = CreateInfo(requiredTotal: 15, requiredFir: 5, ownedFir: 15, ownedNonFir: 0);
        AssertStatus(info.Status, ItemFulfillmentStatus.Fulfilled,
            "Surplus FIR inventory did not satisfy the unrestricted remainder.");
        AssertClose(info.ProgressPercent, 100,
            "Surplus FIR inventory did not reach 100 percent progress.");
    }

    private static void VerifyTotalQuantityCannotReplaceMissingFir()
    {
        var info = CreateInfo(requiredTotal: 15, requiredFir: 5, ownedFir: 4, ownedNonFir: 11);
        AssertStatus(info.Status, ItemFulfillmentStatus.PartiallyFulfilled,
            "Total inventory replaced a missing FIR requirement.");
        AssertClose(info.ProgressPercent, 14.0 / 15.0 * 100,
            "Missing FIR progress was calculated incorrectly.");
    }

    private static void VerifyNonFirOnlyRequirementStillUsesTotalInventory()
    {
        var info = CreateInfo(requiredTotal: 10, requiredFir: 0, ownedFir: 5, ownedNonFir: 5);
        AssertStatus(info.Status, ItemFulfillmentStatus.Fulfilled,
            "Unrestricted requirement no longer accepts combined inventory.");
        AssertClose(info.ProgressPercent, 100,
            "Unrestricted requirement did not reach 100 percent progress.");
    }

    private static ItemFulfillmentInfo CreateInfo(
        int requiredTotal,
        int requiredFir,
        int ownedFir,
        int ownedNonFir) => new()
    {
        RequiredTotal = requiredTotal,
        RequiredFir = requiredFir,
        OwnedFir = ownedFir,
        OwnedNonFir = ownedNonFir
    };

    private static void AssertStatus(
        ItemFulfillmentStatus actual,
        ItemFulfillmentStatus expected,
        string message)
    {
        if (actual != expected)
            throw new InvalidDataException($"{message} expected={expected}, actual={actual}.");
    }

    private static void AssertClose(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 0.001)
            throw new InvalidDataException($"{message} expected={expected:F3}, actual={actual:F3}.");
    }
}
