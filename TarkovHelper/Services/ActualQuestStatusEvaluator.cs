using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Separates quests that are merely available to accept from quests that were
/// actually started in the game. Eligibility still observes prerequisite,
/// player-level, karma, edition, prestige, faction, and DSP requirements.
/// </summary>
internal sealed class ActualQuestStatusEvaluator
{
    private readonly QuestProgressService _progressService;
    private readonly HashSet<string> _startedQuestKeys;
    private readonly Dictionary<string, QuestStatus> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visiting =
        new(StringComparer.OrdinalIgnoreCase);

    public ActualQuestStatusEvaluator(
        QuestProgressService progressService,
        IEnumerable<string> startedQuestKeys)
    {
        _progressService = progressService;
        _startedQuestKeys = startedQuestKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            if (storedStatus is QuestStatus.Done or QuestStatus.Failed)
                return Cache(taskKey, storedStatus);

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

            return Cache(
                taskKey,
                IsActuallyStarted(task)
                    ? QuestStatus.Active
                    : QuestStatus.Available);
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

        // Missing prerequisite references must not silently unlock a quest.
        if (requiredTask == null)
            return false;

        var actualStatus = Evaluate(requiredTask);
        var requiredStatuses = requirement.Status;
        if (requiredStatuses == null || requiredStatuses.Count == 0)
            return actualStatus == QuestStatus.Done;

        foreach (var requiredStatus in requiredStatuses)
        {
            switch (requiredStatus.Trim().ToLowerInvariant())
            {
                case "active":
                case "start":
                case "accept":
                    if (actualStatus is QuestStatus.Active or QuestStatus.Done)
                        return true;
                    break;

                case "complete":
                    if (actualStatus == QuestStatus.Done)
                        return true;
                    break;

                case "failed":
                case "fail":
                    if (actualStatus == QuestStatus.Failed)
                        return true;
                    break;
            }
        }

        return false;
    }

    private bool IsActuallyStarted(TarkovTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.NormalizedName) &&
            _startedQuestKeys.Contains(task.NormalizedName))
        {
            return true;
        }

        return task.Ids?.Any(id =>
            !string.IsNullOrWhiteSpace(id) &&
            _startedQuestKeys.Contains(id)) == true;
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
