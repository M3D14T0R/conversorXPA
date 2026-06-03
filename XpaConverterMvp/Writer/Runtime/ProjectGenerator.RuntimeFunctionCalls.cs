using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string NormalizeRuntimeFunctionCalls(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    if (SplitTopLevelBooleanBinaryExpression(StripRedundantOuterParentheses(expr.Trim())) is not null)
        expr = RewriteGetTextParamBooleanOperand(expr);
    expr = RewriteFunctionCalls(expr, "u.If", args =>
    {
        if (args.Count != 3)
            return null;

        var condition = RewriteGetTextParamBooleanOperand(args[0].Trim());
        if (string.Equals(condition, args[0].Trim(), StringComparison.Ordinal))
            return null;

        return $"u.If({condition}, {args[1]}, {args[2]})";
    });
    expr = RewriteFunctionCalls(expr, "u.Date", args => args.Count == 0 ? "XPARuntimeCore.Box.Date.Now" : null);
    expr = RewriteFunctionCalls(expr, "u.Time", args => args.Count == 0 ? "XPARuntimeCore.Box.Time.Now" : null);
    expr = RewriteFunctionCalls(expr, "Date", args => args.Count == 0 ? "XPARuntimeCore.Box.Date.Now" : null);
    expr = RewriteFunctionCalls(expr, "Time", args => args.Count == 0 ? "XPARuntimeCore.Box.Time.Now" : null);
    expr = RewriteFunctionCalls(expr, "u.VarSet", RewriteVarSetFunctionCall);
    expr = RewriteFunctionCalls(expr, "VarSet", RewriteVarSetFunctionCall);
    expr = RewriteFunctionCalls(expr, "u.User", RewriteUserFunctionCall);
    expr = RewriteFunctionCalls(expr, "User", RewriteUserFunctionCall);
    expr = RewriteFunctionCalls(expr, "u.Stat", RewriteStatFunctionCall);
    expr = RewriteFunctionCalls(expr, "u.IniPut", RewriteIniPutFunctionCall);
    expr = RewriteLateTextConsumerCalls(expr);
    return expr;
}

private static string RewriteLateTextConsumerCalls(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    foreach (var functionName in new[] { "u.Trim", "Trim", "u.LTrim", "LTrim", "u.RTrim", "RTrim", "u.StrToken", "StrToken", "u.Translate", "Translate", "u.FileInfo", "FileInfo" })
    {
        expr = RewriteFunctionCalls(expr, functionName, args =>
        {
            if (args.Count == 0)
                return null;

            var rewritten = args.ToList();
            foreach (var argIndex in GetTextArgumentIndexes(functionName, rewritten.Count))
                rewritten[argIndex] = NormalizeTextSinkArgumentCentral(rewritten[argIndex].Trim());

            return $"{functionName}({string.Join(", ", rewritten)})";
        });
    }

    return expr;
}

private static string RewriteGetTextParamBooleanOperand(string expression)
{
    if (string.IsNullOrWhiteSpace(expression))
        return expression;

    var originalTrimmed = expression.Trim();
    if (originalTrimmed.Length >= 2 && originalTrimmed[0] == '(' && originalTrimmed[^1] == ')')
    {
        var innerTrimmed = StripRedundantOuterParentheses(originalTrimmed);
        if (!string.Equals(innerTrimmed, originalTrimmed, StringComparison.Ordinal))
            return $"({RewriteGetTextParamBooleanOperand(innerTrimmed)})";
    }

    var trimmed = originalTrimmed;
    if (TryParseFunctionCall(trimmed, out var functionName, out var args))
    {
        if (IsTopLevelCall(functionName, "u.GetTextParam"))
        {
            return $"u.GetBoolParam({string.Join(", ", args)})";
        }

        if (IsTopLevelCall(functionName, "u.Not") && args.Count == 1)
        {
            return $"u.Not({RewriteGetTextParamBooleanOperand(args[0])})";
        }
    }

    var split = SplitTopLevelBooleanBinaryExpression(trimmed);
    if (split is not null)
    {
        return $"({RewriteGetTextParamBooleanOperand(split.Value.Left)} {split.Value.Operator} {RewriteGetTextParamBooleanOperand(split.Value.Right)})";
    }

    return expression;
}

