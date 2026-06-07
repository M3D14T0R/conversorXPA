using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string ResolveEventLiteralCommand(string eventDescription)
{
    foreach (var task in _allTasks)
    {
        if (task.EventsSemantic.CommandByDescription.TryGetValue(eventDescription, out var mapped))
            return mapped;
    }
    return "Command." + ToPascalIdentifier(eventDescription);
}

private static string ResolveExpressionInvocationLiteral(string ordinalText)
{
    if (!int.TryParse(ordinalText, out var ordinal))
        return $"Exp_{ordinalText}()";
    foreach (var task in _allTasks)
    {
        if (task.ExpressionsSemantic.InvocationByOrdinal.TryGetValue(ordinal, out var invocation))
            return invocation;
    }
    return $"Exp_{ordinal}()";
}

private static string BuildIncrementalAssignment(string target, string value)
{
    return IsSelfIncrementExpression(value, target)
        ? $"{target}.Value++;"
        : $"{target}.AddDeltaOf(() => {value});";
}

private static bool IsSelfIncrementExpression(string value, string target)
{
    if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(target))
        return false;

    if (!TrySplitTopLevelBinary(value.Trim(), '+', out var left, out var right))
        return false;

    return (IsEquivalentTargetReference(left, target) && IsNumericOneLiteral(right)) ||
           (IsEquivalentTargetReference(right, target) && IsNumericOneLiteral(left));
}

private static bool TrySplitTopLevelBinary(string expression, char op, out string left, out string right)
{
    left = "";
    right = "";
    var depth = 0;
    var inDouble = false;
    for (var i = 0; i < expression.Length; i++)
    {
        var ch = expression[i];
        if (ch == '"' && (i == 0 || expression[i - 1] != '\\'))
        {
            inDouble = !inDouble;
            continue;
        }
        if (inDouble)
            continue;
        if (ch == '(')
        {
            depth++;
            continue;
        }
        if (ch == ')')
        {
            depth--;
            continue;
        }
        if (ch == op && depth == 0)
        {
            left = expression[..i].Trim();
            right = expression[(i + 1)..].Trim();
            return left.Length > 0 && right.Length > 0;
        }
    }
    return false;
}

private static bool IsEquivalentTargetReference(string candidate, string target)
{
    return string.Equals(candidate.Trim(), target.Trim(), StringComparison.Ordinal);
}

private static bool IsNumericOneLiteral(string candidate)
{
    return string.Equals(candidate.Trim(), "1", StringComparison.Ordinal);
}

private static string BuildEvaluateStatement(string exprCode, TaskSemantic? task = null, ExpressionEntrySemantic? expr = null)
{
    if (task is not null &&
        expr is not null &&
        TryBuildDnRefEvaluateStatement(exprCode, task, expr, out var dnRefStatement))
        return dnRefStatement;

    if (task is not null &&
        TryRenderEvaluateAssignmentStatement(exprCode, task, out var assignmentStatement))
        return assignmentStatement;

    var statementExpr = StripRedundantOuterParentheses(exprCode.Trim());
    if (IsCSharpInvocationStatementExpression(statementExpr))
        return $"{statementExpr};";

    return TryGetWholeCSharpStringLiteral(statementExpr, out var literal)
        ? $"CalcExpression({literal});"
        : $"_ = {statementExpr};";
}

private static string BuildEvaluateStatement(EmittedExpression expression, TaskSemantic? task = null, ExpressionEntrySemantic? expr = null)
{
    var exprCode = expression.Code;
    if (task is not null &&
        expr is not null &&
        TryBuildDnRefEvaluateStatement(exprCode, task, expr, out var dnRefStatement))
        return dnRefStatement;

    if (task is not null &&
        TryRenderEvaluateAssignmentStatement(exprCode, task, out var assignmentStatement))
        return assignmentStatement;

    if (expression.CanEmitAsStatement)
        return $"{StripRedundantOuterParentheses(exprCode.Trim())};";

    var statementExpr = StripRedundantOuterParentheses(exprCode.Trim());
    if (IsCSharpInvocationStatementExpression(statementExpr))
        return $"{statementExpr};";

    return TryGetWholeCSharpStringLiteral(statementExpr, out var literal)
        ? $"CalcExpression({literal});"
        : $"_ = {statementExpr};";
}

private static bool IsCSharpInvocationStatementExpression(string expression)
{
    if (string.IsNullOrWhiteSpace(expression))
        return false;

    var trimmed = StripRedundantOuterParentheses(expression.Trim());
    return TryParseFunctionCall(trimmed, out var functionName, out _) &&
           !string.IsNullOrWhiteSpace(functionName);
}

