using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveParentModelBindingByControlHint(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!task.ParentOrdinal.HasValue)
            return "";

        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.ControlName))
            hints.Add(NormalizeKey(c.ControlName));
        if (!string.IsNullOrWhiteSpace(c.Text))
            hints.Add(NormalizeKey(c.Text));
        if (!string.IsNullOrWhiteSpace(c.ColumnTitle))
            hints.Add(NormalizeKey(c.ColumnTitle));
        hints = hints.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hints.Count == 0)
            return "";

        var parentPrefix = "_parent";
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = allTasks.FirstOrDefault(t => t.Ordinal == parentOrdinal.Value);
            if (parentTask is null)
                break;
            foreach (var rc in parentTask.ResourcesSemantic.Ordered)
            {
                var resourceName = ResolveTaskResourceMemberName(parentTask, rc);
                var resourceNorm = NormalizeKey(resourceName);
                if (string.IsNullOrWhiteSpace(resourceNorm))
                    continue;
                if (hints.Any(h => h == resourceNorm || h.Contains(resourceNorm) || resourceNorm.Contains(h)))
                    return $"{parentPrefix}.{resourceName}";
            }
            foreach (var mm in BuildModelMembers(parentTask, dataObjects))
            {
                var d = dataObjects.FirstOrDefault(x => x.Ordinal == mm.DbObj);
                if (d is null)
                    continue;
                foreach (var col in d.Columns)
                {
                    var colNorm = NormalizeKey(col.Name);
                    if (string.IsNullOrWhiteSpace(colNorm))
                        continue;
                    if (hints.Any(h => h == colNorm || h.Contains(colNorm) || colNorm.Contains(h)))
                        return $"{parentPrefix}.{mm.MemberName}.{ToPascalIdentifier(col.Name)}";
                }
            }
            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "._parent";
        }

        return "";
    }

    private static string ResolvePrimaryMember(TaskSemantic task, int primaryObj, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (primaryObj <= 0)
            return "";
        var modelMembers = BuildModelMembers(task, dataObjects);
        var mm = modelMembers.FirstOrDefault(m => m.DbObj == primaryObj);
        if (!string.IsNullOrWhiteSpace(mm.MemberName))
            return mm.MemberName;
        var d = dataObjects.FirstOrDefault(x => x.Ordinal == primaryObj);
        return d is null ? "" : ResolveDataObjectTypeName(d);
    }

    private static string ResolveResourceDbEntityNameExpression(TaskSemantic task, TaskResourceDbDef resourceDb, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(resourceDb.EntityNameExpressionRef))
            return "";
        if (int.TryParse(resourceDb.EntityNameExpressionRef, out var intRef))
        {
            if (task.ExpressionsSemantic.EntriesByOrdinal.ContainsKey(intRef))
                return ResolveExpressionCode(intRef.ToString(), task, dataObjects, CreateMessageTextEmissionContext());
            if (task.ResourcesSemantic.ById.TryGetValue(intRef, out var resource))
                return ResolveTaskResourceMemberName(task, resource);
        }
        return "";
    }

    private static string ResolveBooleanBindingExpression(TaskSemantic task, int expressionId)
    {
        task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out var exp);
        if (exp is null)
            return "false";

        var call = $"_controller.Exp_{expressionId}()";
        return exp.Attribute switch
        {
            "B" => call,
            "N" => $"{call} != 0",
            "A" => $"!string.IsNullOrEmpty({call})",
            _ => $"System.Convert.ToBoolean({call})"
        };
    }

    private static string ResolveConditionExpressionCode(string? expressionId, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        string? sharedKey = null;
        string? ordinalKey = null;
        if (int.TryParse(expressionId, out var expressionOrdinal) &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionOrdinal, out var expr) &&
            expr is not null)
        {
            ordinalKey = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"bool-cond-id|{task.Ordinal}|{expressionOrdinal}");
            if (_sharedConditionExpressionCodeCache.TryGetValue(ordinalKey, out var ordinalCached))
                return ordinalCached;

            var sharedToken = !string.IsNullOrWhiteSpace(expr.LiteralNormalizedSyntax)
                ? expr.LiteralNormalizedSyntax
                : expr.Syntax ?? "";
            if (!string.IsNullOrWhiteSpace(sharedToken))
            {
                sharedKey = $"shared-bool-cond|{task.Ordinal}|{sharedToken}";
                if (_sharedConditionExpressionCodeCache.TryGetValue(sharedKey, out var sharedCached))
                    return sharedCached;
            }
        }

        var resolved = ResolveExpressionCode(expressionId, task, dataObjects, CreateBooleanConditionEmissionContext());
        var normalized = resolved;
        if (!string.IsNullOrWhiteSpace(ordinalKey))
            _sharedConditionExpressionCodeCache[ordinalKey] = normalized;
        if (!string.IsNullOrWhiteSpace(sharedKey))
            _sharedConditionExpressionCodeCache[sharedKey] = normalized;
        return normalized;
    }
}

