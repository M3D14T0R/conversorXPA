using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitBusinessProcessOnLeaveRow(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var className = ResolveTaskClassName(t, allTasks);
        var totalStopwatch = Stopwatch.StartNew();
        if (!HasBusinessProcessLeaveRowWork(t))
            return;

        sb.AppendLine();
        sb.AppendLine("    protected override void OnLeaveRow()");
        sb.AppendLine("    {");
        EmitBusinessProcessLeaveRowBody(sb, t, dataObjects, allTasks, "        ", className, totalStopwatch);
        sb.AppendLine("    }");
    }

    private static bool HasBusinessProcessLeaveRowWork(TaskSemantic t)
    {
        var rowIos = GetPrintRowIos(t);
        var readRowIos = GetTextIoReadRowIos(t);
        var hasRowActions = t.Logic.SavingRowLogics.Count > 0;
        var hasTabCalls = t.DataView.TabCalls.Any(c => c.OperationType == "T");
        return hasRowActions || rowIos.Count > 0 || readRowIos.Count > 0 || hasTabCalls;
    }

    private static void EmitBusinessProcessLeaveRowBody(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        string className,
        Stopwatch totalStopwatch)
    {
        var rowIos = GetPrintRowIos(t);
        var readRowIos = GetTextIoReadRowIos(t);
        var hasTabCalls = t.DataView.TabCalls.Any(c => c.OperationType == "T");
        var writeCallMap = TimeSection(() => BuildFormIoWriteCallMap(t), "LEAVEROW", className, "build-write-call-map");
        var readCallMap = TimeSection(() => BuildFormIoReadCallMap(t), "LEAVEROW", className, "build-read-call-map");

        if (!hasTabCalls &&
            CanEmitStructuredLeaveRows(t, rowIos, readRowIos))
        {
            foreach (var row in t.Logic.SavingRowLogics)
            {
                var rowFormIos = SelectStructuredRowFormIos(row, rowIos, readRowIos);
                EmitStructuredActionBody(sb, row.Actions, row.Blocks, row.EndBlocks, t, dataObjects, allTasks, pad, rowFormIos, writeCallMap, readCallMap);
            }

            var section = rowIos.Count == 0 && readRowIos.Count == 0
                ? "section=\"total-structured\""
                : "section=\"total-structured-formio\"";
            ConversionTelemetry.LogDuration("LEAVEROW", className, totalStopwatch.Elapsed, section);
            return;
        }
        var actionList = TimeSection(() => t.Logic.SavingRowLogics.SelectMany(r => r.Actions).ToList(), "LEAVEROW", className, "collect-row-actions");
        TimeSection(() =>
        {
            foreach (var c in t.DataView.TabCalls.Where(c => c.OperationType == "T"))
            {
                var alreadyExists = actionList.Any(a =>
                    a.Kind == "Call" &&
                    a.Call is not null &&
                    a.Call.OperationType == "T" &&
                    a.Call.TaskId == c.TaskId &&
                    a.Call.ArgumentVariables.SequenceEqual(c.ArgumentVariables));
                if (!alreadyExists)
                    actionList.Add(new TaskRowActionDef("Call", c, null, null, null, null, null, c.ConditionExpressionId, null, null, c.XmlTrace, null));
            }
        }, "LEAVEROW", className, "merge-tabcalls");
        var ordered = new List<(int Order, TaskRowActionDef? Action, TaskFormIoDef? Io)>();
        var nextSyntheticOrder = 1_000_000;
        TimeSection(() =>
        {
            foreach (var action in actionList)
            {
                var order = ExtractLogicLineOrder(action.XmlTrace) ?? nextSyntheticOrder++;
                ordered.Add((order, action, null));
            }
            foreach (var io in rowIos)
            {
                var order = ExtractLogicLineOrder(io.XmlTrace) ?? nextSyntheticOrder++;
                ordered.Add((order, null, io));
            }
            foreach (var io in readRowIos)
            {
                var order = ExtractLogicLineOrder(io.XmlTrace) ?? nextSyntheticOrder++;
                ordered.Add((order, null, io));
            }
        }, "LEAVEROW", className, "build-ordered-items");

        var sortStopwatch = Stopwatch.StartNew();
        var orderedItems = ordered.OrderBy(x => x.Order).ToList();
        ConversionTelemetry.LogDuration("LEAVEROW", className, sortStopwatch.Elapsed, "section=\"sort-ordered-items\"");

        var emitDirectTotal = TimeSpan.Zero;
        var emitActionTotal = TimeSpan.Zero;
        var emitWriteTotal = TimeSpan.Zero;
        var emitReadTotal = TimeSpan.Zero;
        var emitDirectCount = 0;
        var emitActionCount = 0;
        var emitWriteCount = 0;
        var emitReadCount = 0;
        var actionSummary = new Dictionary<string, (TimeSpan Total, int Count)>(StringComparer.OrdinalIgnoreCase);

        for (var itemIndex = 0; itemIndex < orderedItems.Count; itemIndex++)
        {
            var item = orderedItems[itemIndex];
            ConversionTelemetry.Log(
                "LEAVEROW_ITEM",
                $"{className} index={itemIndex} order={item.Order} kind={QuoteTelemetry(ResolveLeaveRowItemKind(item))} xml={QuoteTelemetry(ResolveLeaveRowItemXmlTrace(item))}");
            if (item.Action is not null)
            {
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"before-direct-update\" kind={QuoteTelemetry(item.Action.Kind)} xml={QuoteTelemetry(item.Action.XmlTrace)}");
                var directStopwatch = Stopwatch.StartNew();
                var emittedDirect = EmitDirectResourceAssignment(sb, item.Action, t, dataObjects, allTasks, pad);
                directStopwatch.Stop();
                emitDirectTotal += directStopwatch.Elapsed;
                emitDirectCount++;
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"after-direct-update\" emittedDirect={emittedDirect} elapsedMs={directStopwatch.Elapsed.TotalMilliseconds:F0} kind={QuoteTelemetry(item.Action.Kind)} xml={QuoteTelemetry(item.Action.XmlTrace)}");
                if (!emittedDirect)
                {
                    var actionLabel = ResolveRowActionTelemetryLabel(item.Action);
                    ConversionTelemetry.Log(
                        "LEAVEROW_ITEM",
                        $"{className} index={itemIndex} phase=\"before-row-action\" label={QuoteTelemetry(actionLabel)} kind={QuoteTelemetry(item.Action.Kind)} xml={QuoteTelemetry(item.Action.XmlTrace)}");
                    var rowActionStopwatch = Stopwatch.StartNew();
                    EmitRowAction(sb, item.Action, t, dataObjects, allTasks, pad);
                    rowActionStopwatch.Stop();
                    emitActionTotal += rowActionStopwatch.Elapsed;
                    emitActionCount++;
                    ConversionTelemetry.Log(
                        "LEAVEROW_ITEM",
                        $"{className} index={itemIndex} phase=\"after-row-action\" label={QuoteTelemetry(actionLabel)} elapsedMs={rowActionStopwatch.Elapsed.TotalMilliseconds:F0} kind={QuoteTelemetry(item.Action.Kind)} xml={QuoteTelemetry(item.Action.XmlTrace)}");
                    if (actionSummary.TryGetValue(actionLabel, out var existing))
                        actionSummary[actionLabel] = (existing.Total + rowActionStopwatch.Elapsed, existing.Count + 1);
                    else
                        actionSummary[actionLabel] = (rowActionStopwatch.Elapsed, 1);
                }
                continue;
            }
            if (item.Io is null || !item.Io.FormEntryIndex.HasValue)
                continue;
            if (item.Io.OperationType == "O" && writeCallMap.TryGetValue(item.Io.FormEntryIndex.Value, out var writeCall))
            {
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"before-formio-write\" formEntryIndex={item.Io.FormEntryIndex.Value} xml={QuoteTelemetry(item.Io.XmlTrace)}");
                var writeStopwatch = Stopwatch.StartNew();
                EmitFormIoWrite(sb, item.Io, writeCall, t, dataObjects, pad);
                writeStopwatch.Stop();
                emitWriteTotal += writeStopwatch.Elapsed;
                emitWriteCount++;
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"after-formio-write\" formEntryIndex={item.Io.FormEntryIndex.Value} elapsedMs={writeStopwatch.Elapsed.TotalMilliseconds:F0} xml={QuoteTelemetry(item.Io.XmlTrace)}");
                continue;
            }
            if (item.Io.OperationType == "I" && readCallMap.TryGetValue(item.Io.FormEntryIndex.Value, out var readCall))
            {
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"before-formio-read\" formEntryIndex={item.Io.FormEntryIndex.Value} xml={QuoteTelemetry(item.Io.XmlTrace)}");
                var readStopwatch = Stopwatch.StartNew();
                EmitFormIoRead(sb, item.Io, readCall, t, dataObjects, pad);
                readStopwatch.Stop();
                emitReadTotal += readStopwatch.Elapsed;
                emitReadCount++;
                ConversionTelemetry.Log(
                    "LEAVEROW_ITEM",
                    $"{className} index={itemIndex} phase=\"after-formio-read\" formEntryIndex={item.Io.FormEntryIndex.Value} elapsedMs={readStopwatch.Elapsed.TotalMilliseconds:F0} xml={QuoteTelemetry(item.Io.XmlTrace)}");
            }
        }

        ConversionTelemetry.LogDuration("LEAVEROW", className, emitDirectTotal, $"section=\"emit-direct-resource-assignment\" count={emitDirectCount}");
        ConversionTelemetry.LogDuration("LEAVEROW", className, emitActionTotal, $"section=\"emit-row-action\" count={emitActionCount}");
        ConversionTelemetry.LogDuration("LEAVEROW", className, emitWriteTotal, $"section=\"emit-formio-write\" count={emitWriteCount}");
        ConversionTelemetry.LogDuration("LEAVEROW", className, emitReadTotal, $"section=\"emit-formio-read\" count={emitReadCount}");
        foreach (var summary in actionSummary.OrderByDescending(x => x.Value.Total))
            ConversionTelemetry.LogDuration("ROWACTION", className, summary.Value.Total, $"section=\"summary:{summary.Key}\" count={summary.Value.Count}");
        ConversionTelemetry.LogDuration("LEAVEROW", className, totalStopwatch.Elapsed, "section=\"total\"");
    }

    private static string ResolveLeaveRowItemKind((int Order, TaskRowActionDef? Action, TaskFormIoDef? Io) item)
    {
        if (item.Action is not null)
            return $"Action:{item.Action.Kind}";
        if (item.Io is not null)
            return $"FormIo:{item.Io.OperationType}";
        return "Unknown";
    }

    private static string? ResolveLeaveRowItemXmlTrace((int Order, TaskRowActionDef? Action, TaskFormIoDef? Io) item)
        => item.Action?.XmlTrace ?? item.Io?.XmlTrace;

    private static bool CanEmitStructuredLeaveRows(
        TaskSemantic task,
        IReadOnlyList<TaskFormIoDef> rowIos,
        IReadOnlyList<TaskFormIoDef> readRowIos)
    {
        if (task.Logic.SavingRowLogics.Count == 0)
            return false;
        if (task.Logic.SavingRowLogics.Any(row => !CanEmitStructuredRowLogic(row)))
            return false;

        var allIos = rowIos.Concat(readRowIos).ToList();
        if (allIos.Count == 0)
            return true;

        return allIos.All(io => task.Logic.SavingRowLogics.Any(row => BelongsToStructuredRow(row, io)));
    }

    private static IReadOnlyList<TaskFormIoDef> SelectStructuredRowFormIos(
        TaskRowLogicDef row,
        IReadOnlyList<TaskFormIoDef> rowIos,
        IReadOnlyList<TaskFormIoDef> readRowIos)
    {
        return rowIos
            .Concat(readRowIos)
            .Where(io => BelongsToStructuredRow(row, io))
            .OrderBy(io => ExtractLogicLineOrder(io.XmlTrace) ?? int.MaxValue)
            .ToList();
    }

    private static bool BelongsToStructuredRow(TaskRowLogicDef row, TaskFormIoDef io)
    {
        var ioLogicUnit = ExtractLogicUnitIndex(io.XmlTrace);
        if (!ioLogicUnit.HasValue)
            return false;

        return row.Actions.Any(action => ExtractLogicUnitIndex(action.XmlTrace) == ioLogicUnit) ||
               row.Blocks.Any(block => ExtractLogicUnitIndex(block.XmlTrace) == ioLogicUnit) ||
               row.EndBlocks.Any(endBlock => ExtractLogicUnitIndex(endBlock.XmlTrace) == ioLogicUnit);
    }
}