private static bool TryRenderEvaluateAssignmentStatement(string exprCode, TaskSemantic task, out string statement)
{
    statement = "";
    if (string.IsNullOrWhiteSpace(exprCode))
        return false;

    var trimmed = StripRedundantOuterParentheses(exprCode.Trim());
    var assignment = SplitTopLevelAssignmentExpression(trimmed);
    if (assignment is null)
        return false;

    var target = assignment.Value.Left.Trim();
    if (string.IsNullOrWhiteSpace(target) ||
        target.EndsWith(".Value", StringComparison.Ordinal) ||
        target.EndsWith(".Text", StringComparison.Ordinal) ||
        target.EndsWith(".Data", StringComparison.Ordinal))
        return false;

    return TryRenderColumnAssignmentStatement(task, target, assignment.Value.Right.Trim(), out statement);
}

private static bool TryRenderColumnAssignmentStatement(TaskSemantic task, string target, string value, out string statement)
{
    statement = "";
    if (string.IsNullOrWhiteSpace(target) ||
        string.IsNullOrWhiteSpace(value) ||
        target.EndsWith(".Value", StringComparison.Ordinal) ||
        target.EndsWith(".Text", StringComparison.Ordinal) ||
        target.EndsWith(".Data", StringComparison.Ordinal))
        return false;

    var targetInfo = ResolveTargetValueInfo(task, null, target);
    var hasColumnEvidence =
        targetInfo.Resource is not null ||
        !string.IsNullOrWhiteSpace(targetInfo.AttrObj) ||
        !string.IsNullOrWhiteSpace(targetInfo.ModelAttrObj);
    if (!hasColumnEvidence || targetInfo.IsDotNet)
        return false;

    statement = BuildUpdateAssignment(
        new TaskUpdateDef(target, "", null, false, false, null, null, null, false, null),
        target,
        value.Trim(),
        task,
        preferValueForResourceAssignments: true);
    return true;
}

