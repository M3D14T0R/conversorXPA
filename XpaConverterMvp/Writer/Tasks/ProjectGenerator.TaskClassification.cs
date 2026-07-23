using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveBaseClass(TaskSemantic t)
    {
        if (t.Execution.TaskType == "B")
            return "BusinessProcessBase";
        if (GetStandaloneTabCalls(t).Count > 0 || HasExpandBeforeFlow(t) || t.FlowValidations.Count > 0)
            return "FlowUIControllerBase";
        return "UIControllerBase";
    }

    private static bool TaskNeedsParentReference(TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!task.ParentOrdinal.HasValue)
            return false;

        var parentTask = GetTaskByOrdinal(task.ParentOrdinal.Value, allTasks);
        if (parentTask?.MainProgram == true && task.Execution.TaskType == "B")
            return true;

        if (_parentSelectMapByTaskOrdinal is not null &&
            _parentSelectMapByTaskOrdinal.TryGetValue(task.Ordinal, out var parentSelectMap) &&
            parentSelectMap.Values.Any(v => v.Contains("_parent.", StringComparison.Ordinal)))
            return true;

        if (task.SqlWhere?.Arguments.Any(a => a.ParentLevel > 0) == true)
            return true;

        if (task.HandlersSemantic.Items.Any(h => h.EventParent.HasValue))
            return true;

        if (task.Logic.StartRaises.Any(r => r.EventParent.HasValue) ||
            task.Logic.EndRaises.Any(r => r.EventParent.HasValue))
            return true;

        if (task.HandlersSemantic.Items.Any(h => h.Updates.Any(u => !string.IsNullOrWhiteSpace(u.Parent))) ||
            task.Logic.RowLogics.Any(r => r.Actions.Any(a => a.Update is not null && !string.IsNullOrWhiteSpace(a.Update.Parent))) ||
            task.Logic.GroupLogics.Any(g => g.Actions.Any(a => a.Update is not null && !string.IsNullOrWhiteSpace(a.Update.Parent))))
            return true;

        if (task.FormIos.Any(io => io.IoDeviceParent.HasValue) ||
            task.HandlersSemantic.Items.Any(h => h.FormIos.Any(io => io.IoDeviceParent.HasValue)))
            return true;

        return false;
    }
}

