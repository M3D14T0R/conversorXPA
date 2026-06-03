using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? TryTranslateWholeExpressionSemantically(ExpressionEntrySemantic expr, TaskSemantic task)
    {
        if (expr.IsStringLiteral)
        {
            if (TryParseWholeXpaSingleQuotedLiteral(expr.Syntax, out var literalText))
                return ToCSharpLiteral(literalText);

            var trimmedLiteral = expr.Syntax.Trim();
            if (trimmedLiteral.Length >= 2 &&
                trimmedLiteral.StartsWith("\"", StringComparison.Ordinal) &&
                trimmedLiteral.EndsWith("\"", StringComparison.Ordinal))
                return ToCSharpLiteral(trimmedLiteral[1..^1]);
        }

        return expr.WholeExpressionSemanticKind switch
        {
            "EOP" => "u.EOP()",
            "IOCurr" => "u.IOCurr()",
            "Page" => ResolveWholeExpressionPage(task),
            "Line" => ResolveWholeExpressionLine(task),
            _ => null
        };
    }
}

