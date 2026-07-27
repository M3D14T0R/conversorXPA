using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static int ResolveViewGridCellWidth(
        TaskFormControlDef control,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> tasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        int scaledWidth)
    {
        var dataExpression = ResolveControlDataExpression(control, task, tasks, dataObjects);
        var textLength = ResolveExternalManifestColumnTextLength(dataExpression);
        if (!textLength.HasValue &&
            TryResolveDataViewMemberColumn(task, dataExpression, out _, out var localColumn))
        {
            textLength = ResolveTextChoiceValueLength(localColumn);
        }

        if (!textLength.HasValue)
        {
            var targetInfo = ResolveTargetValueInfo(task, null, dataExpression);
            if (targetInfo.Resource is not null)
                textLength = ResolveTextChoiceValueLength(targetInfo.Resource);
        }

        // Large description fields are intentionally narrower than their full
        // storage picture in many XPA browses. Short identifiers, however, must
        // fit completely; otherwise values such as 001/TST001 become 01/TST0.
        if (textLength is > 0 and <= 12)
            scaledWidth = Math.Max(scaledWidth, (textLength.Value * 7) + 8);

        return Math.Max(24, scaledWidth);
    }

    private static int ResolveViewGridColumnWidth(
        TaskFormControlDef column,
        IReadOnlyDictionary<int, IReadOnlyList<int>> childIdsByColumn,
        IReadOnlyDictionary<int, TaskFormControlDef> controlsById,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> tasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        Func<int, int> scaleX,
        int? sourceColumnStartX)
    {
        var width = Math.Max(24, scaleX(column.Width));
        if (!childIdsByColumn.TryGetValue(column.Id, out var childIds))
            return width;

        foreach (var childId in childIds)
        {
            if (!controlsById.TryGetValue(childId, out var child))
                continue;

            var childWidth = ResolveViewGridCellWidth(
                child,
                task,
                tasks,
                dataObjects,
                Math.Max(10, scaleX(child.Width)));
            // XPA table-column nodes commonly have X=0. Their children,
            // however, retain absolute form coordinates. Use the semantic
            // column start that is also used when emitting the child's
            // relative Location; subtracting column.X would turn the absolute
            // coordinate into an ever-growing (cumulative) column width.
            var childOffset = sourceColumnStartX.HasValue
                ? Math.Max(0, child.X - sourceColumnStartX.Value)
                : Math.Max(0, child.X - column.X);
            width = Math.Max(width, scaleX(childOffset) + childWidth + 4);
        }

        return width;
    }
}
