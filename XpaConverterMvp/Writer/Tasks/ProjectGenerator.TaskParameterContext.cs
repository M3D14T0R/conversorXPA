using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveRaiseDestinationContextExpression(string raw, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (int.TryParse(raw, out var ordinal))
        {
            var resource = ResolveTaskResourceColumn(task, ordinal);
            if (resource is not null)
                return ResolveTaskResourceMemberName(task, resource);
        }

        var selectExpr = ResolveSelectExpressionByName(raw, task, dataObjects);
        if (!string.IsNullOrWhiteSpace(selectExpr))
            return selectExpr;
        return raw;
    }

    private static string ToParameterIdentifier(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "parameter";
        var id = ToCodeIdentifierPreservingCase(rawName);
        if (string.IsNullOrWhiteSpace(id))
            id = "parameter";
        if (char.IsUpper(id[0]))
            id = char.ToLowerInvariant(id[0]) + id[1..];
        return id;
    }

    private static int InferIncomingParameterCount(TaskSemantic targetTask)
    {
        if (_incomingParameterCountCache.TryGetValue(targetTask.Ordinal, out var cached))
            return cached;

        if (_allTasks is null || _allTasks.Count == 0)
        {
            _incomingParameterCountCache[targetTask.Ordinal] = 0;
            return 0;
        }

        var counts = new HashSet<int>();
        foreach (var task in _allTasks)
        {
            foreach (var call in EnumerateTaskCalls(task))
            {
                if (call.OperationType != "T" || !call.TaskId.HasValue)
                    continue;
                var matchesTarget =
                    call.TaskId == targetTask.Ordinal ||
                    (targetTask.ParentOrdinal == task.Ordinal && targetTask.SubtaskIndex == call.TaskId.Value) ||
                    CallTargetsTaskByProgramIndex(call, targetTask);
                if (!matchesTarget)
                    continue;
                counts.Add(CountEffectiveCallArguments(call));
            }
        }

        var result = counts.Count == 1 ? counts.First() : 0;
        _incomingParameterCountCache[targetTask.Ordinal] = result;
        return result;
    }
}

