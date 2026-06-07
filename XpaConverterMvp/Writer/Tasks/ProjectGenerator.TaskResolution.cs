using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveTaskTypeReference(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_taskTypeReferenceByOrdinal.TryGetValue(t.Ordinal, out var cached))
            return cached;

        var chain = new List<string> { t.MainProgram ? "Application" : ResolveTaskClassName(t, allTasks) };
        var current = t;
        while (current.ParentOrdinal.HasValue)
        {
            var parent = GetTaskByOrdinal(current.ParentOrdinal, allTasks);
            if (parent is null)
                break;
            chain.Insert(0, parent.MainProgram ? "Application" : ResolveTaskClassName(parent, allTasks));
            current = parent;
        }
        var resolved = string.Join(".", chain);
        var targetNs = _isComponentized
            ? ResolveNamespaceForComponent(t.SourceComponent)
            : _targetNamespace;
        resolved = $"global::{targetNs}.{resolved}";
        _taskTypeReferenceByOrdinal[t.Ordinal] = resolved;
        return resolved;
    }

    private static string ResolveTaskNameByObj(string obj, IReadOnlyList<TaskSemantic> tasks)
    {
        if (!int.TryParse(obj, out var n))
            return "";
        var task = GetTaskByOrdinal(n, tasks);
        return task is null ? "" : ResolveTaskClassName(task, tasks);
    }

    private static bool SupportsDbTypeInitializer(DataColumnDef column)
    {
        return column.AttrObj switch
        {
            "FIELD_NUMERIC" => true,
            "FIELD_ALPHA" => true,
            "FIELD_UNICODE" => true,
            "FIELD_BOOLEAN" => true,
            "FIELD_LOGICAL" => true,
            "FIELD_BLOB" => true,
            _ => false
        };
    }

    private static string ResolveTaskClassName(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_taskClassNameByOrdinal.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var baseName = ResolveTaskClassBaseName(task);
        var parent = GetTaskByOrdinal(task.ParentOrdinal, allTasks);
        var siblingCollisionIndex = ResolveTaskSiblingCollisionIndex(task, allTasks, baseName);

        var candidate = siblingCollisionIndex == 0
            ? baseName
            : baseName + new string('_', siblingCollisionIndex);

        while (true)
        {
            var needsRename = false;
            var ancestor = parent;
            while (ancestor is not null)
            {
                var ancestorName = ResolveTaskClassName(ancestor, allTasks);
                if (string.Equals(ancestorName, candidate, StringComparison.Ordinal))
                {
                    needsRename = true;
                    break;
                }

                var collidesWithAncestorFunction = ancestor.FunctionOverridesSemantic.Any(fn =>
                    string.Equals(fn.MethodName, candidate, StringComparison.Ordinal));
                if (collidesWithAncestorFunction)
                {
                    needsRename = true;
                    break;
                }

                ancestor = GetTaskByOrdinal(ancestor.ParentOrdinal, allTasks);
            }

            if (!needsRename)
                break;

            candidate += "_";
        }

        _taskClassNameByOrdinal[task.Ordinal] = candidate;
        return candidate;
    }

    private static void BuildTaskClassNameIndexes(IReadOnlyList<TaskSemantic> allTasks)
    {
        _taskClassBaseNameByOrdinal = allTasks.ToDictionary(t => t.Ordinal, ResolveTaskClassBaseNameUncached);
        _taskClassSiblingCollisionIndexByOrdinal = new Dictionary<int, int>();

        foreach (var group in allTasks.GroupBy(t => t.ParentOrdinal))
        {
            var ordered = group.Key.HasValue
                ? group.OrderBy(t => t.SubtaskIndex ?? int.MaxValue).ThenBy(t => t.Ordinal)
                : group.OrderBy(t => t.Ordinal);
            var seenByBaseName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var task in ordered)
            {
                var baseName = ResolveTaskClassBaseName(task);
                seenByBaseName.TryGetValue(baseName, out var seen);
                _taskClassSiblingCollisionIndexByOrdinal[task.Ordinal] = seen;
                seenByBaseName[baseName] = seen + 1;
            }
        }
    }

    private static string ResolveTaskClassBaseName(TaskSemantic task)
    {
        if (_taskClassBaseNameByOrdinal.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var baseName = ResolveTaskClassBaseNameUncached(task);
        _taskClassBaseNameByOrdinal[task.Ordinal] = baseName;
        return baseName;
    }

    private static string ResolveTaskClassBaseNameUncached(TaskSemantic task)
    {
        var descriptionName = ToTaskClassName(task.Description);
        if (task.ParentOrdinal.HasValue || string.IsNullOrWhiteSpace(task.PublicName))
            return descriptionName;

        var publicName = ToCodeIdentifierPreservingCase(task.PublicName!.Trim());
        if (string.IsNullOrWhiteSpace(publicName) || string.Equals(publicName, descriptionName, StringComparison.Ordinal))
            return descriptionName;
        if (descriptionName.StartsWith(publicName + "_", StringComparison.Ordinal))
            return descriptionName;

        var descriptionTail = descriptionName.TrimStart('_');
        if (string.IsNullOrWhiteSpace(descriptionTail))
            return publicName;

        return publicName + "_" + descriptionTail;
    }

    private static int ResolveTaskSiblingCollisionIndex(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks, string baseName)
    {
        if (_taskClassSiblingCollisionIndexByOrdinal.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var seen = 0;
        foreach (var sibling in GetSiblingTasks(task, allTasks))
        {
            if (sibling.Ordinal == task.Ordinal)
                break;
            if (string.Equals(ResolveTaskClassBaseName(sibling), baseName, StringComparison.Ordinal))
                seen++;
        }

        _taskClassSiblingCollisionIndexByOrdinal[task.Ordinal] = seen;
        return seen;
    }

    private static TaskSemantic? GetTaskByOrdinal(int? ordinal, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!ordinal.HasValue)
            return null;
        if (_tasksByOrdinal.Count > 0 && _tasksByOrdinal.TryGetValue(ordinal.Value, out var cached))
            return cached;
        return allTasks.FirstOrDefault(t => t.Ordinal == ordinal.Value);
    }

    private static IReadOnlyList<TaskSemantic> GetChildTasks(int parentOrdinal, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_childTasksByParentOrdinal.Count > 0 && _childTasksByParentOrdinal.TryGetValue(parentOrdinal, out var cached))
            return cached;
        return allTasks
            .Where(t => t.ParentOrdinal == parentOrdinal)
            .OrderBy(t => t.SubtaskIndex ?? int.MaxValue)
            .ThenBy(t => t.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<TaskSemantic> GetSiblingTasks(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!task.ParentOrdinal.HasValue)
            return _topLevelTasks.Count > 0 ? _topLevelTasks : allTasks.Where(t => !t.ParentOrdinal.HasValue).OrderBy(t => t.Ordinal).ToList();
        return GetChildTasks(task.ParentOrdinal.Value, allTasks);
    }

    private static TaskSemantic? ResolveTaskByCall(TaskSemantic currentTask, TaskCallDef call, IReadOnlyList<TaskSemantic> allTasks)
    {
        var cacheKey = BuildTaskCallCacheKey(currentTask, call);
        if (_resolvedCallTargetOrdinalCache.TryGetValue(cacheKey, out var cachedOrdinal))
            return cachedOrdinal < 0 ? null : GetTaskByOrdinal(cachedOrdinal, allTasks);

        if (!string.IsNullOrWhiteSpace(call.TargetComponentName) &&
            !_loadedSourceComponents.Contains(call.TargetComponentName))
        {
            _resolvedCallTargetOrdinalCache[cacheKey] = -1;
            return null;
        }

        if (call.OperationType == "T" && call.TaskId.HasValue)
        {
            var nested = GetChildTasks(currentTask.Ordinal, allTasks)
                .FirstOrDefault(t => t.SubtaskIndex == call.TaskId.Value);
            if (nested is not null)
            {
                _resolvedCallTargetOrdinalCache[cacheKey] = nested.Ordinal;
                return nested;
            }
        }
        if (call.TaskId.HasValue)
        {
            if (string.Equals(call.OperationType, "P", StringComparison.OrdinalIgnoreCase))
            {
                var topLevelProgram = allTasks.FirstOrDefault(t =>
                    t.ParentOrdinal is null &&
                    t.TopLevelProgramIndex == call.TaskId.Value);
                if (topLevelProgram is not null)
                {
                    _resolvedCallTargetOrdinalCache[cacheKey] = topLevelProgram.Ordinal;
                    return topLevelProgram;
                }
            }

            var resolved = ResolveTaskByXpaId(call.TaskId.Value, allTasks);
            _resolvedCallTargetOrdinalCache[cacheKey] = resolved?.Ordinal ?? -1;
            return resolved;
        }

        _resolvedCallTargetOrdinalCache[cacheKey] = -1;
        return null;
    }

    private static string? ResolveExternalTaskTypeReference(TaskCallDef call)
    {
        var cacheKey = BuildExternalTaskTypeReferenceCacheKey(call);
        if (_externalTaskTypeReferenceCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (string.IsNullOrWhiteSpace(call.TargetComponentName))
        {
            _externalTaskTypeReferenceCache[cacheKey] = null;
            return null;
        }
        if (!_projectReferenceManifests.TryGetValue(call.TargetComponentName, out var manifest))
        {
            _externalTaskTypeReferenceCache[cacheKey] = null;
            return null;
        }

        string? className = null;
        if (!string.IsNullOrWhiteSpace(call.TargetPublicName))
            manifest.Programs.TryGetValue(call.TargetPublicName, out className);
        if (string.IsNullOrWhiteSpace(className) && call.TargetObjectId.HasValue)
            manifest.ProgramsByIndex.TryGetValue(call.TargetObjectId.Value, out className);
        if (string.IsNullOrWhiteSpace(className))
        {
            _externalTaskTypeReferenceCache[cacheKey] = null;
            return null;
        }

        var ns = string.IsNullOrWhiteSpace(manifest.Namespace) ? call.TargetComponentName : manifest.Namespace;
        var resolved = $"{ns}.{className}";
        _externalTaskTypeReferenceCache[cacheKey] = resolved;
        return resolved;
    }

    private static string BuildTaskCallCacheKey(TaskSemantic currentTask, TaskCallDef call)
        => string.Join("|",
            currentTask.Ordinal.ToString(CultureInfo.InvariantCulture),
            call.OperationType ?? "",
            call.TargetComponentName ?? "",
            call.TargetPublicName ?? "",
            call.TargetObjectId?.ToString(CultureInfo.InvariantCulture) ?? "",
            call.TaskId?.ToString(CultureInfo.InvariantCulture) ?? "",
            string.Join(",", call.ArgumentVariables.Select(a => a ?? "")));

    private static string BuildExternalTaskTypeReferenceCacheKey(TaskCallDef call)
        => string.Join("|",
            call.TargetComponentName ?? "",
            call.TargetPublicName ?? "",
            call.TargetObjectId?.ToString(CultureInfo.InvariantCulture) ?? "",
            call.OperationType ?? "");

    private static string NormalizeTaskName(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var x = s.ToLowerInvariant().Trim();
        x = Regex.Replace(x, @"\(\s*\)$", "");
        x = Regex.Replace(x, @"^[^a-z0-9]+", "");
        x = Regex.Replace(x, @"[^a-z0-9]+", "");
        return x;
    }

    private static int ResolveOptionalRunParameterStartIndex(TaskSemantic targetTask, IReadOnlyList<TaskSemantic> allTasks, int parameterCount)
    {
        if (parameterCount <= 0)
            return int.MaxValue;

        if (_optionalRunParameterStartIndexCache.TryGetValue(targetTask.Ordinal, out var cachedMinArgCount))
        {
            if (cachedMinArgCount < 0)
                return int.MaxValue;
            if (cachedMinArgCount >= parameterCount)
                return int.MaxValue;
            return ExpandOptionalRunParameterStartIndexForTrailingOutputs(targetTask, Math.Max(0, cachedMinArgCount));
        }

        var min = ResolveOptionalRunParameterStartIndexFallback(targetTask, allTasks);
        _optionalRunParameterStartIndexCache[targetTask.Ordinal] = min;
        if (min < 0 || min >= parameterCount)
            return int.MaxValue;
        return ExpandOptionalRunParameterStartIndexForTrailingOutputs(targetTask, Math.Max(0, min));
    }

    private static int ExpandOptionalRunParameterStartIndexForTrailingOutputs(TaskSemantic targetTask, int optionalStartIndex)
    {
        if (optionalStartIndex <= 0)
            return optionalStartIndex;

        var parameters = GetTaskParameters(targetTask);
        while (optionalStartIndex > 0 &&
               optionalStartIndex <= parameters.Count &&
               IsOptionalRunOutputParameter(parameters[optionalStartIndex - 1]))
        {
            optionalStartIndex--;
        }

        return optionalStartIndex;
    }

    private static bool IsOptionalRunOutputParameter(
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter)
    {
        if (!IsInputParameterDirection(parameter.ParameterDirection))
            return true;

        return parameter.ParameterName.StartsWith("pr_", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, int> BuildOptionalRunParameterStartIndexCache(IReadOnlyList<TaskSemantic> allTasks)
    {
        var result = allTasks.ToDictionary(t => t.Ordinal, _ => -1);
        var topLevelTasksByProgramIndex = allTasks
            .Where(t => !t.ParentOrdinal.HasValue && t.TopLevelProgramIndex.HasValue)
            .GroupBy(t => t.TopLevelProgramIndex!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskSemantic>)g.ToList());
        foreach (var task in allTasks)
        {
            foreach (var call in EnumerateTaskCalls(task))
            {
                var effectiveArgumentCount = CountEffectiveCallArguments(call);
                var resolved = ResolveTaskByCall(task, call, allTasks);
                if (resolved is not null)
                    RegisterOptionalRunArgumentCount(result, resolved.Ordinal, effectiveArgumentCount);

                if (TryResolveProgramIndexCallTargetIndex(call, out var targetIndex) &&
                    topLevelTasksByProgramIndex.TryGetValue(targetIndex, out var targets))
                {
                    foreach (var target in targets)
                        RegisterOptionalRunArgumentCount(result, target.Ordinal, effectiveArgumentCount);
                }
            }
        }

        return result;
    }

    private static void RegisterOptionalRunArgumentCount(Dictionary<int, int> result, int targetOrdinal, int argumentCount)
    {
        if (!result.TryGetValue(targetOrdinal, out var current) || current < 0 || argumentCount < current)
            result[targetOrdinal] = Math.Max(0, argumentCount);
    }

    private static int ResolveOptionalRunParameterStartIndexFallback(TaskSemantic targetTask, IReadOnlyList<TaskSemantic> allTasks)
    {
        var argCounts = new List<int>();
        foreach (var task in allTasks)
        {
            foreach (var c in EnumerateTaskCalls(task))
            {
                var resolved = ResolveTaskByCall(task, c, allTasks);
                if ((resolved is not null && resolved.Ordinal == targetTask.Ordinal) ||
                    CallTargetsTaskByProgramIndex(c, targetTask))
                    argCounts.Add(CountEffectiveCallArguments(c));
            }
        }

        if (argCounts.Count == 0)
            return -1;

        return argCounts.Min();
    }

    private static bool CallTargetsTaskByProgramIndex(TaskCallDef call, TaskSemantic targetTask)
    {
        if (!string.IsNullOrWhiteSpace(call.TargetComponentName))
            return false;
        if (!string.Equals(call.OperationType, "P", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!targetTask.TopLevelProgramIndex.HasValue || targetTask.ParentOrdinal.HasValue)
            return false;
        if (call.TargetObjectId.HasValue && call.TargetObjectId.Value == targetTask.TopLevelProgramIndex.Value)
            return true;
        if (call.TaskId.HasValue &&
            call.TaskId.Value == targetTask.TopLevelProgramIndex.Value)
            return true;
        return false;
    }

    private static IEnumerable<TaskSemantic> ResolveProgramIndexCallTargets(TaskCallDef call, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!TryResolveProgramIndexCallTargetIndex(call, out var targetIndex))
            yield break;

        foreach (var target in allTasks)
        {
            if (target.ParentOrdinal.HasValue ||
                !target.TopLevelProgramIndex.HasValue ||
                target.TopLevelProgramIndex.Value != targetIndex)
                continue;

            yield return target;
        }
    }

    private static bool TryResolveProgramIndexCallTargetIndex(TaskCallDef call, out int targetIndex)
    {
        targetIndex = 0;
        if (!string.IsNullOrWhiteSpace(call.TargetComponentName))
            return false;
        if (!string.Equals(call.OperationType, "P", StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = call.TargetObjectId ?? call.TaskId;
        if (!candidate.HasValue)
            return false;

        targetIndex = candidate.Value;
        return true;
    }

    private static int CountEffectiveCallArguments(TaskCallDef call)
    {
        var count = Math.Max(call.ArgumentVariables?.Count ?? 0, call.ArgumentDefs?.Count ?? 0);
        while (count > 0 && IsEffectivelyEmptyCallArgument(call, count - 1))
            count--;
        return count;
    }

    private static bool IsEffectivelyEmptyCallArgument(TaskCallDef call, int index)
    {
        var argumentVariables = call.ArgumentVariables ?? Array.Empty<string>();
        var argumentDefs = call.ArgumentDefs ?? Array.Empty<TaskArgumentDef>();

        var rawVariable = index >= 0 && index < argumentVariables.Count
            ? argumentVariables[index]
            : null;
        if (!string.IsNullOrWhiteSpace(rawVariable) &&
            !string.Equals(rawVariable.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return false;

        var arg = index >= 0 && index < argumentDefs.Count
            ? argumentDefs[index]
            : null;
        if (arg is null)
            return true;
        if (arg.Skip == true)
            return true;

        return string.IsNullOrWhiteSpace(arg.Variable) &&
               !arg.ExpressionId.HasValue &&
               !arg.Exp.HasValue;
    }

    private static IEnumerable<TaskCallDef> EnumerateTaskCalls(TaskSemantic task)
    {
        foreach (var c in task.DataView.TabCalls)
            yield return c;

        foreach (var h in task.HandlersSemantic.Items)
        {
            foreach (var c in h.Calls)
                yield return c;
            foreach (var a in h.Actions)
                if (a.Call is not null)
                    yield return a.Call;
        }

        foreach (var g in task.Logic.GroupLogics)
            foreach (var a in g.Actions)
                if (a.Call is not null)
                    yield return a.Call;

        foreach (var fn in task.FunctionOverridesSemantic)
            foreach (var a in fn.OrderedActions)
                if (a.Call is not null)
                    yield return a.Call;

        foreach (var rl in task.Logic.StartLogics)
            foreach (var a in rl.Actions)
                if (a.Call is not null)
                    yield return a.Call;
        foreach (var rl in task.Logic.EndLogics)
            foreach (var a in rl.Actions)
                if (a.Call is not null)
                    yield return a.Call;
        foreach (var rl in task.Logic.RowLogics)
            foreach (var a in rl.Actions)
                if (a.Call is not null)
                    yield return a.Call;
        foreach (var rl in task.Logic.SavingRowLogics)
            foreach (var a in rl.Actions)
                if (a.Call is not null)
                    yield return a.Call;
    }
}