private static string NormalizeVarSetValueExpressions(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteFunctionCalls(expr, "u.VarSet", RewriteVarSetFunctionCall);
    expr = RewriteFunctionCalls(expr, "VarSet", RewriteVarSetFunctionCall);
    return expr;
}

private static string? RewriteVarSetFunctionCall(List<string> args)
{
    if (args.Count < 2)
        return null;

    var rewrittenArgs = new List<string>(args.Count);
    for (var i = 0; i < args.Count; i++)
    {
        var arg = args[i].Trim();
        if (i == 1)
            arg = NormalizeNumericOperands(arg);
        rewrittenArgs.Add(arg);
    }

    return $"u.VarSet({string.Join(", ", rewrittenArgs)})";
}

private static string NormalizeRightsExpressions(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteFunctionCalls(expr, "Rights", args => $"u.Rights({string.Join(", ", args)})");
    expr = RewriteFunctionCalls(expr, "u.Rights", args =>
    {
        if (args.Count != 1)
            return null;

        var inner = args[0].Trim();
        if (TryParseFunctionCall(inner, out var innerFunction, out var innerArgs) &&
            string.Equals(innerFunction, "u.Rights", StringComparison.OrdinalIgnoreCase) &&
            innerArgs.Count == 1)
            return $"u.Rights({innerArgs[0].Trim()})";

        if (inner.EndsWith(".Allowed", StringComparison.Ordinal))
            return inner;

        return null;
    });

    return expr;
}

private static string? RewriteUserFunctionCall(List<string> args)
{
    if (args.Count != 1)
        return null;

    return args[0].Trim() switch
    {
        "0" => "ENV.Security.UserManager.CurrentUser.Name",
        "1" => "ENV.Security.UserManager.CurrentUser.Description",
        _ => null
    };
}

private static string? RewriteStatFunctionCall(List<string> args)
{
    if (args.Count != 2 || !string.Equals(args[0].Trim(), "0", StringComparison.Ordinal))
        return null;

    if (!TryGetMagicCharLiteral(args[1], out var mode))
        return null;

    return char.ToUpperInvariant(mode) switch
    {
        'M' => "Activity == Activities.Update",
        'E' => "Activity == Activities.Browse",
        'C' => "Activity == Activities.Insert",
        _ => null
    };
}

private static string? RewriteIniPutFunctionCall(List<string> args)
{
    if (args.Count == 0)
        return null;

    if (!TryGetWholeCSharpStringLiteral(args[0], out var literal))
        return null;

    var normalizedKey = NormalizeIniPutKeyLiteral(literal);
    var rewritten = args.ToArray();
    rewritten[0] = normalizedKey;
    return $"u.IniPut({string.Join(", ", rewritten)})";
}

private static string NormalizeIniPutKeyLiteral(string literalCode)
{
    if (!TryDecodeWholeCSharpStringLiteral(literalCode, out var value))
        return literalCode;

    value = value.Replace("==", "=").Replace(" = ", "=");
    return ToCSharpLiteral(value);
}

private static bool TryGetMagicCharLiteral(string expression, out char value)
{
    value = '\0';
    var trimmed = expression.Trim();
    if (trimmed.Length == 3 && trimmed[0] == '"' && trimmed[2] == '"')
    {
        value = trimmed[1];
        return true;
    }

    if (trimmed.Length == 3 && trimmed[0] == '\'' && trimmed[2] == '\'')
    {
        value = trimmed[1];
        return true;
    }

    return false;
}

private static bool TryDecodeWholeCSharpStringLiteral(string literalCode, out string value)
{
    value = "";
    if (!TryGetWholeCSharpStringLiteral(literalCode, out var normalizedLiteral))
        return false;

    var trimmed = normalizedLiteral.Trim();
    if (trimmed.StartsWith("@\"", StringComparison.Ordinal))
    {
        value = trimmed[2..^1].Replace("\"\"", "\"");
        return true;
    }

    if (!(trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal)))
        return false;

    var inner = trimmed[1..^1];
    value = Regex.Unescape(inner);
    return true;
}
}

