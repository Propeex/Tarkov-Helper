using System.Windows.Controls;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;

namespace TarkovHelper.Pages.Map.Components;

/// <summary>
/// 지도 탭의 퀘스트 기능 제거 후 기존 MapPage 참조를 유지하기 위한 무동작 호환 클래스입니다.
/// 퀘스트 목표를 조회하거나 마커를 생성하지 않습니다.
/// </summary>
public sealed class MapQuestMarkerManager
{
    private readonly Canvas _markersContainer;
    private static readonly List<TaskObjectiveWithLocation> EmptyObjectives = new();

#pragma warning disable CS0067
    public event EventHandler<TaskObjectiveWithLocation>? ObjectiveSelected;
    public event EventHandler<TaskObjectiveWithLocation>? FloorChangeRequested;
    public event Action<string>? StatusUpdated;
#pragma warning restore CS0067

    public MapQuestMarkerManager(
        Canvas markersContainer,
        MapTrackerService trackerService,
        QuestObjectiveService objectiveService,
        QuestProgressService progressService,
        LocalizationService localizationService)
    {
        _markersContainer = markersContainer ?? throw new ArgumentNullException(nameof(markersContainer));

        ArgumentNullException.ThrowIfNull(trackerService);
        ArgumentNullException.ThrowIfNull(objectiveService);
        ArgumentNullException.ThrowIfNull(progressService);
        ArgumentNullException.ThrowIfNull(localizationService);

        _markersContainer.Children.Clear();
        _markersContainer.Visibility = System.Windows.Visibility.Collapsed;
        _markersContainer.IsHitTestVisible = false;
    }

    public void SetCurrentMap(string? mapKey)
    {
    }

    public void SetCurrentFloor(string? floorId)
    {
    }

    public void SetZoomLevel(double zoomLevel)
    {
    }

    public void SetShowQuestMarkers(bool show)
    {
    }

    public void SetQuestMarkerStyle(QuestMarkerStyle style)
    {
    }

    public void SetQuestNameTextSize(double size)
    {
    }

    public void SetHideCompletedObjectives(bool hide)
    {
    }

    public void SetSelectedObjective(TaskObjectiveWithLocation? objective)
    {
    }

    public List<TaskObjectiveWithLocation> GetCurrentMapObjectives()
    {
        return EmptyObjectives;
    }

    public Task RefreshMarkersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearMarkers();
        return Task.CompletedTask;
    }

    public void ClearMarkers()
    {
        _markersContainer.Children.Clear();
        _markersContainer.Visibility = System.Windows.Visibility.Collapsed;
        _markersContainer.IsHitTestVisible = false;
    }

    public void UpdateMarkerScales()
    {
    }
}
