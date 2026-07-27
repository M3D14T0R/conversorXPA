using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static HashSet<int> ResolveReferencedStructuralTopLevelTasks(
        IReadOnlyList<TaskSemantic> tasks)
    {
        var result = new HashSet<int>(_subformTargetTaskOrdinals);
        foreach (var owner in tasks)
        {
            foreach (var call in EnumerateTaskCalls(owner))
            {
                var target = ResolveTaskByCall(owner, call, tasks);
                if (target is not null &&
                    target.ParentOrdinal is null &&
                    IsStructuralTask(target))
                    result.Add(target.Ordinal);
            }

            if (owner.Form?.Controls is not null)
            {
                foreach (var control in owner.Form.Controls)
                {
                    if (!control.SelectProgramObj.HasValue ||
                        control.SelectProgramComponentId.GetValueOrDefault() > 0)
                        continue;

                    var target = tasks.FirstOrDefault(candidate =>
                        !candidate.ParentOrdinal.HasValue &&
                        candidate.TopLevelProgramIndex == control.SelectProgramObj.Value);
                    if (target is not null && IsStructuralTask(target))
                        result.Add(target.Ordinal);
                }
            }

            foreach (var binding in owner.View.SubformBindings)
            {
                if (binding.Kind != ViewSubformBindingKind.Task || !binding.TargetTaskOrdinal.HasValue)
                    continue;

                var target = tasks.FirstOrDefault(t => t.Ordinal == binding.TargetTaskOrdinal.Value);
                if (target is not null &&
                    target.ParentOrdinal is null &&
                    IsStructuralTask(target))
                    result.Add(target.Ordinal);
            }
        }
        return result;
    }

    private static TaskIoDef? ResolveOwnedMergeIoDefinition(TaskSemantic owner)
    {
        EnsureOwnedMergeIoDefinitionCache();
        return _state.OwnedMergeIoDefinitionCache.GetValueOrDefault(owner.Ordinal);
    }

    private static TaskIoDef? ResolveMergeIoDefinition(TaskSemantic task)
    {
        var mergeFormIndexes = task.Layout.MergeForms
            .Select(form => form.Index)
            .ToHashSet();
        var mergeFormIo = task.FormIos.FirstOrDefault(io =>
            io.FormEntryIndex.HasValue &&
            mergeFormIndexes.Contains(io.FormEntryIndex.Value) &&
            io.IoDeviceIndex.GetValueOrDefault() > 0);
        var ioIndex = mergeFormIo?.IoDeviceIndex.GetValueOrDefault() - 1 ?? -1;
        if (ioIndex >= 0 && ioIndex < task.Ios.Count)
            return task.Ios[ioIndex];
        if (task.Io is not null)
            return task.Io;
        return task.Ios.Count == 1 ? task.Ios[0] : null;
    }

    private static void EnsureOwnedMergeIoDefinitionCache()
    {
        if (_state.OwnedMergeIoDefinitionCacheInitialized)
            return;

        lock (_state.OwnedMergeIoDefinitionCacheLock)
        {
            if (_state.OwnedMergeIoDefinitionCacheInitialized)
                return;

            var result = new Dictionary<int, TaskIoDef>();
            var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
            foreach (var task in allTasks)
            {
                var local = ResolveMergeIoDefinition(task);
                if (HasMergeLayout(task) && local is not null)
                    result.TryAdd(task.Ordinal, local);
            }

            foreach (var candidate in allTasks)
            {
                if (candidate.Layout.MergeForms.Count == 0)
                    continue;

                var mergeFormIndexes = candidate.Layout.MergeForms.Select(form => form.Index).ToHashSet();
                foreach (var formIo in candidate.FormIos)
                {
                    if (!formIo.FormEntryIndex.HasValue ||
                        !mergeFormIndexes.Contains(formIo.FormEntryIndex.Value) ||
                        formIo.IoDeviceParent.GetValueOrDefault() <= 0 ||
                        !TryResolveIoDeviceAncestor(
                            candidate,
                            formIo.IoDeviceParent.GetValueOrDefault(),
                            out var ancestor,
                            out _) ||
                        ancestor is null)
                        continue;

                    var ioIndex = formIo.IoDeviceIndex.GetValueOrDefault(1) - 1;
                    if (ioIndex >= 0 && ioIndex < ancestor.Ios.Count)
                        result.TryAdd(ancestor.Ordinal, ancestor.Ios[ioIndex]);
                    else if (ancestor.Io is not null)
                        result.TryAdd(ancestor.Ordinal, ancestor.Io);
                }
            }

            _state.OwnedMergeIoDefinitionCache = result;
            _state.OwnedMergeIoDefinitionCacheInitialized = true;
        }
    }
}
