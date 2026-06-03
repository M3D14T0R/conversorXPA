using System;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string NormalizeNotFileExistCalls(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    return RewriteMalformedNotFunctionCall(expr, "u.Not", "FileExist", "u.FileExist");
}

private static string RewriteMalformedNotFunctionCall(string expr, string outerFunction, string innerFunction, string qualifiedInnerFunction)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    var marker = $"{outerFunction}({innerFunction})";
    var index = expr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    while (index >= 0)
    {
        var afterMarker = index + marker.Length;
        while (afterMarker < expr.Length && char.IsWhiteSpace(expr[afterMarker]))
            afterMarker++;

        if (afterMarker >= expr.Length || expr[afterMarker] != '(')
        {
            index = expr.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase);
            continue;
        }

        var closeParen = FindMatchingParen(expr, afterMarker);
        if (closeParen < 0)
            break;

        var argsText = expr[(afterMarker + 1)..closeParen];
        var replacement = $"{outerFunction}({qualifiedInnerFunction}({argsText}))";
        expr = expr[..index] + replacement + expr[(closeParen + 1)..];
        index = expr.IndexOf(marker, index + replacement.Length, StringComparison.OrdinalIgnoreCase);
    }

    return expr;
}

private static string NormalizeMalformedNotFunctionCalls(string expr)
{
    const string marker = "u.Not(";
    var pos = 0;
    while (true)
    {
        var notStart = expr.IndexOf(marker, pos, StringComparison.Ordinal);
        if (notStart < 0)
            return expr;

        var nameStart = notStart + marker.Length;
        var nameEnd = expr.IndexOf(')', nameStart);
        if (nameEnd < 0)
            return expr;

        var candidate = expr.Substring(nameStart, nameEnd - nameStart).Trim();
        if (!Regex.IsMatch(candidate, @"^[A-Za-z_][A-Za-z0-9_\.]*$"))
        {
            pos = nameEnd + 1;
            continue;
        }

        var argsStart = nameEnd + 1;
        while (argsStart < expr.Length && char.IsWhiteSpace(expr[argsStart]))
            argsStart++;
        if (argsStart >= expr.Length || expr[argsStart] != '(')
        {
            pos = nameEnd + 1;
            continue;
        }

        var argsEnd = FindMatchingParen(expr, argsStart);
        if (argsEnd < 0)
            return expr;

        var argsText = expr.Substring(argsStart + 1, argsEnd - argsStart - 1);
        var replacement = $"u.Not({candidate}({argsText}))";
        expr = expr.Substring(0, notStart) + replacement + expr.Substring(argsEnd + 1);
        pos = notStart + replacement.Length;
    }
}
}

