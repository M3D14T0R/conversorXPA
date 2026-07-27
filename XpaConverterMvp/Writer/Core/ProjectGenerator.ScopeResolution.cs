using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static ProjectGenerationScope ResolveGenerationScope(
        ProjectSemantic parsed,
        string? folderFilter,
        IReadOnlyList<string>? taskFilters,
        bool withTaskDependencies,
        bool forceTaskScopedGeneration)
    {
        var normalizedFolderFilter = ResolveTaskOutputFolder(folderFilter);
        var normalizedTaskFilters = (taskFilters ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasTaskFilters = normalizedTaskFilters.Count > 0;
        var taskScopedGeneration = forceTaskScopedGeneration || hasTaskFilters;
        var explicitTaskMatches = hasTaskFilters
            ? parsed.Tasks
                .Where(t => normalizedTaskFilters.Any(name => TaskMatchesRequestedFilter(t, name)))
                .ToList()
            : new List<TaskSemantic>();
        var explicitTaskSeeds = explicitTaskMatches
            .Where(t => !t.ParentOrdinal.HasValue)
            .ToList();
        if (explicitTaskSeeds.Count == 0)
            explicitTaskSeeds = explicitTaskMatches;
        var explicitlyRequestedTaskOrdinals = hasTaskFilters
            ? explicitTaskSeeds.Select(t => t.Ordinal).ToHashSet()
            : null;
        var selectedTaskOrdinals = hasTaskFilters
            ? ResolveRequestedTaskOrdinals(parsed.Tasks, normalizedTaskFilters, withTaskDependencies)
            : null;

        if (hasTaskFilters && (explicitlyRequestedTaskOrdinals?.Count ?? 0) == 0)
        {
            var requested = string.Join(", ", normalizedTaskFilters.Select(NormalizeTaskName));
            var similar = parsed.Tasks
                .Where(t =>
                {
                    var normalized = NormalizeTaskName(t.Description);
                    return normalizedTaskFilters.Any(name =>
                        normalized.Contains(NormalizeTaskName(name), StringComparison.OrdinalIgnoreCase) ||
                        NormalizeTaskName(name).Contains(normalized, StringComparison.OrdinalIgnoreCase));
                })
                .Take(12)
                .Select(t => $"{t.Description}#{t.Ordinal}")
                .ToList();
            LogProgress($"Stage: scoped requested names -> {requested}");
            LogProgress($"Stage: scoped match miss -> tasks={parsed.Tasks.Count} similar={string.Join(", ", similar)}");
        }

        return new ProjectGenerationScope
        {
            NormalizedFolderFilter = normalizedFolderFilter,
            NormalizedTaskFilters = normalizedTaskFilters,
            TaskScopedGeneration = taskScopedGeneration,
            WithTaskDependencies = withTaskDependencies,
            ExplicitlyRequestedTaskOrdinals = explicitlyRequestedTaskOrdinals,
            SelectedTaskOrdinals = selectedTaskOrdinals
        };
    }

    private static List<TaskSemantic> ResolveGeneratedTasks(ProjectSemantic parsed, ProjectGenerationScope scope)
    {
        var taskIndex = parsed.Tasks.ToDictionary(t => t.Ordinal);

        bool ShouldGenerateForFolder(TaskSemantic task)
            => string.IsNullOrWhiteSpace(scope.NormalizedFolderFilter)
               || string.Equals(ResolveEffectiveTaskOutputFolder(task, parsed.Tasks), scope.NormalizedFolderFilter, StringComparison.OrdinalIgnoreCase);

        bool ShouldGenerateForTask(TaskSemantic task)
        {
            if (scope.SelectedTaskOrdinals is not null)
                return scope.SelectedTaskOrdinals.Contains(task.Ordinal);

            // A range-reduced XML keeps the main program only as semantic
            // context for application events and resources. Do not emit that
            // application subtree as part of the isolated executable.
            if (scope.TaskScopedGeneration)
            {
                var current = task;
                while (true)
                {
                    if (current.MainProgram)
                        return false;
                    if (!current.ParentOrdinal.HasValue ||
                        !taskIndex.TryGetValue(current.ParentOrdinal.Value, out current))
                    {
                        break;
                    }
                }
            }

            return true;
        }

        var generatedTasks = parsed.Tasks
            .Where(t => ShouldGenerateForTarget(t) && ShouldGenerateForFolder(t) && ShouldGenerateForTask(t))
            .ToList();

        if (scope.TaskScopedGeneration)
        {
            var selectedCount = scope.SelectedTaskOrdinals?.Count ?? 0;
            var explicitCount = scope.ExplicitlyRequestedTaskOrdinals?.Count ?? 0;
            var targetCount = parsed.Tasks.Count(t => ShouldGenerateForTarget(t));
            var folderCount = parsed.Tasks.Count(t => ShouldGenerateForTarget(t) && ShouldGenerateForFolder(t));
            LogProgress($"Stage: scoped selection stats -> selected={selectedCount} explicit={explicitCount} target={targetCount} target+folder={folderCount} generated={generatedTasks.Count}");
        }

        if (scope.TaskScopedGeneration && generatedTasks.Count == 0)
        {
            var fallbackSelectedOrdinals = new HashSet<int>();
            var tasksByOrdinal = parsed.Tasks.ToDictionary(t => t.Ordinal);
            var fallbackSeeds = parsed.Tasks
                .Where(t => scope.NormalizedTaskFilters.Any(name => TaskMatchesRequestedFilter(t, name)))
                .ToList();
            foreach (var task in fallbackSeeds)
                IncludeTaskWithStructure(task, parsed.Tasks, tasksByOrdinal, fallbackSelectedOrdinals);
            generatedTasks = parsed.Tasks
                .Where(t => fallbackSelectedOrdinals.Contains(t.Ordinal) && ShouldGenerateForTarget(t) && ShouldGenerateForFolder(t))
                .ToList();

            var fallbackNames = parsed.Tasks
                .Where(t => fallbackSelectedOrdinals.Contains(t.Ordinal))
                .Take(8)
                .Select(t => $"{t.Description}#{t.Ordinal}")
                .ToList();
            LogProgress($"Stage: scoped fallback stats -> selected={fallbackSelectedOrdinals.Count} generated={generatedTasks.Count} sample={string.Join(", ", fallbackNames)}");
        }

        return generatedTasks;
    }
}
