using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveSelectExpression(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, string primaryMember)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.Name ?? "",
            sel.Type ?? "",
            sel.SourceDbObj?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.SourceLinkSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.ColumnId.ToString(CultureInfo.InvariantCulture),
            primaryMember ?? "");
        if (_selectExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (sel.Type == "R" && sel.SourceDbObj.HasValue)
        {
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == sel.SourceDbObj.Value);
            var col = d?.Columns.FirstOrDefault(c => c.Id == sel.ColumnId);
            if (d is not null && col is null && sel.ColumnId > 0 && sel.ColumnId <= d.Columns.Count)
                col = d.Columns[sel.ColumnId - 1];
            if (d is not null && col is not null)
            {
                var sourceMember = ResolveSelectSourceMember(sel, task, dataObjects);
                if (string.IsNullOrWhiteSpace(sourceMember))
                    sourceMember = primaryMember;
                var resolved = $"{sourceMember}.{ToPascalIdentifier(col.Name)}";
                _selectExpressionCache[cacheKey] = resolved;
                return resolved;
            }
        }

        var rc = ResolveTaskResourceColumn(task, sel.ColumnId);
        if (rc is not null)
        {
            var resolved = ResolveTaskResourceMemberName(task, rc);
            _selectExpressionCache[cacheKey] = resolved;
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(sel.Name) &&
            !task.MainProgram &&
            task.ParentOrdinal.HasValue)
        {
            var applicationSelectMap = BuildApplicationSelectMap(_allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
            if (applicationSelectMap.TryGetValue(sel.Name, out var applicationExpr) &&
                !string.IsNullOrWhiteSpace(applicationExpr))
            {
                _selectExpressionCache[cacheKey] = applicationExpr;
                return applicationExpr;
            }
        }

        if (sel.Type == "V")
        {
            var virtuals = task.SelectsSemantic.Items.Where(s => s.Type == "V").ToList();
            var pos = virtuals.FindIndex(s => string.Equals(s.Name, sel.Name, StringComparison.OrdinalIgnoreCase));
            if (pos >= 0)
            {
                var byOrder = task.ResourcesSemantic.Ordered.ToList();
                if (pos < byOrder.Count)
                {
                    var resolved = ResolveTaskResourceMemberName(task, byOrder[pos]);
                    _selectExpressionCache[cacheKey] = resolved;
                    return resolved;
                }
            }
        }

        _selectExpressionCache[cacheKey] = "";
        return "";
    }

    private static string ResolveSelectSourceMember(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.SourceDbObj?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.SourceLinkSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            sel.ColumnId.ToString(CultureInfo.InvariantCulture));
        if (_selectSourceMemberCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var modelMembers = BuildModelMembers(task, dataObjects);
        if (sel.SourceLinkSequence.HasValue)
        {
            var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
            var linkMembers = BuildLinkMembers(task, dataObjects, modelMembers, primaryObj);
            var idx = sel.SourceLinkSequence.Value - 1;
            if (idx >= 0 && idx < linkMembers.Count)
            {
                _selectSourceMemberCache[cacheKey] = linkMembers[idx].MemberName;
                return linkMembers[idx].MemberName;
            }
        }
        var resolved = modelMembers.FirstOrDefault(m => m.DbObj == sel.SourceDbObj).MemberName;
        _selectSourceMemberCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveSelectBindValueExpression(TaskLogicSelectDef sel, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveSelectBindValueExpression(sel, task, dataObjects, null);

    private static string ResolveSelectBindValueExpression(
        TaskLogicSelectDef sel,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? targetExpr)
    {
        if (!sel.AssignmentExpressionId.HasValue)
            return "";
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            sel.Name ?? "",
            sel.AssignmentExpressionId.Value.ToString(CultureInfo.InvariantCulture),
            targetExpr ?? "");
        if (_selectBindValueExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;
        string resolved;
        if (!string.IsNullOrWhiteSpace(targetExpr) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(sel.AssignmentExpressionId.Value, out var bindExpression) &&
            bindExpression is not null)
        {
            var targetInfo = ResolveBindValueTargetInfoFromEvidence(task, sel, targetExpr);

            resolved = targetInfo.IsDotNet || string.IsNullOrWhiteSpace(targetInfo.AttrObj)
                ? ResolveFilterOperandExpression(task, targetExpr, sel.AssignmentExpressionId.Value, dataObjects)
                : ResolveTypedExpressionEntryCode(
                    bindExpression,
                    task,
                    dataObjects,
                    CreateBindValueEmissionContext(targetInfo, targetInfo.TargetMember)).Code;
        }
        else
        {
            resolved = string.IsNullOrWhiteSpace(targetExpr)
                ? ResolveExpressionCode(sel.AssignmentExpressionId.Value.ToString(), task, dataObjects)
                : ResolveFilterOperandExpression(task, targetExpr, sel.AssignmentExpressionId.Value, dataObjects);
        }

        _selectBindValueExpressionCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveSortSegmentExpression(
        TaskSemantic task,
        TaskSortSegmentDef seg,
        IReadOnlyList<DataObjectDef> dataObjects,
        string? primaryMember)
    {
        var selects = task.SelectsSemantic.Items.Where(s => !s.IsFunctionSelect).ToList();
        if (seg.FieldId > 0 && seg.FieldId <= selects.Count)
        {
            var sel = selects[seg.FieldId - 1];
            var expr = ResolveSelectExpression(sel, task, dataObjects, primaryMember ?? "");
            if (IsSimpleMemberAccess(expr))
                return expr;
        }
        return "";
    }
}