private static bool TryBuildDnRefEvaluateStatement(string exprCode, TaskSemantic task, ExpressionEntrySemantic expr, out string statement)
{
    statement = "";

    if (string.IsNullOrWhiteSpace(expr.Syntax) ||
        expr.Syntax.IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) < 0)
        return false;

    if (!TryParseFunctionCall(expr.Syntax.Trim(), out _, out var originalArgs) ||
        !TryParseFunctionCall(exprCode.Trim(), out var emittedFunctionName, out var emittedArgs) ||
        originalArgs.Count != emittedArgs.Count)
        return false;

    if (TryBuildDotNetByRefInteropWrapperStatement(
            task,
            originalArgs,
            emittedFunctionName,
            emittedArgs,
            out statement))
        return true;

    if (TryBuildSingleStringDnRefInteropStatement(
            task,
            originalArgs,
            emittedFunctionName,
            emittedArgs,
            out statement))
        return true;

    var preamble = new List<string>();
    var callArgs = new List<string>(emittedArgs.Count);
    var epilogue = new List<string>();
    var handledDnRef = false;

    for (var i = 0; i < emittedArgs.Count; i++)
    {
        var originalArg = originalArgs[i].Trim();
        var emittedArg = emittedArgs[i].Trim();

        if (!TryParseFunctionCall(originalArg, out var originalArgFunctionName, out var originalArgArgs) ||
            !string.Equals(originalArgFunctionName, "DNRef", StringComparison.OrdinalIgnoreCase) ||
            originalArgArgs.Count != 1)
        {
            callArgs.Add(emittedArg);
            continue;
        }

        handledDnRef = true;
        var tempName = $"__dnref_{i}";
        var targetInfo = ResolveTargetValueInfo(task, null, emittedArg);

        if (targetInfo.IsBlob)
        {
            preamble.Add($"var {tempName} = u.ByteArrayToText({emittedArg}).ToString();");
            epilogue.Add($"{emittedArg}.Value = u.CastToByteArray({tempName});");
            TrackCriticalExternalCoercion(
                "DotNetByRef",
                nameof(TryBuildDnRefEvaluateStatement),
                "dnref-blob-bridge",
                $"arg={i} target={emittedArg}");
        }
        else if (string.Equals(targetInfo.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(targetInfo.ModelAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
        {
            var initialText = emittedArg.EndsWith(".Value", StringComparison.Ordinal)
                ? emittedArg
                : $"((string){emittedArg})";
            preamble.Add($"var {tempName} = {initialText} ?? \"\";");
            if (emittedArg.EndsWith(".Value", StringComparison.Ordinal))
                epilogue.Add($"{emittedArg} = {tempName};");
            else
                epilogue.Add($"{emittedArg}.Value = {tempName};");
        }
        else
        {
            return false;
        }

        callArgs.Add($"out {tempName}");
    }

    if (!handledDnRef)
        return false;

    var lines = new List<string>();
    lines.AddRange(preamble);
    lines.Add($"{emittedFunctionName}({string.Join(", ", callArgs)});");
    lines.AddRange(epilogue);
    statement = string.Join(Environment.NewLine, lines);
    return true;
}

private static bool TryBuildDotNetByRefInteropWrapperStatement(
    TaskSemantic task,
    IReadOnlyList<string> originalArgs,
    string emittedFunctionName,
    IReadOnlyList<string> emittedArgs,
    out string statement)
{
    statement = "";
    if (!TryBuildDotNetByRefInteropWrapperExpression(
            task,
            originalArgs,
            emittedFunctionName,
            emittedArgs,
            out var expression))
        return false;

    statement = $"{expression};";
    return true;
}

private static bool TryBuildDotNetByRefInteropReturnAssignmentStatement(
    string exprCode,
    TaskSemantic task,
    ExpressionEntrySemantic? expr,
    string returnVariable,
    string target,
    string? xmlTrace,
    out string statement)
{
    statement = "";
    if (expr is null ||
        string.IsNullOrWhiteSpace(expr.Syntax) ||
        expr.Syntax.IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) < 0 ||
        !TryParseFunctionCall(expr.Syntax.Trim(), out _, out var originalArgs) ||
        !TryParseFunctionCall(exprCode.Trim(), out var emittedFunctionName, out var emittedArgs) ||
        originalArgs.Count != emittedArgs.Count)
    {
        return false;
    }

    if (!TryBuildDotNetByRefInteropWrapperExpression(
            task,
            originalArgs,
            emittedFunctionName,
            emittedArgs,
            out var expression))
        return false;

    statement = BuildReturnAssignmentExpression(returnVariable, target, expression, task, xmlTrace);
    return true;
}

private static bool TryBuildDotNetByRefInteropWrapperExpression(
    TaskSemantic task,
    IReadOnlyList<string> originalArgs,
    string emittedFunctionName,
    IReadOnlyList<string> emittedArgs,
    out string expression)
{
    expression = "";
    if (!TryResolveDotNetByRefMethodContractForEmittedCall(
            emittedFunctionName,
            task,
            emittedArgs.Count,
            out var contract) ||
        contract.Parameters.Count != emittedArgs.Count ||
        !contract.Parameters.Any(parameter => parameter.IsByRef))
    {
        return false;
    }

    for (var i = 0; i < contract.Parameters.Count; i++)
    {
        if (!contract.Parameters[i].IsByRef)
            continue;

        var originalArg = originalArgs[i].Trim();
        if (!TryResolveDnRefTargetArgument(task, originalArg, emittedArgs[i].Trim(), out _))
            return false;
    }

    var dot = emittedFunctionName.LastIndexOf('.');
    if (dot <= 0 || dot + 1 >= emittedFunctionName.Length)
        return false;

    var instance = emittedFunctionName[..dot].Trim();
    var methodName = emittedFunctionName[(dot + 1)..].Trim();
    if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(methodName))
        return false;

    var args = new List<string>(emittedArgs.Count + 1) { instance };
    for (var i = 0; i < emittedArgs.Count; i++)
    {
        if (contract.Parameters[i].IsByRef)
        {
            if (!TryResolveDnRefTargetArgument(task, originalArgs[i].Trim(), emittedArgs[i].Trim(), out var byRefTarget))
                return false;
            args.Add(byRefTarget);
            continue;
        }

        args.Add(emittedArgs[i].Trim());
    }

    expression = $"DotNetByRefInterop.{methodName}({string.Join(", ", args)})";
    return true;
}

