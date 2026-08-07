using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Calculates the status exposed by the helper. There is no separate
/// acceptance-availability state: quests whose start conditions are satisfied
/// are displayed as Active, while explicit start records are used only when an
/// API prerequisite specifically requires an active predecessor.
/// </summary>
internal sealed class ActualQuestStatusEvaluator
{
    private readonly QuestProgressService _progressService;
    private readonly Dictionary<string, QuestStatus> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visiting =
        new(StringComparer.OrdinalIgnoreCase);

    public ActualQuestStatusEvaluator(QuestProgressService progressService)
    {
        _progressService = progressService;
    }

    public QuestStatus Evaluate(TarkovTask task)
    {
        var taskKey = GetTaskKey(task);
        if (_cache.TryGetValue(taskKey, out var cached))
            return cached;

        if (!_visiting.Add(taskKey))
            return QuestStatus.Locked;

        try
        {
            var storedStatus = _progressService.GetStatus(task);
            if (storedStatus is QuestStatus.Done or QuestStatus.Failed ||
                storedStatus == QuestStatus.Active && _progressService.HasExplicitStart(task))
            {
                return Cache(taskKey, storedStatus);
            }

            if (!_progressService.IsEditionRequirementMet(task) ||
                !_progressService.IsPrestigeLevelRequirementMet(task) ||
                !_progressService.IsFactionRequirementMet(task))
            {
                return Cache(taskKey, QuestStatus.Unavailable);
            }

            if (!_progressService.IsDspRequirementMet(task) ||
                !ArePrerequisitesMet(task))
            {
                return Cache(taskKey, QuestStatus.Locked);
            }

            if (!_progressService.IsLevelRequirementMet(task) ||
                !_progressService.IsScavKarmaRequirementMet(task))
            {
                return Cache(taskKey, QuestStatus.LevelLocked);
            }

            return Cache(taskKey, QuestStatus.Active);
        }
        finally
        {
            _visiting.Remove(taskKey);
        }
    }

    private bool ArePrerequisitesMet(TarkovTask task)
    {
        if (task.TaskRequirements is { Count: > 0 })
        {
            var andRequirements = task.TaskRequirements
                .Where(requirement => requirement.GroupId == 0);

            foreach (var requirement in andRequirements)
            {
                if (!IsRequirementSatisfied(requirement))
                    return false;
            }

            var orGroups = task.TaskRequirements
                .Where(requirement => requirement.GroupId > 0)
                .GroupBy(requirement => requirement.GroupId);

            foreach (var group in orGroups)
            {
                if (!group.Any(IsRequirementSatisfied))
                    return false;
            }

            return true;
        }

        if (task.Previous is not { Count: > 0 })
            return true;

        foreach (var previousName in task.Previous)
        {
            var previousTask = _progressService.GetTask(previousName) ??
                               _progressService.GetTaskById(previousName);
            if (previousTask == null || Evaluate(previousTask) != QuestStatus.Done)
                return false;
        }

        return true;
    }

    private bool IsRequirementSatisfied(TaskRequirement requirement)
    {
        var requiredTask = !string.IsNullOrWhiteSpace(requirement.TaskId)
            ? _progressService.GetTaskById(requirement.TaskId)
            : null;

        requiredTask ??= !string.IsNullOrWhiteSpace(requirement.TaskNormalizedName)
            ? _progressService.GetTask(requirement.TaskNormalizedName)
            : null;

        return requiredTask != null &&
               _progressService.IsTaskRequirementSatisfied(requiredTask, requirement.Status);
    }

    private static string GetTaskKey(TarkovTask task)
    {
        return task.Ids?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
               ?? task.NormalizedName
               ?? $"task:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(task)}";
    }

    private QuestStatus Cache(string taskKey, QuestStatus status)
    {
        _cache[taskKey] = status;
        return status;
    }
}
