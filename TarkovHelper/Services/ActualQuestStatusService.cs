using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Shared source of truth for calculated quest status. Eligibility is treated
/// as Active because the helper intentionally has no separate accept/start
/// action. The asynchronous methods remain as compatibility no-ops for older
/// callers that initialized status from game logs.
/// </summary>
public sealed class ActualQuestStatusService
{
    private static ActualQuestStatusService? _instance;
    public static ActualQuestStatusService Instance => _instance ??= new ActualQuestStatusService();

#pragma warning disable CS0067
    public event EventHandler? StatusChanged;
#pragma warning restore CS0067

    private ActualQuestStatusService()
    {
    }

    public Task EnsureInitializedAsync() => Task.CompletedTask;

    public Task RefreshFromLogsAsync() => Task.CompletedTask;

    internal ActualQuestStatusEvaluator CreateEvaluator() =>
        new(QuestProgressService.Instance);

    public QuestStatus GetStatus(TarkovTask task) => CreateEvaluator().Evaluate(task);
}
