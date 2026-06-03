using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitControlHandlerMethods(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var handlers = t.HandlersSemantic.Items
            .Where(h => string.Equals(h.Level, "C", StringComparison.OrdinalIgnoreCase))
            .Where(h => h.Type is "P" or "S" or "V")
            .Where(h => !string.IsNullOrWhiteSpace(h.Reference))
            .ToList();
        if (handlers.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("    #region Controls handlers");
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            var control = ResolveControlHandlerControl(h, t, allTasks, dataObjects);
            var methodName = ResolveControlHandlerMethodName(h, t, allTasks, dataObjects, control);
            if (!emitted.Add(methodName))
                continue;

            sb.AppendLine($"    internal void {methodName}()");
            sb.AppendLine("    {");
            var dataExpr = control is null ? "" : ResolveControlDataExpression(control, t, allTasks, dataObjects);
            if (h.Type == "V" && !string.IsNullOrWhiteSpace(dataExpr))
            {
                sb.AppendLine($"        if ({dataExpr}.WasChanged)");
                sb.AppendLine("        {");
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 3);
                sb.AppendLine("        }");
            }
            else
            {
                EmitHandlerBody(sb, h, t, dataObjects, allTasks, 2);
            }
            sb.AppendLine("    }");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }

    private static bool DoesControlMatchHandlerReference(
        TaskHandlerDef h,
        TaskFormControlDef c,
        TaskSemantic t,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(h.Reference))
            return false;
        if (string.Equals(c.ControlName, h.Reference, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(c.DataColumn, h.Reference, StringComparison.OrdinalIgnoreCase))
            return true;

        var handlerExpr = ResolveSelectExpressionByName(h.Reference!, t, dataObjects);
        if (string.IsNullOrWhiteSpace(handlerExpr))
            return false;

        var controlExpr = ResolveControlDataExpression(c, t, allTasks, dataObjects);
        if (string.IsNullOrWhiteSpace(controlExpr))
            return false;

        return string.Equals(handlerExpr, controlExpr, StringComparison.OrdinalIgnoreCase);
    }

    private static TaskFormControlDef? ResolveControlHandlerControl(
        TaskHandlerDef h,
        TaskSemantic t,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(h.Reference))
            return null;

        var cacheKey = $"{t.Ordinal}|{h.Reference}";
        if (_controlHandlerControlCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (GetControlHandlerExactControlMap(t).TryGetValue(h.Reference!, out var exactControl))
        {
            _controlHandlerControlCache[cacheKey] = exactControl;
            return exactControl;
        }

        var resolved = t.View.SelectedSupportedControls.FirstOrDefault(c =>
                           DoesControlMatchHandlerReference(h, c, t, allTasks, dataObjects))
                       ?? t.View.SelectedFormControls.FirstOrDefault(c =>
                           DoesControlMatchHandlerReference(h, c, t, allTasks, dataObjects));
        _controlHandlerControlCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ResolveControlHandlerMethodName(
        TaskHandlerDef h,
        TaskSemantic t,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        TaskFormControlDef? resolvedControl = null)
    {
        var cacheKey = $"{t.Ordinal}|{h.Type}|{h.Reference}";
        if (resolvedControl is null && _controlHandlerMethodNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var control = resolvedControl ?? ResolveControlHandlerControl(h, t, allTasks, dataObjects);
        var baseName = "";
        if (control is not null)
        {
            if (t.View.ControlVariableNameById.TryGetValue(control.Id, out var controlVar) &&
                !string.IsNullOrWhiteSpace(controlVar))
            {
                baseName = controlVar;
            }

            var dataExpr = ResolveControlDataExpression(control, t, allTasks, dataObjects);
            if (string.IsNullOrWhiteSpace(baseName) && !string.IsNullOrWhiteSpace(dataExpr))
            {
                var parts = dataExpr
                    .Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(ToPascalIdentifier)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (parts.Count > 0)
                    baseName = "txt" + string.Join("_", parts);
            }

        }

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "ctl" + ToPascalIdentifier(h.Reference ?? "Handler");

        var suffix = h.Type switch
        {
            "V" => "InputValidation",
            "P" => "Enter",
            "S" => "Leave",
            _ => "Handler"
        };
        var methodName = $"{baseName}_{suffix}";
        if (resolvedControl is null)
            _controlHandlerMethodNameCache[cacheKey] = methodName;
        return methodName;
    }

    private static Dictionary<string, TaskFormControlDef> GetControlHandlerExactControlMap(TaskSemantic t)
    {
        if (_controlHandlerExactControlMapCache.TryGetValue(t.Ordinal, out var cached))
            return cached;

        var map = new Dictionary<string, TaskFormControlDef>(StringComparer.OrdinalIgnoreCase);
        void Add(TaskFormControlDef control)
        {
            if (!string.IsNullOrWhiteSpace(control.ControlName) && !map.ContainsKey(control.ControlName!))
                map[control.ControlName!] = control;
            if (!string.IsNullOrWhiteSpace(control.DataColumn) && !map.ContainsKey(control.DataColumn!))
                map[control.DataColumn!] = control;
        }

        foreach (var control in t.View.SelectedSupportedControls)
            Add(control);
        foreach (var control in t.View.SelectedFormControls)
            Add(control);

        _controlHandlerExactControlMapCache[t.Ordinal] = map;
        return map;
    }

    private static string? ResolveControlHandlerEventName(TaskHandlerDef h)
    {
        return h.Type switch
        {
            "V" => "InputValidation",
            "P" => "Enter",
            "S" => "Leave",
            _ => null
        };
    }
}

