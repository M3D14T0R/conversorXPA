using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveParentBindingExpression(
        DataColumnDef childColumn,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{childColumn.Id}|{childColumn.Name}|{childColumn.DbColumnName}");
        if (_parentBindingExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!task.ParentOrdinal.HasValue ||
            !_tasksByOrdinal.TryGetValue(task.ParentOrdinal.Value, out var parentTask))
        {
            _parentBindingExpressionCache[cacheKey] = "";
            return "";
        }

        foreach (var modelMember in BuildModelMembers(parentTask, dataObjects))
        {
            var dataObject = ResolveDataObjectByOrdinal(dataObjects, modelMember.DbObj);
            if (dataObject is null)
                continue;

            var parentColumn = dataObject.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, childColumn.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.DbColumnName, childColumn.DbColumnName, StringComparison.OrdinalIgnoreCase));
            if (parentColumn is null)
                continue;

            var result = $"_parent.{modelMember.MemberName}.{ToPascalIdentifier(parentColumn.Name)}";
            _parentBindingExpressionCache[cacheKey] = result;
            return result;
        }

        _parentBindingExpressionCache[cacheKey] = "";
        return "";
    }
}
