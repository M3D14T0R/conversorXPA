using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitHandlerProgramRegistrations(
        StringBuilder sb,
        string handlerVar,
        TaskHandlerDef h,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        foreach (var invoke in h.Invokes.Where(i => string.Equals(i.OperationType, "B", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryResolveInvokeProgramNameLiteral(task, invoke, dataObjects, out var programLiteral))
                continue;
            sb.AppendLine($"        {handlerVar}.RegisterCallByPublicName(() => {programLiteral});");
        }
    }

    private static bool TryResolveInvokeProgramNameLiteral(
        TaskSemantic task,
        TaskInvokeDef invoke,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string programLiteral)
    {
        programLiteral = "";
        if (invoke is null)
            return false;

        if (invoke.ProgramNameExpressionId.HasValue &&
            task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(invoke.ProgramNameExpressionId.Value, out var expr) &&
            expr is not null)
        {
            var literal = ResolveTypedExpressionEntryCode(expr, task, dataObjects, CreateProgramReferenceEmissionContext()).Code.Trim();
            if (literal.Length > 0 &&
                ((literal.StartsWith("\"", StringComparison.Ordinal) && literal.EndsWith("\"", StringComparison.Ordinal)) ||
                 (literal.StartsWith("@\"", StringComparison.Ordinal) && literal.EndsWith("\"", StringComparison.Ordinal))))
            {
                programLiteral = literal;
                return true;
            }
        }

        return false;
    }

    private static void EmitSystemEventHandler(StringBuilder sb, TaskHandlerDef h, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var keysExpr = ResolveKeyCombination(h.EventKeyCombinationId);
        if (string.IsNullOrWhiteSpace(keysExpr))
        {
            sb.AppendLine($"        // Handler EventType=S not mapped: KeyCombinationID={h.EventKeyCombinationId?.ToString() ?? "?"}. XML={h.XmlTrace ?? "?"}");
            return;
        }
        var handlerVar = $"hs_{Math.Abs((h.XmlTrace ?? keysExpr).GetHashCode())}";
        var scopeExpr = ResolveSystemHandlerScope(h);
        if (string.IsNullOrWhiteSpace(scopeExpr))
            sb.AppendLine($"        var {handlerVar} = Handlers.Add({keysExpr});");
        else
            sb.AppendLine($"        var {handlerVar} = Handlers.Add({keysExpr}, {scopeExpr});");
        var enabledExpId = h.ConditionExpressionId ?? ResolveEnabledExpressionIdFromRaisedControl(task, h);
        if (enabledExpId.HasValue)
        {
            var condExpr = ResolveExpressionCode(enabledExpId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
            if (!string.IsNullOrWhiteSpace(condExpr))
                sb.AppendLine($"        {handlerVar}.BindEnabled(() => {condExpr});");
        }
        sb.AppendLine($"        {handlerVar}.Invokes += e =>");
        sb.AppendLine("        {");
        if (h.Actions.Count > 0)
        {
            EmitHandlerBody(sb, h, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>(), 3);
        }
        else
        {
            EmitRaiseStatements(sb, h.Raises, task, dataObjects, "            ");
            if (h.Calls.Count > 0 || h.Stops.Count > 0 || h.Updates.Count > 0)
                EmitHandlerBody(sb, h, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>(), 3);
        }
        sb.AppendLine($"            e.Handled = {ResolveHandlerHandledExpression(h, task, dataObjects)};");
        sb.AppendLine("        };");
    }

    private static string ResolveSystemHandlerScope(TaskHandlerDef h)
    {
        return h.Scope switch
        {
            "T" => "HandlerScope.CurrentTaskOnly",
            _ => ""
        };
    }

    private static string ResolveHandlerHandledExpression(TaskHandlerDef h, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (int.TryParse(h.Propagate, out var propagate))
        {
            if (propagate == 78)
                return "true";

            var expId = Math.Abs(propagate);
            if (expId > 0)
            {
                var expr = ResolveExpressionCode(expId.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
                if (!string.IsNullOrWhiteSpace(expr))
                    return propagate < 0 ? $"!({expr})" : $"({expr})";
            }
        }

        return "true";
    }

    private static int? ResolveEnabledExpressionIdFromRaisedControl(TaskSemantic task, TaskHandlerDef handler)
    {
        if (!string.Equals(handler.EventType, "S", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!handler.EventKeyCombinationId.HasValue)
            return null;
        return task.View.SystemRaiseEnabledExpressionByKeyCombinationId.TryGetValue(handler.EventKeyCombinationId.Value, out var expressionId)
            ? expressionId
            : null;
    }
}

