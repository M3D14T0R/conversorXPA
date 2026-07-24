using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? TryResolveVarDbNameExpression(string syntax, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return null;

        var m = Regex.Match(
            syntax.Trim(),
            @"^VarDbName\s*\(\s*'(?<var>[A-Za-z])'\s*VAR\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;

        var binding = ResolveExpressionOrdinalBinding(m.Groups["var"].Value, task, _allTasks ?? Array.Empty<TaskSemantic>(), dataObjects);
        if (string.IsNullOrWhiteSpace(binding))
            return null;

        return $"{binding}.FullDbName";
    }

    private static bool TryResolveExpressionAsStringLiteralCode(TaskSemantic task, int expressionId, IReadOnlyList<DataObjectDef> dataObjects, out string literalCode)
    {
        literalCode = "";
        if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out var expr))
            return false;
        if (expr is null || string.IsNullOrWhiteSpace(expr.Syntax))
            return false;

        if (expr.IsStringLiteral)
        {
            literalCode = ResolveStringLiteralExpressionCode(expr).Trim();
            return !string.IsNullOrWhiteSpace(literalCode);
        }

        var translated = TranslateXpaExpressionToCSharp(expr.LiteralSourceSyntax, task, dataObjects).Trim();
        if (!TryGetWholeCSharpStringLiteral(translated, out var resolvedLiteralCode))
            return false;

        literalCode = resolvedLiteralCode;
        return true;
    }

    private static bool TryGetWholeCSharpStringLiteral(string code, out string literalCode)
    {
        literalCode = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = code.Trim();
        if (trimmed.Length < 2)
            return false;

        var verbatim = false;
        var index = 0;
        if (trimmed.StartsWith("@\"", StringComparison.Ordinal))
        {
            verbatim = true;
            index = 2;
        }
        else if (trimmed[0] == '"')
        {
            index = 1;
        }
        else
        {
            return false;
        }

        while (index < trimmed.Length)
        {
            if (verbatim)
            {
                if (trimmed[index] == '"')
                {
                    if (index + 1 < trimmed.Length && trimmed[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (index != trimmed.Length - 1)
                        return false;

                    literalCode = trimmed;
                    return true;
                }

                index++;
                continue;
            }

            if (trimmed[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (trimmed[index] == '"')
            {
                if (index != trimmed.Length - 1)
                    return false;

                literalCode = trimmed;
                return true;
            }

            index++;
        }

        return false;
    }

    private static string ResolveStringLiteralExpressionCode(ExpressionEntrySemantic expr)
    {
        if (TryParseWholeXpaSingleQuotedLiteral(expr.Syntax, out var literalText))
            return ToCSharpLiteral(literalText);

        var trimmedLiteral = expr.Syntax.Trim();
        if (trimmedLiteral.Length >= 2 &&
            trimmedLiteral.StartsWith("\"", StringComparison.Ordinal) &&
            trimmedLiteral.EndsWith("\"", StringComparison.Ordinal))
            return ToCSharpLiteral(trimmedLiteral[1..^1]);

        return ToCSharpLiteral(expr.LiteralSourceSyntax);
    }
}

