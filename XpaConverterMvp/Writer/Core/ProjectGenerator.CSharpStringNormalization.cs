using System;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string NormalizeCSharpStringLiterals(string expr)
{
    if (string.IsNullOrEmpty(expr))
        return expr;

    var sb = new StringBuilder(expr.Length + 32);
    for (var i = 0; i < expr.Length; i++)
    {
        var ch = expr[i];
        if (ch != '"')
        {
            sb.Append(ch);
            continue;
        }

        if (i > 0 && expr[i - 1] == '@')
        {
            sb.Append(ch);
            i++;
            while (i < expr.Length)
            {
                sb.Append(expr[i]);
                if (expr[i] == '"')
                {
                    if (i + 1 < expr.Length && expr[i + 1] == '"')
                    {
                        sb.Append(expr[i + 1]);
                        i += 2;
                        continue;
                    }
                    break;
                }
                i++;
            }
            continue;
        }

        var content = new StringBuilder();
        i++;
        while (i < expr.Length)
        {
            var c = expr[i];
            if (c == '\\' && i + 1 < expr.Length)
            {
                content.Append(c);
                content.Append(expr[i + 1]);
                i += 2;
                continue;
            }
            if (c == '"' && i + 1 < expr.Length && expr[i + 1] == '"')
            {
                content.Append('\'');
                i += 2;
                continue;
            }
            if (c == '"')
                break;
            content.Append(c);
            i++;
        }

        var decodedContent = DecodeRegularCSharpStringContent(content.ToString());
        var normalizedContent = NormalizeLiteralContent(decodedContent);
        var needsVerbatim = normalizedContent.Contains('\n') || normalizedContent.Contains('\r') || normalizedContent.Contains('\\');
        if (needsVerbatim)
            sb.Append("@\"").Append(normalizedContent.Replace("\"", "\"\"")).Append('"');
        else
            sb.Append('"').Append(normalizedContent.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
    }

    return sb.ToString();
}

private static string NormalizeLiteralContent(string content)
{
    if (string.IsNullOrEmpty(content))
        return content;

    var unified = content.Replace("\r\n", "\n").Replace('\r', '\n');
    if (!unified.Contains('\n'))
        return unified;

    var lines = unified.Split('\n');
    if (lines.Length > 1 && lines[1..].All(string.IsNullOrWhiteSpace))
    {
        var hadWhitespaceTail = lines[1..].Any(l => l.Length > 0);
        return hadWhitespaceTail ? lines[0] + " " : lines[0];
    }

    for (var i = 1; i < lines.Length; i++)
        lines[i] = lines[i].TrimStart(' ', '\t');
    return string.Join("\n", lines);
}

private static string DecodeRegularCSharpStringContent(string content)
{
    if (string.IsNullOrEmpty(content))
        return content;

    try
    {
        return Regex.Unescape(content);
    }
    catch
    {
        return content;
    }
}
}

