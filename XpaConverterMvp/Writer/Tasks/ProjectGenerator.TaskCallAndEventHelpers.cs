using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveCallSemantics(TaskCallDef call, TaskSemantic? targetTask)
    {
        return call.OperationType switch
        {
            "T" => "CallTask OperationType=T -> tab/subtask semantics",
            "O" => "CallTask OperationType=O -> output/print semantics",
            "P" when targetTask is not null && NormalizeTaskName(targetTask.Description).Contains("print") => "CallTask OperationType=P -> print program semantics",
            "P" => "CallTask OperationType=P -> program/expand semantics",
            _ => $"CallTask OperationType={call.OperationType}"
        };
    }

    private static TaskSemantic? ResolveTaskByXpaId(int xpaId, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_tasksByDeclaredTaskId.TryGetValue(xpaId, out var byDeclaredTaskId))
            return byDeclaredTaskId;

        if (_topLevelTasksByProgramIndex.TryGetValue(xpaId, out var byProgramIndex))
            return byProgramIndex;

        var byOrdinal = GetTaskByOrdinal(xpaId, allTasks);
        if (byOrdinal is not null)
            return byOrdinal;

        var topLevelByOrdinal = _topLevelTasks.Count > 0
            ? _topLevelTasks
            : allTasks.Where(t => t.ParentOrdinal is null).OrderBy(t => t.Ordinal).ToList();
        if (xpaId > 0 && xpaId <= topLevelByOrdinal.Count)
            return topLevelByOrdinal[xpaId - 1];

        return null;
    }

    private static string BuildEventParameterSignature(TaskEventParameterDef parameter)
    {
        var type = parameter.Attr switch
        {
            "N" => "NumberParameter",
            "D" => "DateParameter",
            "T" => "TimeParameter",
            "L" => "BoolParameter",
            "B" => "BoolParameter",
            _ => "TextParameter"
        };
        return $"{type} {ToParameterIdentifier(parameter.Name)} = null";
    }
}

