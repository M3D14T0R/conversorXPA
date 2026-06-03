using System;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string RewriteQualifiedConstructorInvocations(string expr, string constructorName, Func<List<string>, string?> rewrite)
{
    if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(constructorName))
        return expr;

    var index = expr.IndexOf(constructorName, StringComparison.Ordinal);
    while (index >= 0)
    {
        if (index > 0 && (char.IsLetterOrDigit(expr[index - 1]) || expr[index - 1] == '_' || expr[index - 1] == '.'))
        {
            index = expr.IndexOf(constructorName, index + constructorName.Length, StringComparison.Ordinal);
            continue;
        }

        var isAlreadyConstructed = index >= 4 &&
                                   string.Equals(expr.Substring(index - 4, 4), "new ", StringComparison.Ordinal);
        var cursor = index + constructorName.Length;
        while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
            cursor++;
        if (cursor >= expr.Length || expr[cursor] != '(')
        {
            index = expr.IndexOf(constructorName, cursor, StringComparison.Ordinal);
            continue;
        }

        var closeParen = FindMatchingParen(expr, cursor);
        if (closeParen < 0)
            break;

        if (isAlreadyConstructed)
        {
            index = expr.IndexOf(constructorName, closeParen + 1, StringComparison.Ordinal);
            continue;
        }

        var args = SplitTopLevelArguments(expr[(cursor + 1)..closeParen]);
        var replacement = rewrite(args);
        if (!string.IsNullOrWhiteSpace(replacement))
        {
            expr = expr[..index] + replacement + expr[(closeParen + 1)..];
            index = expr.IndexOf(constructorName, index + replacement.Length, StringComparison.Ordinal);
            continue;
        }

        index = expr.IndexOf(constructorName, closeParen + 1, StringComparison.Ordinal);
    }

    return expr;
}
}

