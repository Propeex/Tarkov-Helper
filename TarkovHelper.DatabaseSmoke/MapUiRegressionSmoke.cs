using System.Runtime.CompilerServices;
using TarkovHelper.Models.Map;

internal static class MapUiRegressionSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        AssertEqual("지하 2층", MapFloorConfig.GetLocalizedDisplayName("Basement 2", "basement2", -2));
        AssertEqual("지하 1층", MapFloorConfig.GetLocalizedDisplayName("Basement", "basement", -1));
        AssertEqual("지상층", MapFloorConfig.GetLocalizedDisplayName("Main Floor", "main", 0));
        AssertEqual("2층", MapFloorConfig.GetLocalizedDisplayName("Level 2", "level2", 1));
        AssertEqual("옥상", MapFloorConfig.GetLocalizedDisplayName("Rooftop", "roof", 3));

        var objective = new TaskObjectiveWithLocation
        {
            QuestId = "shipping-delay-db-id",
            QuestBsgId = "673f348dd3346c21670217e7",
            TaskName = "Shipping Delay - Part 1",
            TaskNameKo = "배송 지연 - 파트 1",
            Description = "Hand over the package",
            DescriptionKo = "화물을 건네주십시오"
        };

        AssertEqual("shipping-delay-db-id", objective.QuestId);
        AssertEqual("673f348dd3346c21670217e7", objective.QuestBsgId);
        AssertEqual("Shipping Delay - Part 1", objective.TaskName);
        if (objective.TaskNameKo != null)
            throw new InvalidDataException("Map quest title localization must remain disabled.");
        AssertEqual("화물을 건네주십시오", objective.DescriptionKo);
    }

    private static void AssertEqual(string expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException($"Map UI regression smoke failed: expected '{expected}', actual '{actual}'.");
    }
}