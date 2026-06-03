using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? TryTranslateUnsupportedEnvironmentExpression(ExpressionEntrySemantic expr)
    {
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return null;

        var syntax = expr.Syntax.Trim();
        if (Regex.IsMatch(syntax, @"^SharedValGetNames\s*\(\s*\)\s*$", RegexOptions.IgnoreCase))
            return "(Text[])null /* mismatch original Expression: Error in expression SharedValGetNames() */";

        if (Regex.IsMatch(syntax, @"^SharedValGetAttr\s*\(", RegexOptions.IgnoreCase))
            return "\"\"";

        return null;
    }

    private static string NormalizeDateConstructorMappings(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        code = RewriteConstructorInvocations(code, "u.Date", args =>
        {
            if (args.Count == 3 &&
                IsZeroLikeLiteral(args[0]) &&
                IsZeroLikeLiteral(args[1]) &&
                IsZeroLikeLiteral(args[2]))
                return "XPARuntimeCore.Box.Date.Empty";

            return $"new Date({string.Join(", ", args)})";
        });

        code = RewriteWholeMemberReference(code, "Date.Now", "XPARuntimeCore.Box.Date.Now");
        code = RewriteWholeMemberReference(code, "Time.Now", "XPARuntimeCore.Box.Time.Now");
        return code;
    }

    private static string RewriteConstructorInvocations(string expr, string constructorName, Func<List<string>, string?> rewrite)
    {
        if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(constructorName))
            return expr;

        var marker = $"new {constructorName}";
        var index = expr.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index > 0 && (char.IsLetterOrDigit(expr[index - 1]) || expr[index - 1] == '_' || expr[index - 1] == '.'))
            {
                index = expr.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
                continue;
            }

            var cursor = index + marker.Length;
            while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
                cursor++;
            if (cursor >= expr.Length || expr[cursor] != '(')
            {
                index = expr.IndexOf(marker, cursor, StringComparison.Ordinal);
                continue;
            }

            var closeParen = FindMatchingParen(expr, cursor);
            if (closeParen < 0)
                break;

            var args = SplitTopLevelArguments(expr[(cursor + 1)..closeParen]);
            var replacement = rewrite(args);
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                expr = expr[..index] + replacement + expr[(closeParen + 1)..];
                index = expr.IndexOf(marker, index + replacement.Length, StringComparison.Ordinal);
                continue;
            }

            index = expr.IndexOf(marker, closeParen + 1, StringComparison.Ordinal);
        }

        return expr;
    }
}

