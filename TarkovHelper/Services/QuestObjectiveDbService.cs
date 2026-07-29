using TarkovHelper.Models;
using TarkovHelper.Models.Map;

namespace TarkovHelper.Services;

/// <summary>
/// 지도 탭의 퀘스트 기능이 제거되어 빈 목표 집합만 제공하는 호환 서비스입니다.
/// 퀘스트 탭과 퀘스트 데이터베이스는 이 서비스에 의존하지 않습니다.
/// </summary>
public sealed class QuestObjectiveDbService
{
    private static QuestObjectiveDbService? _instance;
    public static QuestObjectiveDbService Instance => _instance ??= new QuestObjectiveDbService();

    private static readonly IReadOnlyList<QuestObjective> EmptyObjectives = Array.Empty<QuestObjective>();
    private bool _isLoaded;

    public bool IsLoaded => _isLoaded;
    public IReadOnlyList<QuestObjective> AllObjectives => EmptyObjectives;

    /// <summary>
    /// 기존 지도 코드와의 이진 호환을 위해 유지합니다.
    /// 지도 퀘스트 데이터는 더 이상 로드하지 않습니다.
    /// </summary>
    public event EventHandler? DataRefreshed;

    private QuestObjectiveDbService()
    {
        // 지도 탭에서 퀘스트 데이터베이스 갱신을 구독하지 않습니다.
    }

    public Task<bool> LoadObjectivesAsync()
    {
        _isLoaded = true;
        return Task.FromResult(true);
    }

    public async Task RefreshAsync()
    {
        await LoadObjectivesAsync();
        OnDataRefreshed();
    }

    public List<QuestObjective> GetObjectivesForMap(string mapKey, MapConfig mapConfig)
    {
        return new List<QuestObjective>();
    }

    private void OnDataRefreshed()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            DataRefreshed?.Invoke(this, EventArgs.Empty);
            return;
        }

        dispatcher.BeginInvoke(() => DataRefreshed?.Invoke(this, EventArgs.Empty));
    }
}
