using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryGetAccessibleFunctionContract(
        TaskSemantic task,
        string functionName,
        out FunctionOverrideSemantic function,
        out string targetName)
    {
        function = null!;
        targetName = "";
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        var trimmed = functionName.Trim();
        foreach (var candidate in task.FunctionOverridesSemantic)
        {
            if (FunctionContractMatches(candidate, trimmed, candidate.MethodName))
            {
                function = candidate;
                targetName = candidate.MethodName;
                return true;
            }
        }

        if (_allTasks is null)
            return false;

        var appTask = _allTasks.FirstOrDefault(x => x.MainProgram) ?? _allTasks.FirstOrDefault(x => x.ParentOrdinal is null);
        if (appTask is not null && appTask.Ordinal != task.Ordinal)
        {
            foreach (var candidate in appTask.FunctionOverridesSemantic)
            {
                var target = $"Application.Instance.{candidate.MethodName}";
                if (FunctionContractMatches(candidate, trimmed, target))
                {
                    function = candidate;
                    targetName = target;
                    return true;
                }
            }
        }

        if (!task.ParentOrdinal.HasValue)
            return false;

        var depth = 1;
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = _allTasks.FirstOrDefault(x => x.Ordinal == parentOrdinal.Value);
            if (parentTask is null)
                break;

            var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
            foreach (var candidate in parentTask.FunctionOverridesSemantic)
            {
                if (!string.Equals(candidate.Definition.Scope, "S", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = prefix + candidate.MethodName;
                if (FunctionContractMatches(candidate, trimmed, target))
                {
                    function = candidate;
                    targetName = target;
                    return true;
                }
            }

            parentOrdinal = parentTask.ParentOrdinal;
            depth++;
        }

        return false;
    }

    private static bool FunctionContractMatches(FunctionOverrideSemantic function, string requestedName, string emittedTarget)
    {
        if (string.IsNullOrWhiteSpace(function.Name) || string.IsNullOrWhiteSpace(function.MethodName))
            return false;

        return string.Equals(requestedName, function.Name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(requestedName, function.MethodName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(requestedName, emittedTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ResolveAccessibleApplicationFunctionTargets(TaskSemantic task)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_allTasks is null || task.MainProgram)
            return result;

        var appTask = _allTasks.FirstOrDefault(x => x.MainProgram) ?? _allTasks.FirstOrDefault(x => x.ParentOrdinal is null);
        if (appTask is null || appTask.Ordinal == task.Ordinal)
            return result;

        foreach (var fn in appTask.FunctionOverridesSemantic)
        {
            if (string.IsNullOrWhiteSpace(fn.Name) || string.IsNullOrWhiteSpace(fn.MethodName))
                continue;
            if (!result.ContainsKey(fn.Name))
                result[fn.Name] = $"Application.Instance.{fn.MethodName}";
        }

        return result;
    }

    private static Dictionary<string, string> ResolveAccessibleParentFunctionTargets(TaskSemantic task)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_allTasks is null || !task.ParentOrdinal.HasValue)
            return result;

        var depth = 1;
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = _allTasks.FirstOrDefault(x => x.Ordinal == parentOrdinal.Value);
            if (parentTask is null)
                break;

            var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
            foreach (var fn in parentTask.FunctionOverridesSemantic)
            {
                if (string.IsNullOrWhiteSpace(fn.Name) || string.IsNullOrWhiteSpace(fn.MethodName))
                    continue;
                if (!string.Equals(fn.Definition.Scope, "S", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!result.ContainsKey(fn.Name))
                    result[fn.Name] = prefix + fn.MethodName;
            }

            parentOrdinal = parentTask.ParentOrdinal;
            depth++;
        }

        return result;
    }
}

