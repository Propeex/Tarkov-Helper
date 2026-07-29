using System.Runtime.CompilerServices;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;

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

        if (OverlayClickThroughPolicy.ShouldToggle(
                isInitializing: true,
                currentState: true,
                requestedState: false))
        {
            throw new InvalidDataException(
                "Overlay click-through changed while the settings window was initializing.");
        }

        if (!OverlayClickThroughPolicy.ShouldToggle(
                isInitializing: false,
                currentState: true,
                requestedState: false))
        {
            throw new InvalidDataException(
                "Overlay click-through did not recognize an explicit disable request.");
        }

        if (OverlayClickThroughPolicy.ShouldToggle(
                isInitializing: false,
                currentState: false,
                requestedState: false))
        {
            throw new InvalidDataException(
                "Overlay click-through toggled despite an unchanged requested state.");
        }

        var mapQuestService = QuestObjectiveDbService.Instance;
        if (!mapQuestService.LoadObjectivesAsync().GetAwaiter().GetResult())
            throw new InvalidDataException("Map quest compatibility service failed to initialize.");

        if (mapQuestService.AllObjectives.Count != 0)
            throw new InvalidDataException("Map tab must not load quest objectives.");
    }

    private static void AssertEqual(string expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException($"Map UI regression smoke failed: expected '{expected}', actual '{actual}'.");
    }
}
