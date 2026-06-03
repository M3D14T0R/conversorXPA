using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static HashSet<int> ResolveRequestedTaskOrdinals(
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<string> requestedTaskNames,
        bool withDependencies)
    {
        var selected = new HashSet<int>();
        var tasksByOrdinal = allTasks.ToDictionary(t => t.Ordinal);
        var seedMatches = allTasks
            .Where(t => requestedTaskNames.Any(name => TaskMatchesRequestedFilter(t, name)))
            .ToList();
        var seedTasks = seedMatches
            .Where(t => !t.ParentOrdinal.HasValue)
            .ToList();
        if (seedTasks.Count == 0)
            seedTasks = seedMatches;

        foreach (var task in seedTasks)
            IncludeTaskWithStructure(task, allTasks, tasksByOrdinal, selected);

        if (!withDependencies || selected.Count == 0)
            return selected;

        var queue = new Queue<int>(selected);
        while (queue.Count > 0)
        {
            var currentOrdinal = queue.Dequeue();
            if (!tasksByOrdinal.TryGetValue(currentOrdinal, out var task))
                continue;

            foreach (var dependency in ResolveStrongTaskDependencies(task, allTasks, tasksByOrdinal))
            {
                var before = selected.Count;
                IncludeTaskWithStructure(dependency, allTasks, tasksByOrdinal, selected);
                if (selected.Count > before)
                {
                    foreach (var addedOrdinal in selected.Where(o => o == dependency.Ordinal || IsRelatedTo(dependency.Ordinal, o, tasksByOrdinal)))
                        queue.Enqueue(addedOrdinal);
                }
            }
        }

        return selected;
    }

    private static IEnumerable<TaskSemantic> ResolveStrongTaskDependencies(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyDictionary<int, TaskSemantic> tasksByOrdinal)
    {
        foreach (var binding in task.View.SubformBindings)
        {
            if (tasksByOrdinal.TryGetValue(binding.TargetTaskOrdinal, out var subformTask))
                yield return subformTask;
        }

        foreach (var call in EnumerateTaskCalls(task))
        {
            var targetTask = ResolveTaskByCall(task, call, allTasks);
            if (targetTask is not null)
                yield return targetTask;
        }
    }

    private static bool TaskMatchesRequestedFilter(TaskSemantic task, string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return false;

        var requestedTrimmed = requestedName.Trim();
        foreach (var candidate in EnumerateTaskFilterCandidates(task))
        {
            if (System.String.Equals(candidate?.Trim(), requestedTrimmed, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var normalizedFilter = NormalizeTaskName(requestedTrimmed);
        if (string.IsNullOrWhiteSpace(normalizedFilter))
            return false;

        var requestedHasMarker = HasLeadingTaskMarker(requestedTrimmed);
        foreach (var candidate in EnumerateTaskFilterCandidates(task))
        {
            if (!System.String.Equals(NormalizeTaskName(candidate), normalizedFilter, System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasLeadingTaskMarker(candidate) == requestedHasMarker)
                return true;
        }

        return false;
    }

    private static bool HasLeadingTaskMarker(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           System.Text.RegularExpressions.Regex.IsMatch(value, @"^\s*[-=<>\.]+");

    private static IEnumerable<string> EnumerateTaskFilterCandidates(TaskSemantic task)
    {
        if (!string.IsNullOrWhiteSpace(task.Description))
            yield return task.Description;
        if (!string.IsNullOrWhiteSpace(task.Name))
            yield return task.Name;
        if (!string.IsNullOrWhiteSpace(task.PublicName))
            yield return task.PublicName!;
        if (!string.IsNullOrWhiteSpace(task.FormName))
            yield return task.FormName!;
    }
}

