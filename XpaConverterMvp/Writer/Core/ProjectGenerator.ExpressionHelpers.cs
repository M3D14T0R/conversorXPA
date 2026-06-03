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

    var targetInfo = ResolveTargetValueInfo(task, target, target);
    var hasColumnEvidence =
        targetInfo.Resource is not null ||
        !string.IsNullOrWhiteSpace(targetInfo.AttrObj) ||
        !string.IsNullOrWhiteSpace(targetInfo.ModelAttrObj);
    if (!hasColumnEvidence || targetInfo.IsDotNet)
        return false;

    statement = $"{target}.Value = {assignment.Value.Right.Trim()};";
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
        }
        else if (string.Equals(targetInfo.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(targetInfo.ModelAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
        {
            preamble.Add($"var {tempName} = u.CastToText({emittedArg}).ToString();");
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
}

