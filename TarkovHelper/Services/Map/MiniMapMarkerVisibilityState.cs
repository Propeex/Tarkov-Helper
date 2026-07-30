using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Services.Map;

/// <summary>
/// Immutable snapshot of the map-tab marker visibility settings used by the
/// overlay minimap. Capturing once per refresh prevents mixed old/new filters
/// when the user toggles several marker categories quickly.
/// </summary>
public readonly record struct MiniMapMarkerVisibilityState(
    bool ShowPmcSpawns,
    bool ShowSniperScavs,
    bool ShowRogues,
    bool ShowCultists,
    bool ShowLevers,
    bool ShowBosses,
    bool ShowExtracts,
    bool ShowPmcExtracts,
    bool ShowScavExtracts,
    bool ShowTransits)
{
    public static MiniMapMarkerVisibilityState Capture(MapSettings markerSettings)
    {
        ArgumentNullException.ThrowIfNull(markerSettings);

        // Standard marker toggles are owned by MapSettings. Extract toggles on
        // MapPage are still owned by SettingsService, so read that same live
        // source instead of a separately cached MapSettings copy.
        var applicationSettings = SettingsService.Instance;
        return new MiniMapMarkerVisibilityState(
            markerSettings.ShowPmcSpawns,
            markerSettings.ShowSniperScavs,
            markerSettings.ShowRogues,
            markerSettings.ShowCultists,
            markerSettings.ShowLevers,
            markerSettings.ShowBosses,
            applicationSettings.MapShowExtracts,
            applicationSettings.MapShowPmcExtracts,
            applicationSettings.MapShowScavExtracts,
            applicationSettings.MapShowTransits);
    }

    public bool IsMapMarkerVisible(MarkerType type) => type switch
    {
        MarkerType.PmcSpawn => ShowPmcSpawns,
        MarkerType.SniperScavSpawn => ShowSniperScavs,
        MarkerType.RogueSpawn => ShowRogues,
        MarkerType.CultistSpawn => ShowCultists,
        MarkerType.Lever => ShowLevers,
        MarkerType.BossSpawn => ShowBosses,
        _ => false
    };

    public bool IsExtractVisible(ExtractFaction faction)
    {
        if (!ShowExtracts)
            return false;

        return faction switch
        {
            ExtractFaction.Pmc => ShowPmcExtracts,
            ExtractFaction.Scav => ShowScavExtracts,
            ExtractFaction.Transit => ShowTransits,
            // MapPage classifies shared extracts with the PMC category.
            ExtractFaction.Shared => ShowPmcExtracts,
            _ => true
        };
    }

    /// <summary>
    /// Unknown floor detection must not be treated as the main floor. Some maps
    /// contain floor-tagged markers without detection ranges, so an unknown floor
    /// keeps every marker fully visible until a reliable floor is available.
    /// </summary>
    public static bool IsCurrentFloor(string? markerFloorId, string? detectedFloorId)
    {
        if (string.IsNullOrWhiteSpace(detectedFloorId))
            return true;

        var effectiveMarkerFloor = string.IsNullOrWhiteSpace(markerFloorId)
            ? "main"
            : markerFloorId;
        return string.Equals(
            effectiveMarkerFloor,
            detectedFloorId,
            StringComparison.OrdinalIgnoreCase);
    }
}
