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
    public static MiniMapMarkerVisibilityState Capture(MapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MiniMapMarkerVisibilityState(
            settings.ShowPmcSpawns,
            settings.ShowSniperScavs,
            settings.ShowRogues,
            settings.ShowCultists,
            settings.ShowLevers,
            settings.ShowBosses,
            settings.ShowExtracts,
            settings.ShowPmcExtracts,
            settings.ShowScavExtracts,
            settings.ShowTransits);
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
            ExtractFaction.Shared => ShowPmcExtracts || ShowScavExtracts,
            _ => true
        };
    }
}
