using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitInlineCommentBlock(StringBuilder sb, string pad, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                sb.AppendLine($"{pad}//");
                continue;
            }

            sb.AppendLine($"{pad}// {line}");
        }
    }

    private static void EmitFunctionOverrides(StringBuilder sb, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (task.FunctionOverridesSemantic.Count == 0)
            return;

        var emittedAny = false;
        foreach (var fn in task.FunctionOverridesSemantic)
        {
            if (!emittedAny)
            {
                sb.AppendLine();
                sb.AppendLine("    #region Functions");
                emittedAny = true;
            }

            var methodParams = fn.Parameters
                .Select(p => $"{p.ParameterType} {p.ParameterName}")
                .ToList();

            var accessibility = task.MainProgram ? "internal " : string.Empty;
            sb.AppendLine($"    {accessibility}{fn.ReturnType} {fn.MethodName}({string.Join(", ", methodParams)})");
            sb.AppendLine("    {");
            foreach (var p in fn.Parameters)
            {
                var targetExpr = ResolveSelectExpressionByName(p.SelectName, task, dataObjects);
                if (!string.IsNullOrWhiteSpace(targetExpr))
                    sb.AppendLine($"        {BuildFunctionOverrideParameterAssignment(task, targetExpr, p.ParameterType, p.ParameterName)}");
            }
            EmitMutableFunctionAssignmentInitializers(
                sb,
                fn,
                task,
                dataObjects,
                _allTasks ?? Array.Empty<TaskSemantic>());
            EmitInlineCommentBlock(sb, "        ", fn.Definition.Comment);
            foreach (var r in fn.Remarks)
                EmitInlineCommentBlock(sb, "        ", r);
            var preferLegacyFunctionActionEmission =
                fn.OrderedActions.Any(a => string.Equals(a.Kind, "Remark", StringComparison.OrdinalIgnoreCase));
            if (!preferLegacyFunctionActionEmission &&
                CanEmitStructuredActionBody(fn.OrderedActions, fn.Definition.Blocks, fn.Definition.EndBlocks))
                EmitStructuredActionBody(sb, fn.OrderedActions, fn.Definition.Blocks, fn.Definition.EndBlocks, task, dataObjects, Array.Empty<TaskSemantic>(), "        ");
            else
                EmitOrderedActions(sb, fn.OrderedActions, fn.Blocks, task, dataObjects, Array.Empty<TaskSemantic>(), "        ");
            var returnExpr = ResolveExpressionCode(fn.ReturnExpressionId?.ToString(), task, dataObjects, CreateReturnValueEmissionContext(fn.ReturnType));
            if (string.IsNullOrWhiteSpace(returnExpr))
                returnExpr = fn.ReturnType switch
                {
                    "Number" => "0",
                    "Date" => "XPARuntimeCore.Box.Date.Now",
                    "Time" => "XPARuntimeCore.Box.Time.Now",
                    "Bool" => "false",
                    _ => "\"\""
                };
            sb.AppendLine($"        return {returnExpr};");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (emittedAny)
            sb.AppendLine("    #endregion");
    }

    private static HashSet<string> ResolveMutableFunctionAssignmentTargets(
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in task.FunctionOverridesSemantic)
        {
            foreach (var action in function.OrderedActions)
            {
                var variable = action.Update?.Variable;
                if (string.IsNullOrWhiteSpace(variable))
                    continue;

                var target = ResolveUpdateTargetExpression(
                    variable,
                    task,
                    dataObjects,
                    allTasks);
                if (!string.IsNullOrWhiteSpace(target))
                    targets.Add(target);
            }
        }

        return targets;
    }

    private static void EmitMutableFunctionAssignmentInitializers(
        StringBuilder sb,
        FunctionOverrideSemantic function,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var functionLogicUnit = ExtractLogicUnitIndex(function.Definition.XmlTrace);
        if (!functionLogicUnit.HasValue)
            return;

        var mutableTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in function.OrderedActions)
        {
            var variable = action.Update?.Variable;
            if (string.IsNullOrWhiteSpace(variable))
                continue;

            var target = ResolveUpdateTargetExpression(
                variable,
                task,
                dataObjects,
                allTasks);
            if (!string.IsNullOrWhiteSpace(target))
                mutableTargets.Add(target);
        }

        foreach (var select in task.SelectsSemantic.Items)
        {
            if (!select.IsFunctionSelect ||
                select.IsParameter ||
                !select.AssignmentExpressionId.HasValue ||
                ExtractLogicUnitIndex(select.XmlTrace) != functionLogicUnit)
            {
                continue;
            }

            var target = ResolveSelectExpression(select, task, dataObjects, "");
            if (string.IsNullOrWhiteSpace(target) ||
                !mutableTargets.Contains(target))
            {
                continue;
            }

            var value = ResolveSelectBindValueExpression(
                select,
                task,
                dataObjects,
                target);
            value = AdjustBindValueExpressionForTarget(
                select,
                target,
                value,
                task,
                dataObjects);
            if (!string.IsNullOrWhiteSpace(value))
                sb.AppendLine($"        {target}.Value = {value};");
        }
    }

    private static string BuildFunctionOverrideParameterAssignment(TaskSemantic task, string targetExpr, string parameterType, string parameterName)
    {
        var targetInfo = ResolveTargetValueInfo(task, null, targetExpr);
        if (targetInfo.IsArray)
        {
            if (string.Equals(parameterType, "byte[]", StringComparison.Ordinal))
                return $"{targetExpr}.Value = {targetExpr}.FromByteArray({parameterName});";

            return $"{targetExpr}.Value = {parameterName};";
        }

        return $"{targetExpr}.SilentSet({parameterName});";
    }
}

