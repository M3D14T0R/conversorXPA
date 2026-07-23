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

        if (!_incomingTaskCallsByTargetOrdinal.TryGetValue(targetTask.Ordinal, out var incomingCalls))
        {
            _incomingParameterCountCache[targetTask.Ordinal] = 0;
            return 0;
        }

        var counts = incomingCalls
            .Select(x => CountEffectiveCallArguments(x.Call))
            .ToHashSet();

        var result = counts.Count == 1 ? counts.First() : 0;
        _incomingParameterCountCache[targetTask.Ordinal] = result;
        return result;
    }
}

