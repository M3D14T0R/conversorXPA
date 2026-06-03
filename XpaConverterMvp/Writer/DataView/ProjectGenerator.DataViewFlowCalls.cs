using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTabFlowCalls(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var tabCalls = GetStandaloneTabCalls(t);
        if (tabCalls.Count == 0)
            return;

        var selectMap = BuildSelectNameToExpressionMap(t, dataObjects);
        foreach (var call in tabCalls)
        {
            var targetTask = ResolveTaskByCall(t, call, allTasks);
            if (targetTask is null)
                continue;
            var targetClass = ResolveTaskTypeReferenceForCall(t, targetTask);
            var args = call.ArgumentVariables.Select(v => ResolveCallArgumentExpression(v, t, dataObjects, selectMap, allTasks)).ToArray();
            var run = args.Length == 0 ? "c.Run()" : $"c.Run({string.Join(", ", args)})";
            var dir = call.Direction switch
            {
                "F" => ", Direction.Forward",
                "B" => ", Direction.Backward",
                _ => ""
            };
            sb.AppendLine($"        Flow.Add<{targetClass}>(c => {run}, FlowMode.Tab{dir});");
        }
    }

    private static List<TaskCallDef> GetStandaloneTabCalls(TaskSemantic t)
    {
        var tabCalls = t.DataView.TabCalls.Where(c => c.OperationType == "T").ToList();
        if (tabCalls.Count == 0)
            return tabCalls;

        var handlerCalls = t.HandlersSemantic.Items
            .SelectMany(h => h.Calls)
            .Where(c => c.OperationType == "T")
            .ToList();

        if (handlerCalls.Count == 0)
            return tabCalls;

        return tabCalls.Where(tc => !handlerCalls.Any(hc => CallsEquivalent(tc, hc))).ToList();
    }

    private static bool CallsEquivalent(TaskCallDef a, TaskCallDef b)
    {
        if (!string.Equals(a.OperationType, b.OperationType, StringComparison.OrdinalIgnoreCase))
            return false;
        if (a.TaskId != b.TaskId)
            return false;
        if (!string.Equals(a.Direction ?? "", b.Direction ?? "", StringComparison.OrdinalIgnoreCase))
            return false;
        if (a.ArgumentVariables.Count != b.ArgumentVariables.Count)
            return false;

        for (var i = 0; i < a.ArgumentVariables.Count; i++)
        {
            if (!string.Equals((a.ArgumentVariables[i] ?? "").Trim(), (b.ArgumentVariables[i] ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool HasExpandBeforeFlow(TaskSemantic t)
    {
        return FindExpandBeforeFlowCalls(t).Count > 0;
    }

    private static List<(TaskHandlerDef Handler, TaskCallDef Call)> FindExpandBeforeFlowCalls(TaskSemantic t)
    {
        return t.HandlersSemantic.Items
            .Where(h => h.EventType == "U")
            .SelectMany(h => h.Calls.Select(c => (Handler: h, Call: c)))
            .Where(x =>
                x.Call.OperationType == "P" &&
                IsExpandEvent(t, x.Handler) &&
                x.Call.ArgumentVariables.Count <= 1)
            .ToList();
    }

    private static void EmitExpandBeforeFlowCalls(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var expandCalls = FindExpandBeforeFlowCalls(t);
        if (expandCalls.Count == 0)
            return;

        var selectMap = BuildSelectNameToExpressionMap(t, dataObjects);
        foreach (var ec in expandCalls)
        {
            var targetTask = ResolveTaskByCall(t, ec.Call, allTasks);
            if (targetTask is null)
                continue;
            var targetClass = ResolveTaskTypeReferenceForCall(t, targetTask);
            var args = ec.Call.ArgumentVariables
                .Select(v => ResolveCallArgumentExpression(v, t, dataObjects, selectMap, allTasks))
                .ToArray();
            var run = args.Length == 0 ? "c.Run()" : $"c.Run({string.Join(", ", args)})";
            sb.AppendLine($"        Flow.Add<{targetClass}>(c => {run}, FlowMode.ExpandBefore);");
        }
    }
}

