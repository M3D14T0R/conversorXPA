using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitHandlerBody(
        StringBuilder sb,
        TaskHandlerDef h,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        int indentLevel)
    {
        var pad = new string(' ', indentLevel * 4);
        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        if (h.FormIos.Count > 0)
        {
            var writeCallMap = task.Layout.FormIoWriteCallsByFormEntryIndex;
            var readCallMap = task.Layout.FormIoReadCallsByFormEntryIndex;
            var items = new List<(int Order, TaskRowActionDef? Action, TaskFormIoDef? Io, int? LoopId, int? ConditionId)>();
            items.AddRange(h.Actions.Select(a => (ExtractLogicLineOrder(a.XmlTrace) ?? int.MaxValue, (TaskRowActionDef?)a, (TaskFormIoDef?)null, a.LoopConditionExpressionId, a.ConditionExpressionId)));
            items.AddRange(h.FormIos.Select(io => (ExtractLogicLineOrder(io.XmlTrace) ?? int.MaxValue, (TaskRowActionDef?)null, (TaskFormIoDef?)io, io.LoopConditionExpressionId, io.ConditionExpressionId)));
            items = items.OrderBy(x => x.Order).ToList();

            void EmitCombinedItem((int Order, TaskRowActionDef? Action, TaskFormIoDef? Io, int? LoopId, int? ConditionId) item, string itemPad, bool stripConditions = false)
            {
                if (item.Action is not null)
                {
                    if (stripConditions)
                        EmitRowActionCore(sb, StripActionCondition(item.Action), task, dataObjects, allTasks, itemPad, preferValueForResourceAssignments: true);
                    else
                        EmitRowAction(sb, item.Action, task, dataObjects, allTasks, itemPad);
                }
                else if (item.Io is not null && item.Io.FormEntryIndex.HasValue)
                {
                    var effectiveIo = stripConditions ? StripFormIoCondition(item.Io) : item.Io;
                    if (item.Io.OperationType == "O" && writeCallMap.TryGetValue(item.Io.FormEntryIndex.Value, out var writeCall))
                        EmitFormIoWrite(sb, effectiveIo, writeCall, task, dataObjects, itemPad);
                    else if (item.Io.OperationType == "I" && readCallMap.TryGetValue(item.Io.FormEntryIndex.Value, out var readCall))
                        EmitFormIoRead(sb, effectiveIo, readCall, task, dataObjects, itemPad);
                }
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.LoopId.HasValue)
                {
                    var loopCond = ResolveExpressionCode(item.LoopId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(loopCond))
                    {
                        sb.AppendLine($"{pad}u.StartBlockLoop();");
                        sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({loopCond}))");
                        sb.AppendLine($"{pad}{{");
                        while (i < items.Count && items[i].LoopId == item.LoopId)
                        {
                            EmitCombinedItem(items[i], pad + "    ", stripConditions: true);
                            i++;
                        }
                        i--;
                        sb.AppendLine($"{pad}}}");
                        sb.AppendLine($"{pad}u.EndBlockLoop();");
                        continue;
                    }
                }

                if (item.ConditionId.HasValue)
                {
                    var cond = ResolveExpressionCode(item.ConditionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
                    if (!string.IsNullOrWhiteSpace(cond))
                    {
                        sb.AppendLine($"{pad}if ({cond})");
                        sb.AppendLine($"{pad}{{");
                        while (i < items.Count && items[i].LoopId == item.LoopId && items[i].ConditionId == item.ConditionId)
                        {
                            EmitCombinedItem(items[i], pad + "    ", stripConditions: true);
                            i++;
                        }
                        i--;
                        sb.AppendLine($"{pad}}}");
                        continue;
                    }
                }

                EmitCombinedItem(item, pad);
            }

            EmitRaiseStatements(sb, h.Raises, task, dataObjects, pad);
            return;
        }
        if (h.Actions.Count > 0)
        {
            _handlerBodyByReference.TryGetValue(h, out var body);
            var orderedActions = body?.OrderedActions ?? h.Actions.OrderBy(a => ExtractLogicLineOrder(a.XmlTrace) ?? int.MaxValue).ToList();
            var blocks = body?.Blocks;
            if (CanEmitStructuredActionBody(orderedActions, h.Blocks, h.EndBlocks))
                EmitStructuredActionBody(sb, orderedActions, h.Blocks, h.EndBlocks, task, dataObjects, allTasks, pad);
            else
                EmitOrderedActions(sb, orderedActions, blocks, task, dataObjects, allTasks, pad);
            EmitRaiseStatements(sb, h.Raises, task, dataObjects, pad);
            return;
        }
        foreach (var remark in h.Remarks)
            EmitRemarkComment(sb, remark.Text, pad);
        foreach (var stop in h.Stops)
        {
            EmitXmlTraceComment(sb, stop.XmlTrace ?? h.XmlTrace, pad);
            EmitStopStatement(sb, stop, task, dataObjects, pad);
        }

        foreach (var up in h.Updates)
        {
            EmitXmlTraceComment(sb, up.XmlTrace ?? h.XmlTrace, pad);
            var target = ResolveUpdateTargetExpression(up.Variable, task, dataObjects, allTasks);
            if (string.IsNullOrWhiteSpace(target))
            {
                sb.AppendLine($"{pad}// Update skipped: unresolved variable {up.Variable}. XML={up.XmlTrace ?? h.XmlTrace ?? "?"}");
                continue;
            }
            var targetInfo = ResolveTargetValueInfo(task, up.Variable, target);
            var value = ResolveUpdateValueExpression(up.WithValue, task, dataObjects, CreateAssignmentEmissionContext(targetInfo, target));
            if (up.ConditionExpressionId.HasValue)
            {
                var cond = ResolveExpressionCode(up.ConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
                if (!string.IsNullOrWhiteSpace(cond))
                {
                    sb.AppendLine($"{pad}if ({cond})");
                    sb.AppendLine($"{pad}{{");
                    EmitUpdateAssignmentStatements(sb, $"{pad}    ", up, target, value, task);
                    sb.AppendLine($"{pad}}}");
                    continue;
                }
            }
            EmitUpdateAssignmentStatements(sb, pad, up, target, value, task);
        }

        EmitRaiseStatements(sb, h.Raises, task, dataObjects, pad);

        foreach (var call in h.Calls)
        {
            EmitXmlTraceComment(sb, call.XmlTrace ?? h.XmlTrace, pad);
            var callCondition = call.ConditionExpressionId.HasValue
                ? ResolveExpressionCode(call.ConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext())
                : "";
            var callPad = pad;
            if (!string.IsNullOrWhiteSpace(callCondition))
            {
                sb.AppendLine($"{pad}if ({callCondition})");
                sb.AppendLine($"{pad}{{");
                callPad = pad + "    ";
            }
            var targetTask = ResolveTaskByCall(task, call, allTasks);
            var callSemantics = ResolveCallSemantics(call, targetTask);
            if (!string.IsNullOrWhiteSpace(callSemantics))
                sb.AppendLine($"{callPad}// {callSemantics}");

            if (call.OperationType == "O")
            {
                EmitOperationOStatement(sb, call, task, callPad);
                if (!string.IsNullOrWhiteSpace(callCondition))
                    sb.AppendLine($"{pad}}}");
                continue;
            }

            if (!call.TaskId.HasValue)
            {
                sb.AppendLine($"{callPad}// Call fallback: missing TaskID (OperationType={call.OperationType}). XML={call.XmlTrace ?? h.XmlTrace ?? "?"}");
                if (!string.IsNullOrWhiteSpace(callCondition))
                    sb.AppendLine($"{pad}}}");
                continue;
            }

            if (targetTask is null)
            {
                var externalTargetClass = ResolveExternalTaskTypeReference(call);
                if (!string.IsNullOrWhiteSpace(externalTargetClass))
                {
                    var externalParameterTypes = ResolveCompatCallParameterTypes(call, targetTask);
                    var externalParameterDirections = ResolveCompatCallParameterDirections(targetTask);
                    var argsResolvedValues = externalParameterTypes is { Count: 0 }
                        ? Array.Empty<string>()
                        : ResolveCallArgumentExpressionsPreservingPositions(
                            call.ArgumentDefs,
                            call.ArgumentVariables,
                            task,
                            dataObjects,
                            selectMap,
                            allTasks,
                            externalParameterTypes,
                            externalParameterDirections);
                    argsResolvedValues = AlignAndEmitExternalCallArguments(
                        argsResolvedValues,
                        externalParameterTypes,
                        task);
                    if (externalParameterTypes is not null &&
                        argsResolvedValues.Count > externalParameterTypes.Count)
                    {
                        argsResolvedValues = argsResolvedValues.Take(externalParameterTypes.Count).ToArray();
                    }
                    var argsResolved = argsResolvedValues.ToArray();
                    var runArgsResolved = argsResolved.Length == 0 ? "" : string.Join(", ", argsResolved);
                    if (ShouldPreferExternalProgramCompatForResolvedExternalCall(call))
                    {
                        sb.AppendLine($"{callPad}// GAP: External component call downgraded to compat fallback (Component={call.TargetComponentName ?? "?"}, Obj={call.TargetObjectId?.ToString() ?? "?"}, Public={call.TargetPublicName ?? "?"}, OperationType={call.OperationType}). XML={call.XmlTrace ?? h.XmlTrace ?? "?"}");
                        sb.AppendLine($"{callPad}ExternalProgramCompat.{GetExternalProgramCompatMethodName(call)}({runArgsResolved});");
                    }
                    else
                    {
                        sb.AppendLine($"{callPad}{BuildRunCallForExternalTarget(externalTargetClass, runArgsResolved)}");
                    }
                    if (!string.IsNullOrWhiteSpace(callCondition))
                        sb.AppendLine($"{pad}}}");
                    continue;
                }

                var argsFallbackValues = ResolveCallArgumentExpressionsPreservingPositions(call.ArgumentDefs, call.ArgumentVariables, task, dataObjects, selectMap, allTasks);
                var argsFallback = argsFallbackValues.Count == 0 ? "" : string.Join(", ", argsFallbackValues);
                if (string.IsNullOrWhiteSpace(argsFallback))
                    sb.AppendLine($"{callPad}Application.Instance.AllPrograms.RunByIndex({call.TaskId.Value});");
                else
                {
                    sb.AppendLine($"{callPad}// Args do XML nao aplicados no fallback por indice: {argsFallback}");
                    sb.AppendLine($"{callPad}Application.Instance.AllPrograms.RunByIndex({call.TaskId.Value});");
                }
                if (!string.IsNullOrWhiteSpace(callCondition))
                    sb.AppendLine($"{pad}}}");
                continue;
            }
            var targetClass = ResolveTaskTypeReferenceForCall(task, targetTask);
            var targetParameters = GetTaskParameters(targetTask);
            var expectedParameterTypes = targetParameters.Select(p => p.ParameterType).ToArray();
            var expectedParameterDirections = targetParameters.Select(p => p.ParameterDirection).ToArray();
            var args = ResolveCallArgumentExpressionsPreservingPositions(
                call.ArgumentDefs,
                call.ArgumentVariables,
                task,
                dataObjects,
                selectMap,
                allTasks,
                expectedParameterTypes,
                expectedParameterDirections).ToArray();
            var runArgs = args.Length == 0 ? "" : string.Join(", ", args);
            sb.AppendLine($"{callPad}{BuildTaskRunStatement(task, targetTask, targetClass, call.OperationType, runArgs, call, allTasks, dataObjects)}");
            if (!string.IsNullOrWhiteSpace(callCondition))
                sb.AppendLine($"{pad}}}");
        }

        foreach (var invoke in h.Invokes)
        {
            EmitXmlTraceComment(sb, invoke.XmlTrace ?? h.XmlTrace, pad);
            EmitInvokeStatement(sb, invoke, task, dataObjects, pad);
        }
    }
}

