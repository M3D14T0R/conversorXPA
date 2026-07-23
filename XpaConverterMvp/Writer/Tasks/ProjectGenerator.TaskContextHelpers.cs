using System;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool ShouldReversePrimaryOrderBy(TaskSemantic task)
        => string.Equals(task.LocateDirection, "D", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(task.RangeDirection, "D", StringComparison.OrdinalIgnoreCase);

    private static bool IsInvokedByParentDatabaseErrorHandler(TaskSemantic task)
    {
        if (!task.ParentOrdinal.HasValue || _allTasks is null || _allTasks.Count == 0)
            return false;
        var parent = GetTaskByOrdinal(task.ParentOrdinal.Value, _allTasks);
        if (parent is null)
            return false;
        return parent.Handlers.Any(h => string.Equals(h.EventType, "R", StringComparison.OrdinalIgnoreCase));
    }
}

