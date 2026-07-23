using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitRowAction(
        StringBuilder sb,
        TaskRowActionDef action,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        bool suppressForcedUndo = false)
    {
        var className = ResolveTaskClassName(t, allTasks);
        var actionLabel = ResolveRowActionTelemetryLabel(action);
        var totalStopwatch = Stopwatch.StartNew();
        if (action.LoopConditionExpressionId.HasValue)
        {
            var loopCond = ResolveExpressionCode(action.LoopConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
            loopCond = NormalizeStatementBooleanConditionSyntax(loopCond);
            if (!string.IsNullOrWhiteSpace(loopCond))
            {
                sb.AppendLine($"{pad}u.StartBlockLoop();");
                sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({loopCond}))");
                sb.AppendLine($"{pad}{{");
                var loopAction = action with { LoopConditionExpressionId = null };
                var loopActionCond = ResolveActionConditionCode(loopAction, t, dataObjects);
                if (!string.IsNullOrWhiteSpace(loopActionCond) &&
                    loopActionCond.Contains("u.LoopCounter()", StringComparison.Ordinal))
                {
                    EmitRowActionCore(sb, StripActionCondition(loopAction), t, dataObjects, allTasks, pad + "    ", preferValueForResourceAssignments: true, suppressForcedUndo: suppressForcedUndo);
                }
                else
                {
                    EmitRowAction(sb, loopAction, t, dataObjects, allTasks, pad + "    ", suppressForcedUndo);
                }
                sb.AppendLine($"{pad}}}");
                sb.AppendLine($"{pad}u.EndBlockLoop();");
                if (totalStopwatch.ElapsedMilliseconds >= 250)
                    ConversionTelemetry.LogDuration("ROWACTION", className, totalStopwatch.Elapsed, $"section={QuoteTelemetry(actionLabel)}");
                return;
            }
        }
        var actionCond = ResolveActionConditionCode(action, t, dataObjects);
        actionCond = NormalizeStatementBooleanConditionSyntax(actionCond);
        if (action.Kind == "Call" &&
            action.Call is not null &&
            !string.IsNullOrWhiteSpace(actionCond) &&
            ContainsFunctionCallOutsideQuotes(actionCond, "u.LoopCounter") &&
            !ContainsLogicalConjunctionOutsideQuotes(actionCond))
        {
            sb.AppendLine($"{pad}u.StartBlockLoop();");
            sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({actionCond}))");
            sb.AppendLine($"{pad}{{");
            var loopAction = action with { ConditionExpressionId = null, Call = action.Call with { ConditionExpressionId = null } };
            EmitRowActionCore(sb, loopAction, t, dataObjects, allTasks, pad + "    ", suppressForcedUndo: suppressForcedUndo);
            sb.AppendLine($"{pad}}}");
            sb.AppendLine($"{pad}u.EndBlockLoop();");
            if (totalStopwatch.ElapsedMilliseconds >= 250)
                ConversionTelemetry.LogDuration("ROWACTION", className, totalStopwatch.Elapsed, $"section={QuoteTelemetry(actionLabel)}");
            return;
        }
        if (!string.IsNullOrWhiteSpace(actionCond))
        {
            sb.AppendLine($"{pad}if ({actionCond})");
            sb.AppendLine($"{pad}{{");
            EmitRowActionCore(sb, StripActionCondition(action), t, dataObjects, allTasks, pad + "    ", suppressForcedUndo: suppressForcedUndo);
            sb.AppendLine($"{pad}}}");
            if (totalStopwatch.ElapsedMilliseconds >= 250)
                ConversionTelemetry.LogDuration("ROWACTION", className, totalStopwatch.Elapsed, $"section={QuoteTelemetry(actionLabel)}");
            return;
        }
        EmitRowActionCore(sb, action, t, dataObjects, allTasks, pad, suppressForcedUndo: suppressForcedUndo);
        if (totalStopwatch.ElapsedMilliseconds >= 250)
            ConversionTelemetry.LogDuration("ROWACTION", className, totalStopwatch.Elapsed, $"section={QuoteTelemetry(actionLabel)}");
    }
}

