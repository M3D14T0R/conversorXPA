using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitOnEnterRow(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (t.Logic.RowLogics.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnEnterRow()");
        sb.AppendLine("    {");
        foreach (var row in t.Logic.RowLogics)
        {
            if (CanEmitStructuredRowLogic(row))
            {
                EmitStructuredRowLogic(sb, row, t, dataObjects, allTasks, "        ");
                continue;
            }

            foreach (var action in row.Actions)
            {
                if (!EmitDirectResourceAssignment(sb, action, t, dataObjects, allTasks, "        ", suppressForcedUndo: true))
                    EmitRowAction(sb, action, t, dataObjects, allTasks, "        ");
            }
            EmitRaiseStatements(sb, row.Raises, t, dataObjects, "        ");
        }
        sb.AppendLine("    }");
    }

    private static void EmitOnStart(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string? finalStatement = null)
    {
        var writeCallMap = BuildFormIoWriteCallMap(t);
        var startIos = t.Layout.StartTaskOutputIos;
        var primaryObj = t.PrimaryDbObj ?? t.InformationDbObj;
        var modelMembers = BuildModelMembers(t, dataObjects);
        var linkMembers = BuildLinkMembers(t, dataObjects, modelMembers, primaryObj);
        var taskEvaluatedLinks = linkMembers
            .Where(lb => lb.Link.ConditionExpressionId.HasValue && string.Equals(lb.Link.EvaluateConditionMode, "T", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var reloadAfterDataCreationConditions = t.Logic.StartLogics
            .SelectMany(row => row.Actions)
            .Where(action => action.Call is not null)
            .Select(action => new
            {
                Action = action,
                ConditionId = action.ConditionExpressionId ?? action.Call?.ConditionExpressionId
            })
            .Where(item => item.ConditionId.HasValue &&
                           t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(item.ConditionId.Value, out var expression) &&
                           expression.Syntax.Contains("DBRecs", StringComparison.OrdinalIgnoreCase))
            .Select(item => ResolveActionConditionCode(item.Action, t, dataObjects))
            .Where(condition => !string.IsNullOrWhiteSpace(condition))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (t.Logic.StartLogics.Count == 0 &&
            t.Logic.StartRaises.Count == 0 &&
            startIos.Count == 0 &&
            !t.HasStartLogicUnit &&
            taskEvaluatedLinks.Count == 0 &&
            string.IsNullOrWhiteSpace(finalStatement))
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnStart()");
        sb.AppendLine("    {");
        if (reloadAfterDataCreationConditions.Count > 0 && t.DataView.HasFrom)
            sb.AppendLine($"        var reloadDataAfterStart = {string.Join(" || ", reloadAfterDataCreationConditions.Select(condition => $"({condition})"))};");
        if (ShouldEmitCheckExitOnStart(t, dataObjects))
            sb.AppendLine("        CheckExit();");
        foreach (var row in t.Logic.StartLogics)
        {
            if (CanEmitStructuredRowLogic(row))
            {
                EmitStructuredRowLogic(sb, row, t, dataObjects, allTasks, "        ");
                continue;
            }
            foreach (var action in row.Actions)
            {
                if (!EmitDirectResourceAssignment(sb, action, t, dataObjects, allTasks, "        ", suppressForcedUndo: true))
                    EmitRowAction(sb, action, t, dataObjects, allTasks, "        ");
            }
            EmitRaiseStatements(sb, row.Raises, t, dataObjects, "        ");
        }
        foreach (var io in startIos)
        {
            if (!io.FormEntryIndex.HasValue || !writeCallMap.TryGetValue(io.FormEntryIndex.Value, out var writeCall))
                continue;
            EmitFormIoWrite(sb, io, writeCall, t, dataObjects, "        ");
        }
        foreach (var lb in taskEvaluatedLinks)
        {
            var enabledExpr = ResolveExpressionCode(lb.Link.ConditionExpressionId!.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
            if (!string.IsNullOrWhiteSpace(enabledExpr))
                sb.AppendLine($"        Relations[{lb.MemberName}].Enabled = {enabledExpr};");
        }
        if (reloadAfterDataCreationConditions.Count > 0 && t.DataView.HasFrom)
        {
            sb.AppendLine("        if (reloadDataAfterStart)");
            sb.AppendLine("            Raise(Command.ReloadData);");
        }
        if (!string.IsNullOrWhiteSpace(finalStatement))
            sb.AppendLine($"        {finalStatement}");
        sb.AppendLine("    }");
    }

    private static bool ShouldEmitCheckExitOnStart(TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!t.Execution.EndTaskCondition ||
            !string.Equals(t.Execution.EvaluateEndCondition, "I", StringComparison.OrdinalIgnoreCase) ||
            !t.Execution.EndTaskConditionExpressionId.HasValue)
            return false;

        var exitCondition = ResolveExpressionCode(t.Execution.EndTaskConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
        return !string.IsNullOrWhiteSpace(ResolveImmediateExitReevaluationArgument(exitCondition, t));
    }
}

