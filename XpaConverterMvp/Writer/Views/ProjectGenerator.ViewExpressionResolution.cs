using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveControlDataExpression(TaskFormControlDef c, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        // Control ids are local to a FormEntry. Tasks with multiple displays commonly
        // reuse the same ids, so the form must be part of the semantic cache identity.
        var cacheKey = string.Join("|",
            task.Ordinal,
            c.FormEntryIndex,
            c.Id,
            c.DataColumn?.Trim().ToUpperInvariant() ?? "",
            c.DataExpressionId?.ToString() ?? "");
        if (_controlDataExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string Cache(string value)
        {
            _controlDataExpressionCache[cacheKey] = value;
            return value;
        }

        var allowParentHintBinding = AllowsViewControlParentHintBinding(c);
        var parentModelBinding = allowParentHintBinding
            ? ResolveParentModelBindingByControlHint(c, task, allTasks, dataObjects)
            : "";

        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            if (TryResolvePushButtonControlResourceBinding(c, task, out var buttonResourceBinding))
                return Cache(buttonResourceBinding);

            var selectBinding = ResolveDataColumnSelectBinding(c.DataColumn, task, allTasks, dataObjects);
            if (!string.IsNullOrWhiteSpace(selectBinding))
            {
                var normalizedExpr = PrefixControllerReferencesForView(selectBinding, task, dataObjects);
                if (normalizedExpr.StartsWith("_controller.", StringComparison.Ordinal))
                    normalizedExpr = normalizedExpr["_controller.".Length..];
                return Cache(normalizedExpr);
            }

            var ordinalBinding = ResolveDataColumnOrdinalBinding(c.DataColumn, task, allTasks);
            if (!string.IsNullOrWhiteSpace(ordinalBinding))
            {
                var normalizedOrdinalBinding = PrefixControllerReferencesForView(ordinalBinding, task, dataObjects);
                if (normalizedOrdinalBinding.StartsWith("_controller.", StringComparison.Ordinal))
                    normalizedOrdinalBinding = normalizedOrdinalBinding["_controller.".Length..];
                return Cache(normalizedOrdinalBinding);
            }
        }
        if (allowParentHintBinding && !string.IsNullOrWhiteSpace(parentModelBinding))
        {
            var normalizedBinding = PrefixControllerReferencesForView(parentModelBinding, task, dataObjects);
            if (normalizedBinding.StartsWith("_controller.", StringComparison.Ordinal))
                normalizedBinding = normalizedBinding["_controller.".Length..];
            return Cache(normalizedBinding);
        }
        if (c.DataExpressionId.HasValue)
            return Cache("");
        return Cache("");
    }

    private static bool AllowsViewControlParentHintBinding(TaskFormControlDef c)
    {
        return !string.Equals(c.Model, "CTRL_GUI0_IMAGE", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(c.Model, "CTRL_GUI1_IMAGE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolvePushButtonControlResourceBinding(TaskFormControlDef c, TaskSemantic task, out string binding)
    {
        binding = "";
        if (!TryResolvePushButtonControlResource(c, task, out var resource) || resource is null)
            return false;

        binding = ResolveTaskResourceMemberName(task, resource);
        return !string.IsNullOrWhiteSpace(binding);
    }

    private static bool TryResolvePushButtonControlResource(TaskFormControlDef c, TaskSemantic task, out TaskResourceColumnDef? resource)
    {
        resource = null;
        if (!string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(c.ControlName) && !c.ModelVarColumn.HasValue))
            return false;

        if (!string.IsNullOrWhiteSpace(c.ControlName) &&
            task.ResourcesSemantic.ByName.TryGetValue(c.ControlName, out var byName) && byName is not null)
        {
            resource = byName;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(c.ControlName) &&
            task.ResourcesSemantic.ByLegacyName.TryGetValue(c.ControlName, out var byLegacy) && byLegacy is not null)
        {
            resource = byLegacy;
            return true;
        }

        if (c.ModelVarColumn.HasValue)
        {
            var resourceIndex = c.ModelVarColumn.Value - 1;
            if (resourceIndex >= 0 && resourceIndex < task.ResourcesSemantic.Ordered.Count)
            {
                resource = task.ResourcesSemantic.Ordered[resourceIndex];
                return true;
            }
        }

        return false;
    }

    private static string ResolvePushButtonDesignText(TaskFormControlDef c, TaskSemantic task)
    {
        if (!TryResolvePushButtonControlResource(c, task, out var resource) || resource is null)
            return "";

        var attrObj = NormalizeAttrObjKind(resource.AttrObj);
        if (!string.Equals(attrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(attrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase))
            return "";

        return string.IsNullOrWhiteSpace(resource.DefaultValue) ? "" : resource.DefaultValue!;
    }

    private static string ResolveDataColumnSelectBinding(
        string? dataColumn,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(dataColumn))
            return "";

        var token = dataColumn.Trim();
        var prefix = "";
        var currentTask = task;
        while (currentTask is not null)
        {
            if (currentTask.SelectsSemantic.ItemsByName.TryGetValue(token, out var sel) && sel is not null)
            {
                var primaryObj = currentTask.PrimaryDbObj ?? currentTask.InformationDbObj;
                var primaryMember = ResolvePrimaryMember(currentTask, primaryObj ?? 0, dataObjects);
                var expr = ResolveSelectExpression(sel, currentTask, dataObjects, primaryMember);
                if (IsSimpleMemberAccess(expr))
                    return string.IsNullOrWhiteSpace(prefix) ? expr : $"{prefix}.{expr}";
            }

            if (!currentTask.ParentOrdinal.HasValue)
                break;

            currentTask = GetTaskByOrdinal(currentTask.ParentOrdinal.Value, allTasks);
            prefix = string.IsNullOrWhiteSpace(prefix) ? "_parent" : $"{prefix}._parent";
        }

        return "";
    }

    private static void EmitTreeControlInitialization(
        StringBuilder code,
        TaskFormControlDef control,
        string varName,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        string indentation = "        ")
    {
        if (!string.Equals(control.Model, "CTRL_GUI0_TREE", StringComparison.OrdinalIgnoreCase))
            return;

        EmitTreeControlAssignment(code, control, varName, task, allTasks, dataObjects, "NodeID", control.TreeNodeIdColumn, control.TreeNodeIdExpressionId, indentation);
        EmitTreeControlAssignment(code, control, varName, task, allTasks, dataObjects, "ParentNodeID", control.TreeParentIdColumn, control.TreeParentIdExpressionId, indentation);
        EmitTreeControlAssignment(code, control, varName, task, allTasks, dataObjects, "Data", control.TreeDescriptionColumn, control.TreeDescriptionExpressionId, indentation);

        if (control.TreeRootExpressionId.HasValue)
        {
            if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(control.TreeRootExpressionId.Value, out var exp) && exp is not null)
            {
                var rootCode = ResolveTypedExpressionEntryCode(exp, task, dataObjects, CreateViewBindingEmissionContext(exp.Attribute)).Code;
                if (!string.IsNullOrWhiteSpace(rootCode))
                {
                    rootCode = PrefixControllerReferencesForView(rootCode, task, dataObjects);
                    code.AppendLine($"{indentation}{varName}.SetRootNodeId(() => {rootCode});");
                }
                else
                {
                    code.AppendLine($"{indentation}// GAP: Tree root binding not resolved for control {varName} (ControlId={control.Id}, RootExp={control.TreeRootExpressionId.Value}).");
                }
            }
            else
            {
                code.AppendLine($"{indentation}// GAP: Tree root binding not resolved for control {varName} (ControlId={control.Id}, RootExp={control.TreeRootExpressionId.Value}).");
            }
        }
    }

    private static void EmitTreeControlAssignment(
        StringBuilder code,
        TaskFormControlDef control,
        string varName,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        string propertyName,
        string? dataColumn,
        int? dataExpressionId,
        string indentation)
    {
        if (string.IsNullOrWhiteSpace(dataColumn) && !dataExpressionId.HasValue)
            return;

        var bindingControl = control with { DataColumn = dataColumn, DataExpressionId = dataExpressionId };
        var dataExpr = ResolveControlDataExpression(bindingControl, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(dataExpr))
        {
            code.AppendLine($"{indentation}{varName}.{propertyName} = _controller.{dataExpr};");
            return;
        }

        var bindingExpr = ResolveControlDataExpressionBindingForView(bindingControl, task, dataObjects, allTasks);
        if (!string.IsNullOrWhiteSpace(bindingExpr))
        {
            code.AppendLine($"{indentation}{varName}.{propertyName} = {bindingExpr};");
            return;
        }

        code.AppendLine($"{indentation}// GAP: Tree {propertyName} binding not resolved for control {varName} (ControlId={control.Id}, DataExpressionId={dataExpressionId?.ToString() ?? "?"}, DataColumn={dataColumn ?? "?"}).");
    }

    private static string ResolveDataColumnOrdinalBinding(string? dataColumn, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(dataColumn))
            return "";
        var token = dataColumn.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(token, "^[A-Z]+$"))
            return "";
        long slot = 0;
        foreach (var ch in token)
        {
            slot = (slot * 26) + (ch - 'A' + 1);
            if (slot > int.MaxValue)
                return "";
        }

        // In Magic XML forms, D/E/F... typically map to task columns starting at 1.
        var taskColumnIndex = (int)slot - 3;
        if (taskColumnIndex <= 0)
            return "";
        if (taskColumnIndex <= task.ResourcesSemantic.Ordered.Count)
        {
            var rc = task.ResourcesSemantic.Ordered[taskColumnIndex - 1];
            return ResolveTaskResourceMemberName(task, rc);
        }

        // In subtasks, view DataColumn can point to parent task columns.
        var parentPrefix = "_parent";
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal.Value, allTasks);
            if (parentTask is null)
                break;
            if (taskColumnIndex <= parentTask.ResourcesSemantic.Ordered.Count)
            {
                var prc = parentTask.ResourcesSemantic.Ordered[taskColumnIndex - 1];
                return $"{parentPrefix}.{ResolveTaskResourceMemberName(parentTask, prc)}";
            }
            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "._parent";
        }
        return "";
    }

    private static bool IsSimpleMemberAccess(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var parts = expr.Trim().Split('.');
        return parts.Length > 0 && parts.All(IsCSharpIdentifierSegment);
    }

    private static bool IsCSharpIdentifierSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        }

        return true;
    }

}
