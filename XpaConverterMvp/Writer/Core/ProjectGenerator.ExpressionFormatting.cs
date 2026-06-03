using System;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static bool ContainsLogicalConjunctionOutsideQuotes(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return false;

    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (ch == '\'' || ch == '"')
        {
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                return false;
            i = quoteEnd - 1;
            continue;
        }

        if (i + 1 < expr.Length && ((expr[i] == '&' && expr[i + 1] == '&') || (expr[i] == '|' && expr[i + 1] == '|')))
            return true;
    }

    return false;
}

private static string NormalizeOperatorSpacingOutsideQuotes(string expr)
{
    var result = new StringBuilder(expr.Length + 16);
    for (var i = 0; i < expr.Length;)
    {
        var ch = expr[i];
        if (IsQuotedSegmentStart(expr, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(expr, i, out var quoteEnd))
                break;
            result.Append(expr, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        if (TryReadSpacingOperator(expr, i, out var op, out var opLength))
        {
            TrimTrailingSpaces(result);
            if (result.Length > 0 && !char.IsWhiteSpace(result[^1]))
                result.Append(' ');
            result.Append(op);
            var next = i + opLength;
            while (next < expr.Length && char.IsWhiteSpace(expr[next]))
                next++;
            if (next < expr.Length && expr[next] != ')' && expr[next] != ',' && expr[next] != ';')
                result.Append(' ');
            i = next;
            continue;
        }

        result.Append(ch);
        i++;
    }

    return result.ToString();
}

private static bool TryReadSpacingOperator(string expr, int index, out string op, out int length)
{
    op = "";
    length = 0;
    if (index < 0 || index >= expr.Length)
        return false;

    if (index + 1 < expr.Length)
    {
        var two = expr.Substring(index, 2);
        if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||")
        {
            op = two;
            length = 2;
            return true;
        }
    }

    var ch = expr[index];
    if (ch == '<' || ch == '>')
    {
        var next = index + 1 < expr.Length ? expr[index + 1] : '\0';
        if (next != '=' && next != ch)
        {
            op = ch.ToString();
            length = 1;
            return true;
        }
    }

    return false;
}

private static void TrimTrailingSpaces(StringBuilder sb)
{
    while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
        sb.Length--;
}

private static string ReplaceWholeWordOutsideQuotes(string input, string token, string replacement)
{
    if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(token))
        return input;

    var rx = new Regex($@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase);
    static bool IsRuntimeHelperU(Match m, string chunk, string tokenValue)
    {
        if (!string.Equals(tokenValue, "U", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(m.Value, "u", StringComparison.Ordinal))
            return false;
        var nextIndex = m.Index + m.Length;
        return nextIndex < chunk.Length && chunk[nextIndex] == '.';
    }
    var sb = new StringBuilder(input.Length + 32);
    var i = 0;
    while (i < input.Length)
    {
        if (IsQuotedSegmentStart(input, i))
        {
            var quoteStart = i;
            if (!TryReadQuotedSegmentEnd(input, i, out var quoteEnd))
                break;
            sb.Append(input, quoteStart, (quoteEnd - quoteStart) + 1);
            i = quoteEnd + 1;
            continue;
        }

        var chunkStart = i;
        while (i < input.Length && !IsQuotedSegmentStart(input, i))
            i++;
        var chunk = input.Substring(chunkStart, i - chunkStart);
        sb.Append(rx.Replace(chunk, m => IsRuntimeHelperU(m, chunk, token) ? m.Value : replacement));
    }

    return sb.ToString();
}
}

