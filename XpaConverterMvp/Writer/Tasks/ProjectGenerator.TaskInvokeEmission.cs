using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitInvokeStatement(
        StringBuilder sb,
        TaskInvokeDef invoke,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        string pad)
    {
        var cond = ResolveExpressionCode(invoke.ConditionExpressionId?.ToString(), task, dataObjects, CreateBooleanConditionEmissionContext());
        var innerPad = pad;
        if (!string.IsNullOrWhiteSpace(cond))
        {
            sb.AppendLine($"{pad}if ({cond})");
            sb.AppendLine($"{pad}{{");
            innerPad = pad + "    ";
        }

        if (invoke.OperationType == "O")
        {
            var program = ResolveExpressionCode(invoke.CommandExpressionId?.ToString(), task, dataObjects, CreateProgramReferenceEmissionContext());
            if (string.IsNullOrWhiteSpace(program))
                program = ResolveExpressionCode(invoke.ProgramNameExpressionId?.ToString(), task, dataObjects, CreateProgramReferenceEmissionContext());
            if (string.IsNullOrWhiteSpace(program))
                program = "\"\"";
            sb.AppendLine($"{innerPad}ENV.Windows.OSCommand({program});");
            if (!string.IsNullOrWhiteSpace(cond))
                sb.AppendLine($"{pad}}}");
            return;
        }

        if (invoke.OperationType == "B")
        {
            var program = ResolveExpressionCode(invoke.ProgramNameExpressionId?.ToString(), task, dataObjects, CreateProgramReferenceEmissionContext());
            if (!string.IsNullOrWhiteSpace(program))
            {
                var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
                var argValues = ResolveArgumentExpressions(
                        invoke.ArgumentDefs,
                        invoke.ArgumentVariables,
                        task,
                        dataObjects,
                        selectMap,
                        _allTasks ?? Array.Empty<TaskSemantic>())
                    .ToArray();
                var callExpr = $"Application.Instance.AllPrograms.RunByPublicName({program}{(argValues.Length > 0 ? ", " + string.Join(", ", argValues) : "")})";

                if (!string.IsNullOrWhiteSpace(invoke.ReturnVariable))
                {
                    var target = ResolveUpdateTargetExpression(invoke.ReturnVariable!, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var valueExpr = CoerceInvokeReturnValue(callExpr, invoke.ReturnValue, invoke.ReturnVariable!, target, task);
                        sb.AppendLine($"{innerPad}{BuildReturnAssignmentExpression(invoke.ReturnVariable!, target, valueExpr, task, invoke.XmlTrace)}");
                    }
                    else
                    {
                        sb.AppendLine($"{innerPad}{callExpr};");
                    }
                }
                else
                {
                    sb.AppendLine($"{innerPad}{callExpr};");
                }
                if (!string.IsNullOrWhiteSpace(cond))
                    sb.AppendLine($"{pad}}}");
                return;
            }
            var cabinet = ResolveExpressionCode(invoke.CabinetNameExpressionId?.ToString(), task, dataObjects, CreateProgramReferenceEmissionContext());
            if (!string.IsNullOrWhiteSpace(cabinet) && !string.IsNullOrWhiteSpace(program))
            {
                sb.AppendLine($"{innerPad}RunControllerFromAnUnreferencedApplication({cabinet}, {program});");
                if (!string.IsNullOrWhiteSpace(cond))
                    sb.AppendLine($"{pad}}}");
                return;
            }
        }

        if (invoke.OperationType == "E")
        {
            var program = ResolveExpressionCode(invoke.TaskIdExpressionId?.ToString(), task, dataObjects, CreateProgramIndexEmissionContext());
            if (string.IsNullOrWhiteSpace(program))
                program = ResolveExpressionCode(invoke.ProgramNameExpressionId?.ToString(), task, dataObjects, CreateProgramIndexEmissionContext());
            if (string.IsNullOrWhiteSpace(program))
                program = ResolveExpressionCode(invoke.CommandExpressionId?.ToString(), task, dataObjects, CreateProgramIndexEmissionContext());
            if (!string.IsNullOrWhiteSpace(program))
            {
                var callExpr = $"Application.Instance.AllPrograms.RunByIndex({program})";
                if (!string.IsNullOrWhiteSpace(invoke.ReturnVariable))
                {
                    var target = ResolveUpdateTargetExpression(invoke.ReturnVariable!, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var valueExpr = CoerceInvokeReturnValue(callExpr, invoke.ReturnValue, invoke.ReturnVariable!, target, task);
                        sb.AppendLine($"{innerPad}{BuildReturnAssignmentExpression(invoke.ReturnVariable!, target, valueExpr, task, invoke.XmlTrace)}");
                    }
                    else
                    {
                        sb.AppendLine($"{innerPad}{callExpr};");
                    }
                }
                else
                {
                    sb.AppendLine($"{innerPad}{callExpr};");
                }
                if (!string.IsNullOrWhiteSpace(cond))
                    sb.AppendLine($"{pad}}}");
                return;
            }
        }

        if (invoke.OperationType == ".")
        {
            var requestedFunctionName = string.IsNullOrWhiteSpace(invoke.FunctionName) ? "func" : invoke.FunctionName!;
            var functionName = ResolveSnippetCallableFunctionName(invoke.SnippetCode, requestedFunctionName);
            var snippetClass = ResolveSnippetClassName(task, invoke, _allTasks ?? Array.Empty<TaskSemantic>());
            var snippetQualifiedClass = $"{BuildSnippetNamespace(snippetClass)}.{snippetClass}";
            var activeArgumentDefs = invoke.ArgumentDefs?
                .Where(arg => arg.Skip != true &&
                              (arg.ExpressionId.HasValue ||
                               arg.Exp.HasValue ||
                               !string.IsNullOrWhiteSpace(arg.Variable)))
                .ToList();
            var hasSnippetSignature = TryGetSnippetFunctionParameters(invoke.SnippetCode, functionName, out var parameters);
            var snippetReturnContract = TryGetSnippetFunctionReturnContract(invoke.SnippetCode, functionName, out var snippetReturnType)
                ? snippetReturnType
                : "";
            var snippetParameterTypes = hasSnippetSignature
                ? ResolveSnippetExpectedParameterTypes(parameters)
                : null;
            var snippetParameterDirections = hasSnippetSignature
                ? ResolveSnippetExpectedParameterDirections(parameters)
                : null;
            var argValues = ResolveArgumentExpressions(
                    invoke.ArgumentDefs,
                    invoke.ArgumentVariables,
                    task,
                    dataObjects,
                    BuildSelectNameToExpressionMap(task, dataObjects),
                    _allTasks ?? Array.Empty<TaskSemantic>(),
                    snippetParameterTypes,
                    snippetParameterDirections)
                .ToList();
            var stagedSnippetArgs = new List<(string TempName, string TargetExpr, string ValueType, string ReadExpr, string WriteExpr, bool InitializeFromValue)>();
            if (hasSnippetSignature)
            {
                argValues = NormalizeSnippetArgumentValues(argValues, snippetParameterTypes, task);

                while (argValues.Count > parameters.Count &&
                       activeArgumentDefs is not null &&
                       activeArgumentDefs.Count > 0 &&
                       !string.IsNullOrWhiteSpace(invoke.ReturnVariable) &&
                       string.Equals(activeArgumentDefs[0].Variable, invoke.ReturnVariable, StringComparison.OrdinalIgnoreCase))
                {
                    argValues.RemoveAt(0);
                    activeArgumentDefs.RemoveAt(0);
                }
                argValues = argValues.Take(parameters.Count).ToList();
                argValues = ApplySnippetParameterSinks(argValues, parameters, task);
                for (var i = 0; i < argValues.Count && i < parameters.Count; i++)
                {
                    if (!SnippetParameterRequiresRef(parameters[i]))
                        continue;

                    var modifier = GetSnippetParameterModifier(parameters[i]);
                    var argumentDef = activeArgumentDefs is not null && i < activeArgumentDefs.Count
                        ? activeArgumentDefs[i]
                        : null;
                    if (TryResolveSnippetDotNetResourceArgument(task, argValues[i], parameters[i], argumentDef, out var dotNetTargetExpr))
                    {
                        argValues[i] = $"{modifier} {dotNetTargetExpr}";
                        continue;
                    }
                    if (TryResolveSnippetColumnArgument(task, dataObjects, argValues[i], parameters[i], argumentDef, out var targetExpr, out var valueType, out var readExpr, out var writeExpr))
                    {
                        var tempName = $"__snippetArg{i + 1}";
                        stagedSnippetArgs.Add((tempName, targetExpr, valueType, readExpr, writeExpr, !string.Equals(modifier, "out", StringComparison.OrdinalIgnoreCase)));
                        argValues[i] = $"{modifier} {tempName}";
                    }
                    else
                    {
                        argValues[i] = $"{modifier} {argValues[i]}";
                    }
                }
            }
            var callExpr = $"{snippetQualifiedClass}.{functionName}({string.Join(", ", argValues)})";
            if (stagedSnippetArgs.Count > 0)
            {
                sb.AppendLine($"{innerPad}Try(() =>");
                sb.AppendLine($"{innerPad}{{");
                foreach (var staged in stagedSnippetArgs)
                {
                    if (staged.InitializeFromValue)
                        sb.AppendLine($"{innerPad}    {staged.ValueType} {staged.TempName} = {staged.ReadExpr};");
                    else
                        sb.AppendLine($"{innerPad}    {staged.ValueType} {staged.TempName} = default;");
                }

                if (!string.IsNullOrWhiteSpace(invoke.ReturnVariable))
                {
                    var target = ResolveUpdateTargetExpression(invoke.ReturnVariable!, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var assignmentExpr = BuildReturnAssignmentExpression(
                            invoke.ReturnVariable!,
                            target,
                            RenderSnippetReturnContractForTarget(callExpr, snippetReturnContract, invoke.ReturnVariable!, target, task),
                            task,
                            invoke.XmlTrace).Trim().TrimEnd(';');
                        sb.AppendLine($"{innerPad}    {assignmentExpr};");
                    }
                    else
                    {
                        sb.AppendLine($"{innerPad}    {callExpr};");
                    }
                }
                else
                {
                    sb.AppendLine($"{innerPad}    {callExpr};");
                }

                foreach (var staged in stagedSnippetArgs)
                    sb.AppendLine($"{innerPad}    {staged.WriteExpr.Replace("__TEMP__", staged.TempName, StringComparison.Ordinal)};");
                sb.AppendLine($"{innerPad}}});");
                if (!string.IsNullOrWhiteSpace(cond))
                    sb.AppendLine($"{pad}}}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(invoke.ReturnVariable))
            {
                var target = ResolveUpdateTargetExpression(invoke.ReturnVariable!, task, dataObjects, _allTasks ?? Array.Empty<TaskSemantic>());
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var assignmentExpr = BuildReturnAssignmentExpression(
                        invoke.ReturnVariable!,
                        target,
                        RenderSnippetReturnContractForTarget(callExpr, snippetReturnContract, invoke.ReturnVariable!, target, task),
                        task,
                        invoke.XmlTrace).Trim().TrimEnd(';');
                    sb.AppendLine($"{innerPad}Try(() => {assignmentExpr});");
                    if (!string.IsNullOrWhiteSpace(cond))
                        sb.AppendLine($"{pad}}}");
                    return;
                }
            }

            sb.AppendLine($"{innerPad}Try(() => {callExpr});");
            if (!string.IsNullOrWhiteSpace(cond))
                sb.AppendLine($"{pad}}}");
            return;
        }

        sb.AppendLine($"{innerPad}// Invoke skipped: OperationType={invoke.OperationType}, EventType={invoke.EventType ?? "?"}, Condition={cond}. XML={invoke.XmlTrace ?? "?"}");
        if (!string.IsNullOrWhiteSpace(cond))
            sb.AppendLine($"{pad}}}");
    }

    private static string RenderSnippetReturnContractForTarget(
        string callExpr,
        string sourceReturnType,
        string returnVariable,
        string target,
        TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(callExpr) || string.IsNullOrWhiteSpace(sourceReturnType))
            return callExpr;

        var targetInfo = ResolveTargetValueInfo(task, returnVariable, target);
        var context = CreateAssignmentEmissionContext(targetInfo, target);
        var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return callExpr;

        var evidence = new[]
        {
            CreateEvidence(sourceReturnType, EmittedExpressionTypeEvidenceKind.FunctionContract, "snippet-return"),
            CreateEvidence(expectedReturnType, EmittedExpressionTypeEvidenceKind.SinkExpectedType, target, isExpectedType: true)
        };
        var request = new EmittedExpressionRequest(callExpr.Trim(), context.SinkKind.ToString(), expectedReturnType, target);
        return StrictEmittedExpressionEngine.TryEmitFromReliableEvidence(request, evidence, out var emitted)
            ? emitted.Code
            : callExpr;
    }

    private static List<string> ApplySnippetParameterSinks(IReadOnlyList<string> argValues, IReadOnlyList<string> parameters, TaskSemantic task)
    {
        var adjusted = argValues.ToList();
        for (var i = 0; i < adjusted.Count && i < parameters.Count; i++)
        {
            if (SnippetParameterRequiresRef(parameters[i]))
                continue;

            var expectedClrType = ResolveSnippetParameterClrType(parameters[i], "");
            if (!IsSnippetTextParameter(expectedClrType))
                continue;

            var value = adjusted[i].Trim();
            if (IsWholeStringLiteralExpression(value) ||
                value.StartsWith("u.CastToText(", StringComparison.Ordinal))
                continue;

            if (TryEmitExpectedArgumentFromReliableEvidence(value, "Text", task, out var emitted))
                adjusted[i] = emitted;
        }

        return adjusted;
    }
}

