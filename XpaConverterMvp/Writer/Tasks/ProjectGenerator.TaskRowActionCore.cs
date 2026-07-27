using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool EmitDirectResourceAssignment(StringBuilder sb, TaskRowActionDef action, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks, string pad, bool suppressForcedUndo = false)
    {
        if (action.Kind != "Update" || action.Update is null)
            return false;

        var totalStopwatch = Stopwatch.StartNew();
        var up = action.Update;
        var className = ResolveTaskClassName(task, allTasks);
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, TimeSpan.Zero, $"section=\"start\" var={QuoteTelemetry(up.Variable ?? "?")}");
        var targetStopwatch = Stopwatch.StartNew();
        var target = ResolveUpdateTargetExpression(up.Variable, task, dataObjects, allTasks);
        targetStopwatch.Stop();
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, targetStopwatch.Elapsed, $"section=\"after-target\" var={QuoteTelemetry(up.Variable ?? "?")}");
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var resourceStopwatch = Stopwatch.StartNew();
        var resource = ResolveTaskResourceForAssignment(task, up.Variable, target);
        resourceStopwatch.Stop();
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, resourceStopwatch.Elapsed, $"section=\"after-resource\" var={QuoteTelemetry(up.Variable ?? "?")}");
        if (resource is null)
            return false;

        if (TryEmitUpdateWithoutValueStatements(
                sb,
                pad,
                up,
                target,
                task,
                dataObjects,
                suppressForcedUndo,
                includeCondition: true))
        {
            totalStopwatch.Stop();
            LogSlowDirectUpdate(
                task,
                allTasks,
                action,
                totalStopwatch.Elapsed,
                targetStopwatch.Elapsed,
                resourceStopwatch.Elapsed,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero);
            return true;
        }

        if (TryBuildUnsupportedUpdateExpressionComment(up.WithValue, task, out var unsupportedComment))
        {
            sb.AppendLine($"{pad}{unsupportedComment}");
            return true;
        }

        var targetInfoStopwatch = Stopwatch.StartNew();
        var targetInfo = ResolveTargetValueInfo(task, up.Variable, target, resource);
        var resolvedColumnType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
        targetInfo = RecalibrateTargetInfoFromResolvedColumnType(targetInfo, resolvedColumnType);
        targetInfoStopwatch.Stop();
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, targetInfoStopwatch.Elapsed, $"section=\"after-targetinfo\" var={QuoteTelemetry(up.Variable ?? "?")}");
        var valueStopwatch = Stopwatch.StartNew();
        var assignmentContext = CreateAssignmentEmissionContext(targetInfo, target);
        var value = ResolveUpdateValueExpression(up.WithValue, task, dataObjects, assignmentContext);
        valueStopwatch.Stop();
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, valueStopwatch.Elapsed, $"section=\"after-value\" var={QuoteTelemetry(up.Variable ?? "?")}");
        var typeEmissionStopwatch = Stopwatch.StartNew();
        var valueBeforeEmission = value;
        var directBooleanLiteral =
            (targetInfo.IsBoolean ||
             string.Equals(targetInfo.AttrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(targetInfo.AttrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(targetInfo.ModelAttrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(targetInfo.ModelAttrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase)) &&
            IsBooleanLiteralExpression(value);
        if (directBooleanLiteral)
        {
            value = value.Trim();
        }
        else
        {
            if (TryEmitAssignmentValueFromKnownTypes(value, up, task, targetInfo, out var knownTypedValue))
                value = knownTypedValue;
        }
        if (IsArrayAssignmentTarget(task, target, resource, resolvedColumnType, targetInfo) &&
            IsSourceNullExpression(value))
            value = "null";
        if (TryEmitBlobWrappedNewClrExpression(value, out var directClrAssignmentValue))
            value = directClrAssignmentValue;
        typeEmissionStopwatch.Stop();
        ConversionTelemetry.LogDuration("DIRECTUPDATE", className, typeEmissionStopwatch.Elapsed, $"section=\"after-type-emission\" var={QuoteTelemetry(up.Variable ?? "?")}");
        if (typeEmissionStopwatch.Elapsed.TotalMilliseconds >= 500)
        {
            var slowEmissionAttrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
                ? targetInfo.AttrObj
                : targetInfo.ModelAttrObj ?? "";
            ConversionTelemetry.LogDuration(
                "DIRECTUPDATE_DETAIL",
                className,
                typeEmissionStopwatch.Elapsed,
                $"section=\"slow-type-emission\" var={QuoteTelemetry(up.Variable ?? "?")} target={QuoteTelemetry(target)} attr={QuoteTelemetry(slowEmissionAttrObj)} before={QuoteTelemetry(TruncateTelemetryValue(valueBeforeEmission))} after={QuoteTelemetry(TruncateTelemetryValue(value))}");
        }
        if (targetInfo.IsDotNet)
        {
            value = EmitDotNetAssignmentExpression(value, resource.ObjectType);
            var conditionStopwatch = Stopwatch.StartNew();
            var dotNetCondition = up.ConditionExpressionId.HasValue
                ? ResolveExpressionCode(up.ConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext())
                : "";
            conditionStopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(dotNetCondition))
            {
                sb.AppendLine($"{pad}if ({dotNetCondition})");
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    {target} = {value};");
                sb.AppendLine($"{pad}}}");
            }
            else
            {
                sb.AppendLine($"{pad}{target} = {value};");
            }
            totalStopwatch.Stop();
            LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionStopwatch.Elapsed);
            return true;
        }
        var conditionSharedStopwatch = Stopwatch.StartNew();
        var condition = up.ConditionExpressionId.HasValue
            ? ResolveExpressionCode(up.ConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext())
            : "";
        conditionSharedStopwatch.Stop();
        var effectiveAttrObj = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
            ? targetInfo.AttrObj
            : targetInfo.ModelAttrObj ?? "";
        if (TryBuildBlobVariantAssignment(target, value, effectiveAttrObj, preferValue: up.ForcedUpdate, out var blobVariantAssignment))
        {
            if (!string.IsNullOrWhiteSpace(condition))
            {
                sb.AppendLine($"{pad}if ({condition})");
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    {blobVariantAssignment}");
                if (up.ForcedUpdate && !suppressForcedUndo)
                    sb.AppendLine($"{pad}    u.DenyUndoFor({target});");
                sb.AppendLine($"{pad}}}");
            }
            else
            {
                sb.AppendLine($"{pad}{blobVariantAssignment}");
                if (up.ForcedUpdate && !suppressForcedUndo)
                    sb.AppendLine($"{pad}u.DenyUndoFor({target});");
            }
            totalStopwatch.Stop();
            LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionSharedStopwatch.Elapsed);
            return true;
        }
        if (targetInfo.IsBlob && !targetInfo.IsArray)
        {
            if (string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                var nullAssignment = $"{target}.Value = null;";
                if (!string.IsNullOrWhiteSpace(condition))
                {
                    sb.AppendLine($"{pad}if ({condition})");
                    sb.AppendLine($"{pad}{{");
                    sb.AppendLine($"{pad}    {nullAssignment}");
                    if (up.ForcedUpdate && !suppressForcedUndo)
                        sb.AppendLine($"{pad}    u.DenyUndoFor({target});");
                    sb.AppendLine($"{pad}}}");
                }
                else
                {
                    sb.AppendLine($"{pad}{nullAssignment}");
                    if (up.ForcedUpdate && !suppressForcedUndo)
                        sb.AppendLine($"{pad}u.DenyUndoFor({target});");
                }
                totalStopwatch.Stop();
                LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionSharedStopwatch.Elapsed);
                return true;
            }
            var assignment = $"{target}.Value = {value};";
            if (!string.IsNullOrWhiteSpace(condition))
            {
                sb.AppendLine($"{pad}if ({condition})");
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    {assignment}");
                if (up.ForcedUpdate && !suppressForcedUndo)
                    sb.AppendLine($"{pad}    u.DenyUndoFor({target});");
                sb.AppendLine($"{pad}}}");
            }
            else
            {
                sb.AppendLine($"{pad}{assignment}");
                if (up.ForcedUpdate && !suppressForcedUndo)
                    sb.AppendLine($"{pad}u.DenyUndoFor({target});");
            }
            totalStopwatch.Stop();
            LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionSharedStopwatch.Elapsed);
            return true;
        }
        if (action.LoopConditionExpressionId.HasValue)
        {
            var loopConditionStopwatch = Stopwatch.StartNew();
            var loopCond = ResolveExpressionCode(action.LoopConditionExpressionId.Value.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
            loopConditionStopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(loopCond))
            {
                sb.AppendLine($"{pad}u.StartBlockLoop();");
                sb.AppendLine($"{pad}while(u.AdvanceBlockLoop() &&({loopCond}))");
                sb.AppendLine($"{pad}{{");
                if (!string.IsNullOrWhiteSpace(condition))
                {
                    sb.AppendLine($"{pad}    if ({condition})");
                    sb.AppendLine($"{pad}    {{");
                    sb.AppendLine($"{pad}        {target}.Value = {value};");
                    if (up.ForcedUpdate && !suppressForcedUndo)
                        sb.AppendLine($"{pad}        u.DenyUndoFor({target});");
                    sb.AppendLine($"{pad}    }}");
                }
                else
                {
                    sb.AppendLine($"{pad}    {target}.Value = {value};");
                    if (up.ForcedUpdate && !suppressForcedUndo)
                        sb.AppendLine($"{pad}    u.DenyUndoFor({target});");
                }
                sb.AppendLine($"{pad}}}");
                sb.AppendLine($"{pad}u.EndBlockLoop();");
                totalStopwatch.Stop();
                LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionSharedStopwatch.Elapsed + loopConditionStopwatch.Elapsed);
                return true;
            }
        }
        if (!string.IsNullOrWhiteSpace(condition))
        {
            sb.AppendLine($"{pad}if ({condition})");
            sb.AppendLine($"{pad}{{");
            sb.AppendLine($"{pad}    {target}.Value = {value};");
            if (up.ForcedUpdate && !suppressForcedUndo)
                sb.AppendLine($"{pad}    u.DenyUndoFor({target});");
            sb.AppendLine($"{pad}}}");
        }
        else
        {
            sb.AppendLine($"{pad}{target}.Value = {value};");
            if (up.ForcedUpdate && !suppressForcedUndo)
                sb.AppendLine($"{pad}u.DenyUndoFor({target});");
        }
        totalStopwatch.Stop();
        LogSlowDirectUpdate(task, allTasks, action, totalStopwatch.Elapsed, targetStopwatch.Elapsed, resourceStopwatch.Elapsed, targetInfoStopwatch.Elapsed, valueStopwatch.Elapsed, typeEmissionStopwatch.Elapsed, conditionSharedStopwatch.Elapsed);
        return true;
    }

    private static string TruncateTelemetryValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        const int maxLength = 500;
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }

    private static void LogSlowDirectUpdate(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        TaskRowActionDef action,
        TimeSpan total,
        TimeSpan target,
        TimeSpan resource,
        TimeSpan targetInfo,
        TimeSpan value,
        TimeSpan typeEmission,
        TimeSpan condition)
    {
        if (total.TotalMilliseconds < 1000)
            return;

        var className = ResolveTaskClassName(task, allTasks);
        var variable = action.Update?.Variable ?? "?";
        ConversionTelemetry.LogDuration(
            "DIRECTUPDATE",
            className,
            total,
            $"var={QuoteTelemetry(variable)} target-ms={(long)target.TotalMilliseconds} resource-ms={(long)resource.TotalMilliseconds} targetinfo-ms={(long)targetInfo.TotalMilliseconds} value-ms={(long)value.TotalMilliseconds} type-emission-ms={(long)typeEmission.TotalMilliseconds} condition-ms={(long)condition.TotalMilliseconds}");
    }

    private static void EmitRowActionCore(
        StringBuilder sb,
        TaskRowActionDef action,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks,
        string pad,
        bool preferValueForResourceAssignments = false,
        bool suppressForcedUndo = false)
    {
        if (action.Kind == "Call" && action.Call is not null)
        {
            var call = action.Call;
            EmitXmlTraceComment(sb, action.XmlTrace ?? call.XmlTrace, pad);
            var targetTask = ResolveTaskByCall(t, call, allTasks);
            var selectMap = BuildSelectNameToExpressionMap(t, dataObjects);
            if (targetTask is null)
            {
                var externalTargetClass = ResolveExternalTaskTypeReference(call);
                if (!string.IsNullOrWhiteSpace(externalTargetClass))
                {
                    var externalParameterTypes = ResolveCompatCallParameterTypes(call, targetTask);
                    var externalParameterDirections = ResolveCompatCallParameterDirections(targetTask);
                    var externalArgValues = externalParameterTypes is { Count: 0 }
                        ? Array.Empty<string>()
                        : ResolveCallArgumentExpressionsPreservingPositions(
                            call.ArgumentDefs,
                            call.ArgumentVariables,
                            t,
                            dataObjects,
                            selectMap,
                            allTasks,
                            externalParameterTypes,
                            externalParameterDirections);
                    if (externalParameterTypes is not null &&
                        externalArgValues.Count > externalParameterTypes.Count)
                    {
                        externalArgValues = externalArgValues.Take(externalParameterTypes.Count).ToArray();
                    }
                    var externalArgs = externalArgValues.ToArray();
                    var externalRunArgs = externalArgs.Length == 0 ? "" : string.Join(", ", externalArgs);
                    if (ShouldPreferExternalProgramCompatForResolvedExternalCall(call))
                    {
                        sb.AppendLine($"{pad}// GAP: External component call downgraded to compat fallback (Component={call.TargetComponentName ?? "?"}, Obj={call.TargetObjectId?.ToString() ?? "?"}, Public={call.TargetPublicName ?? "?"}, OperationType={call.OperationType}). XML={call.XmlTrace ?? action.XmlTrace ?? "?"}");
                        sb.AppendLine($"{pad}ExternalProgramCompat.{GetExternalProgramCompatMethodName(call)}({externalRunArgs});");
                    }
                    else
                    {
                        sb.AppendLine($"{pad}{BuildRunCallForExternalTarget(externalTargetClass, externalRunArgs)}");
                    }
                    return;
                }
                sb.AppendLine($"{pad}// GAP: RowAction Call target not resolved (TaskID={call.TaskId?.ToString() ?? "?"}, Component={call.TargetComponentName ?? "?"}, Obj={call.TargetObjectId?.ToString() ?? "?"}, Public={call.TargetPublicName ?? "?"}, OperationType={call.OperationType}). XML={call.XmlTrace ?? action.XmlTrace ?? "?"}");
                if (ShouldEmitExternalProgramCompat(t, call, allTasks))
                {
                    var unresolvedParameterTypes = ResolveCompatCallParameterTypes(call, targetTask);
                    var unresolvedParameterDirections = ResolveCompatCallParameterDirections(targetTask);
                    var unresolvedArgs = ResolveCallArgumentExpressionsPreservingPositions(
                        call.ArgumentDefs,
                        call.ArgumentVariables,
                        t,
                        dataObjects,
                        selectMap,
                        allTasks,
                        unresolvedParameterTypes,
                        unresolvedParameterDirections).ToArray();
                    var unresolvedRunArgs = unresolvedArgs.Length == 0 ? "" : string.Join(", ", unresolvedArgs);
                    sb.AppendLine($"{pad}ExternalProgramCompat.{GetExternalProgramCompatMethodName(call)}({unresolvedRunArgs});");
                }
                return;
            }
            var targetClass = ResolveTaskTypeReferenceForCall(t, targetTask);
            var targetParameters = GetTaskParameters(targetTask);
            var expectedParameterTypes = targetParameters.Select(p => p.ParameterType).ToArray();
            var expectedParameterDirections = targetParameters.Select(p => p.ParameterDirection).ToArray();
            var args = ResolveCallArgumentExpressionsPreservingPositions(
                call.ArgumentDefs,
                call.ArgumentVariables,
                t,
                dataObjects,
                selectMap,
                allTasks,
                expectedParameterTypes,
                expectedParameterDirections).ToArray();
            var runArgs = args.Length == 0 ? "" : string.Join(", ", args);
            sb.AppendLine($"{pad}{BuildTaskRunStatement(t, targetTask, targetClass, call.OperationType, runArgs, call, allTasks, dataObjects)}");
            return;
        }

        if (action.Kind == "Update" && action.Update is not null)
        {
            var up = action.Update;
            EmitXmlTraceComment(sb, action.XmlTrace ?? up.XmlTrace, pad);
            var target = ResolveUpdateTargetExpression(up.Variable, t, dataObjects, allTasks);
            if (string.IsNullOrWhiteSpace(target))
            {
                sb.AppendLine($"{pad}// Update skipped: unresolved variable {up.Variable}. XML={up.XmlTrace ?? action.XmlTrace ?? "?"}");
                return;
            }
            var targetInfo = ResolveTargetValueInfo(t, up.Variable, target);
            var value = ResolveUpdateValueExpression(up.WithValue, t, dataObjects, CreateAssignmentEmissionContext(targetInfo, target));
            if (up.ConditionExpressionId.HasValue)
            {
                var cond = ResolveExpressionCode(up.ConditionExpressionId.Value.ToString(), t, dataObjects, CreateBooleanConditionEmissionContext());
                if (!string.IsNullOrWhiteSpace(cond))
                {
                    sb.AppendLine($"{pad}if ({cond})");
                    EmitUpdateAssignmentStatements(sb, $"{pad}    ", up, target, value, t, preferValueForResourceAssignments, suppressForcedUndo);
                    return;
                }
            }
            EmitUpdateAssignmentStatements(sb, pad, up, target, value, t, preferValueForResourceAssignments, suppressForcedUndo);
            return;
        }

        if (action.Kind == "Stop" && action.Stop is not null)
        {
            EmitXmlTraceComment(sb, action.XmlTrace ?? action.Stop.XmlTrace, pad);
            EmitStopStatement(sb, action.Stop with { ConditionExpressionId = null }, t, dataObjects, pad);
            return;
        }

        if (action.Kind == "Evaluate" && action.EvaluateExpressionId.HasValue)
        {
            EmitXmlTraceComment(sb, action.XmlTrace, pad);
            var expressionId = action.EvaluateExpressionId.Value.ToString();
            t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(action.EvaluateExpressionId.Value, out var evaluateExpressionEntry);
            var typedExpression = ResolveTypedExpressionEntryCode(
                evaluateExpressionEntry,
                t,
                dataObjects,
                CreateEvaluateStatementEmissionContext());
            var exprCode = typedExpression.Code;
            if (!string.IsNullOrWhiteSpace(exprCode) && !string.IsNullOrWhiteSpace(action.EvaluateReturnVariable))
            {
                var target = ResolveUpdateTargetExpression(action.EvaluateReturnVariable!, t, dataObjects, allTasks);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var targetInfo = ResolveTargetValueInfo(t, action.EvaluateReturnVariable!, target);
                    exprCode = ResolveExpressionCode(expressionId, t, dataObjects, CreateAssignmentEmissionContext(targetInfo, target));
                    if (TryBuildDotNetByRefInteropReturnAssignmentStatement(
                            exprCode,
                            t,
                            evaluateExpressionEntry,
                            action.EvaluateReturnVariable!,
                            target,
                            action.XmlTrace,
                            out var byRefAssignment))
                    {
                        sb.AppendLine($"{pad}{byRefAssignment}");
                    }
                    else
                    {
                        sb.AppendLine($"{pad}{BuildReturnAssignmentExpression(action.EvaluateReturnVariable!, target, exprCode, t, action.XmlTrace)}");
                    }
                }
                else
                    sb.AppendLine($"{pad}// GAP: Evaluate return target not resolved ({action.EvaluateReturnVariable}). XML={action.XmlTrace ?? "?"}");
            }
            else if (!string.IsNullOrWhiteSpace(exprCode))
            {
                var statement = BuildEvaluateStatement(typedExpression, t, evaluateExpressionEntry);
                statement = statement.Replace(Environment.NewLine, Environment.NewLine + pad);
                sb.AppendLine($"{pad}{statement}");
            }
            else
                sb.AppendLine($"{pad}// GAP: Evaluate expression not resolved (Exp={action.EvaluateExpressionId.Value}). XML={action.XmlTrace ?? "?"}");
            return;
        }

        if (action.Kind == "Invoke" && action.Invoke is not null)
        {
            EmitXmlTraceComment(sb, action.XmlTrace ?? action.Invoke.XmlTrace, pad);
            EmitInvokeStatement(sb, action.Invoke, t, dataObjects, pad);
            return;
        }

        if (action.Kind == "Remark")
        {
            EmitRemarkComment(sb, action.RemarkText, pad);
            return;
        }

        sb.AppendLine($"{pad}// GAP: RowAction kind '{action.Kind}' not mapped. XML={action.XmlTrace ?? "?"}");
    }
}

