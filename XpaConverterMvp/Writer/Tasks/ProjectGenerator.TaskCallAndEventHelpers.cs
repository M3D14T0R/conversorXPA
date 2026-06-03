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
        var byDeclaredTaskId = allTasks.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.TaskId) &&
            int.TryParse(t.TaskId, out var taskId) &&
            taskId == xpaId);
        if (byDeclaredTaskId is not null)
            return byDeclaredTaskId;

        var byProgramIndex = allTasks.FirstOrDefault(t => t.ParentOrdinal is null && t.TopLevelProgramIndex == xpaId);
        if (byProgramIndex is not null)
            return byProgramIndex;

        var byOrdinal = allTasks.FirstOrDefault(t => t.Ordinal == xpaId);
        if (byOrdinal is not null)
            return byOrdinal;

        var topLevelByOrdinal = allTasks.Where(t => t.ParentOrdinal is null).OrderBy(t => t.Ordinal).ToList();
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

