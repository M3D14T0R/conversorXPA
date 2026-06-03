using System;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string RewriteBareFunctionInvocations(string expr, Func<string, string?> rewriteFunctionName)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        var result = new StringBuilder(expr.Length + 32);
        for (var i = 0; i < expr.Length;)
        {
            if (IsQuotedSegmentStart(expr, i))
            {
                var quoteStart = i;
                if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                    break;
                result.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
                i = quoteEnd + 1;
                continue;
            }

            if (!IsBareFunctionInvocationStart(expr, i, out var functionNameEnd))
            {
                result.Append(expr[i]);
                i++;
                continue;
            }

            var functionName = expr[i..functionNameEnd];
            var cursor = functionNameEnd;
            while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
                cursor++;

            if (cursor >= expr.Length || expr[cursor] != '(')
            {
                result.Append(expr[i]);
                i++;
                continue;
            }

            var rewrittenName = rewriteFunctionName(functionName);
            result.Append(string.IsNullOrWhiteSpace(rewrittenName) ? functionName : rewrittenName);
            result.Append(expr, functionNameEnd, cursor - functionNameEnd);
            result.Append('(');
            i = cursor + 1;
        }

        return result.ToString();
    }

    private static bool IsBareFunctionInvocationStart(string expr, int startIndex, out int endIndex)
    {
        endIndex = startIndex;
        if (startIndex >= expr.Length || !(char.IsLetter(expr[startIndex]) || expr[startIndex] == '_'))
            return false;

        if (startIndex > 0)
        {
            var prev = expr[startIndex - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_' || prev == '.')
                return false;
        }

        var i = startIndex + 1;
        while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_'))
            i++;

        endIndex = i;
        return true;
    }
}

