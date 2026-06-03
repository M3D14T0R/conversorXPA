using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string NormalizeMessageFunctionCalls(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteFunctionCalls(expr, "Message.ShowWarning", args =>
        $"ENV.Message.ShowWarning({string.Join(", ", args)})");
    expr = RewriteFunctionCalls(expr, "Message.ShowError", args =>
        $"ENV.Message.ShowError({string.Join(", ", args)})");

    return expr;
}

private static string NormalizeDatabaseMetadataFunctions(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteFunctionCalls(expr, "u.DBSize", args =>
    {
        if (args.Count == 2 && IsTypeOfExpression(args[0]) && IsEmptyTextLiteral(args[1]))
            return $"Application.Instance.AllEntities.IndexOf({args[0].Trim()})";
        return null;
    });

    expr = RewriteFunctionCalls(expr, "u.DbSize", args =>
    {
        if (args.Count == 2 && IsTypeOfExpression(args[0]) && IsEmptyTextLiteral(args[1]))
            return $"Application.Instance.AllEntities.IndexOf({args[0].Trim()})";
        return null;
    });

    expr = RewriteFunctionCalls(expr, "u.DbCache", args =>
    {
        if (args.Count == 2 && IsTypeOfExpression(args[0]))
            return $"u.DbCache(Application.Instance.AllEntities.IndexOf({args[0].Trim()}), {args[1].Trim()})";
        return null;
    });

    return expr;
}

private static string NormalizeJsonFunctionSourceArguments(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    foreach (var functionName in new[] { "JSONInsert", "JSONModify", "JSONDelete", "JSONFind", "JSONExist", "JSONGet", "JSONCnt" })
    {
        expr = RewriteFunctionCalls(expr, functionName, args =>
        {
            if (args.Count == 0 || !TryUnwrapIndexOfSource(args[0], out var sourceArg))
                return null;

            var rewrittenArgs = args.ToArray();
            rewrittenArgs[0] = sourceArg;
            return $"{functionName}({string.Join(", ", rewrittenArgs)})";
        });
    }

    return expr;
}

private static bool IsTypeOfExpression(string expression)
{
    var trimmed = expression.Trim();
    return trimmed.StartsWith("typeof(", StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal);
}

private static bool IsEmptyTextLiteral(string expression)
{
    var trimmed = expression.Trim();
    return string.Equals(trimmed, "\"\"", StringComparison.Ordinal) ||
           string.Equals(trimmed, "@\"\"", StringComparison.Ordinal);
}

private static bool TryUnwrapIndexOfSource(string expression, out string sourceArg)
{
    sourceArg = "";
    if (!TryParseFunctionCall(expression, out var functionName, out var args) ||
        !string.Equals(functionName, "u.IndexOf", StringComparison.Ordinal) ||
        args.Count != 1)
        return false;

    var candidate = args[0].Trim();
    if (!Regex.IsMatch(candidate, @"^(?:_parent\.)?[A-Za-z_]\w*$"))
        return false;

    sourceArg = candidate;
    return true;
}
}

