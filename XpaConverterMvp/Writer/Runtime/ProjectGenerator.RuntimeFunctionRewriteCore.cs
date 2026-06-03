using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string RewriteFunctionCalls(string expr, string functionName, Func<List<string>, string?> rewrite)
{
    TrackLegacyExpressionTreatment("Rewrite", nameof(RewriteFunctionCalls));
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(functionName))
        return expr;

    var index = FindNextFunctionCallIndex(expr, functionName, 0);
    while (index >= 0)
    {
        var argsStart = index + functionName.Length;
        while (argsStart < expr.Length && char.IsWhiteSpace(expr[argsStart]))
            argsStart++;
        if (argsStart >= expr.Length || expr[argsStart] != '(')
        {
            index = FindNextFunctionCallIndex(expr, functionName, index + functionName.Length);
            continue;
        }
        argsStart++;
        var argsEnd = FindMatchingParen(expr, argsStart - 1);
        if (argsEnd < 0)
            break;

        var args = SplitTopLevelArguments(expr.Substring(argsStart, argsEnd - argsStart));
        var replacement = rewrite(args);
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            expr = expr[..index] + replacement + expr[(argsEnd + 1)..];
            index = FindNextFunctionCallIndex(expr, functionName, index + replacement.Length);
            continue;
        }

        index = FindNextFunctionCallIndex(expr, functionName, argsEnd + 1);
    }

    return expr;
}

private static string RewriteFunctionCallsInnermost(string expr, string functionName, Func<List<string>, string?> rewrite)
{
    TrackLegacyExpressionTreatment("Rewrite", nameof(RewriteFunctionCallsInnermost));
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(functionName))
        return expr;

    var index = FindLastFunctionCallIndex(expr, functionName, expr.Length - 1);
    while (index >= 0)
    {
        var argsStart = index + functionName.Length;
        while (argsStart < expr.Length && char.IsWhiteSpace(expr[argsStart]))
            argsStart++;
        if (argsStart >= expr.Length || expr[argsStart] != '(')
        {
            index = FindLastFunctionCallIndex(expr, functionName, index - 1);
            continue;
        }
        argsStart++;
        var argsEnd = FindMatchingParen(expr, argsStart - 1);
        if (argsEnd < 0)
        {
            index = FindLastFunctionCallIndex(expr, functionName, index - 1);
            continue;
        }

        var args = SplitTopLevelArguments(expr.Substring(argsStart, argsEnd - argsStart));
        var replacement = rewrite(args);
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            expr = expr[..index] + replacement + expr[(argsEnd + 1)..];
            index = FindLastFunctionCallIndex(expr, functionName, index - 1);
            continue;
        }

        index = FindLastFunctionCallIndex(expr, functionName, index - 1);
    }

    return expr;
}

private static int FindNextFunctionCallIndex(string expr, string functionName, int startIndex)
{
    if (string.IsNullOrEmpty(expr) || string.IsNullOrEmpty(functionName) || startIndex < 0)
        return -1;

    for (var i = startIndex; i <= expr.Length - functionName.Length; i++)
    {
        if (!expr.AsSpan(i, functionName.Length).SequenceEqual(functionName))
            continue;

        if (i > 0 && (char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_' || expr[i - 1] == '.'))
            continue;

        var next = i + functionName.Length;
        while (next < expr.Length && char.IsWhiteSpace(expr[next]))
            next++;

        if (next < expr.Length && expr[next] == '(')
            return i;
    }

    return -1;
}

private static int FindLastFunctionCallIndex(string expr, string functionName, int startIndex)
{
    if (string.IsNullOrEmpty(expr) || string.IsNullOrEmpty(functionName) || startIndex < 0)
        return -1;

    var lastStart = Math.Min(startIndex, expr.Length - functionName.Length);
    for (var i = lastStart; i >= 0; i--)
    {
        if (!expr.AsSpan(i, functionName.Length).SequenceEqual(functionName))
            continue;

        if (i > 0 && (char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_' || expr[i - 1] == '.'))
            continue;

        var next = i + functionName.Length;
        while (next < expr.Length && char.IsWhiteSpace(expr[next]))
            next++;

        if (next < expr.Length && expr[next] == '(')
            return i;
    }

    return -1;
}
}