private static bool TryResolveDnRefTargetArgument(TaskSemantic task, string originalArg, string emittedArg, out string target)
{
    target = "";
    if (!TryParseFunctionCall(originalArg, out var originalArgFunctionName, out var originalArgArgs) ||
        !string.Equals(originalArgFunctionName, "DNRef", StringComparison.OrdinalIgnoreCase) ||
        originalArgArgs.Count != 1)
        return false;

    var emittedTarget = StripRedundantOuterParentheses((emittedArg ?? "").Trim());
    if (!string.IsNullOrWhiteSpace(emittedTarget))
    {
        var emittedTargetInfo = ResolveTargetValueInfo(task, null, emittedTarget);
        if (emittedTargetInfo.Resource is not null && !emittedTargetInfo.IsDotNet)
        {
            target = emittedTarget;
            return true;
        }
    }

    var originalTarget = originalArgArgs[0].Trim();
    if (string.IsNullOrWhiteSpace(originalTarget))
        return false;

    var targetInfo = ResolveTargetValueInfo(task, null, originalTarget);
    if (targetInfo.Resource is null || string.IsNullOrWhiteSpace(targetInfo.TargetMember))
        return false;

    if (IsSimpleIdentifierPath(originalTarget))
    {
        target = targetInfo.TargetMember;
        return true;
    }

    if (originalTarget.StartsWith("_parent.", StringComparison.Ordinal) ||
        originalTarget.StartsWith("Application.Instance.", StringComparison.Ordinal))
    {
        target = originalTarget.EndsWith(".Value", StringComparison.Ordinal)
            ? originalTarget[..^".Value".Length]
            : originalTarget;
        return true;
    }

    return false;
}

private static bool TryBuildSingleStringDnRefInteropStatement(
    TaskSemantic task,
    IReadOnlyList<string> originalArgs,
    string emittedFunctionName,
    IReadOnlyList<string> emittedArgs,
    out string statement)
{
    statement = "";
    var dnRefIndex = -1;
    for (var i = 0; i < originalArgs.Count; i++)
    {
        var originalArg = originalArgs[i].Trim();
        if (!TryParseFunctionCall(originalArg, out var originalArgFunctionName, out var originalArgArgs) ||
            !string.Equals(originalArgFunctionName, "DNRef", StringComparison.OrdinalIgnoreCase) ||
            originalArgArgs.Count != 1)
            continue;

        if (dnRefIndex >= 0)
            return false;
        dnRefIndex = i;
    }

    if (dnRefIndex < 0)
        return false;

    var parameterIsOut = true;
    if (TryResolveDotNetByRefMethodContractForEmittedCall(
            emittedFunctionName,
            task,
            emittedArgs.Count,
            out var contract))
    {
        if (contract.Parameters.Count != emittedArgs.Count)
            return false;

        var parameter = contract.Parameters[dnRefIndex];
        if (!parameter.IsByRef ||
            !string.Equals(parameter.ClrType, "string", StringComparison.Ordinal))
            return false;
        parameterIsOut = parameter.IsOut;
    }

    var target = emittedArgs[dnRefIndex].Trim();
    var targetInfo = ResolveTargetValueInfo(task, null, target);
    if (!targetInfo.IsBlob &&
        !string.Equals(targetInfo.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(targetInfo.ModelAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
        return false;

    var tempName = $"__dnref_{dnRefIndex}";
    var callArgs = emittedArgs.Select(arg => arg.Trim()).ToArray();
    callArgs[dnRefIndex] = parameterIsOut ? $"out {tempName}" : $"ref {tempName}";

    var helper = parameterIsOut ? "InvokeStringOut" : "InvokeStringRef";
    var lambdaParameter = parameterIsOut ? $"out string {tempName}" : $"ref string {tempName}";
    statement = $"DotNetByRefInterop.{helper}({target}, ({lambdaParameter}) => {emittedFunctionName}({string.Join(", ", callArgs)}));";
    return true;
}

private static bool TryResolveDotNetByRefMethodContractForEmittedCall(
    string emittedFunctionName,
    TaskSemantic task,
    int argumentCount,
    out DotNetByRefMethodContract contract)
{
    contract = default!;
    if (string.IsNullOrWhiteSpace(emittedFunctionName))
        return false;

    var dot = emittedFunctionName.LastIndexOf('.');
    if (dot <= 0 || dot + 1 >= emittedFunctionName.Length)
        return false;

    var ownerPath = emittedFunctionName[..dot].Trim();
    var methodName = emittedFunctionName[(dot + 1)..].Trim();
    if (string.IsNullOrWhiteSpace(ownerPath) || string.IsNullOrWhiteSpace(methodName))
        return false;

    if (!TryReadDotNetMemberEvidence(ownerPath, task, out var objectType))
    {
        var resource = ResolveResourceByTargetPath(task, ownerPath, _allTasks ?? Array.Empty<TaskSemantic>());
        if (resource is null || !IsDotNetTaskResource(resource))
            return false;
        objectType = NormalizeDotNetObjectType(resource.ObjectType ?? "");
    }

    return TryReadDotNetByRefMethodContract(objectType, methodName, argumentCount, out contract);
}
}

