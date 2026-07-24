using System;
using System.Collections.Generic;
using System.Globalization;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string StripRedundantOuterParentheses(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        while (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            var depth = 0;
            var wrapsAll = true;
            char quote = '\0';
            for (var i = 0; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (quote == '"' && i > 0 && trimmed[i - 1] == '\\')
                            continue;
                        quote = '\0';
                    }
                    continue;
                }

                if (ch is '"' or '\'')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '(')
                    depth++;
                else if (ch == ')')
                    depth--;

                if (depth == 0 && i < trimmed.Length - 1)
                {
                    wrapsAll = false;
                    break;
                }
            }

            if (!wrapsAll)
                break;

            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsTypeOfExpression(string expression)
    {
        var trimmed = expression.Trim();
        return trimmed.StartsWith("typeof(", StringComparison.Ordinal) &&
               trimmed.EndsWith(")", StringComparison.Ordinal);
    }

    private static bool IsTopLevelCall(string? functionName, string expected)
        => !string.IsNullOrWhiteSpace(functionName) &&
           (functionName.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            functionName.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase) ||
            expected.EndsWith("." + functionName, StringComparison.OrdinalIgnoreCase));

    private static string? TryGetTopLevelFunctionName(string expression)
        => TryParseFunctionCall(expression.Trim(), out var name, out _) ? name : null;

    private static bool IsWholeStringLiteralExpression(string value)
        => TryGetWholeCSharpStringLiteral(value?.Trim() ?? "", out _);

    private static bool IsNumericLiteralExpressionCentral(string value)
        => decimal.TryParse(
            value?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out _);

    private static bool TryGetLiteralNumberValue(string expression, out decimal value)
        => decimal.TryParse(
            expression?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static bool IsNullCallExpression(string expression)
    {
        var value = expression?.Trim() ?? "";
        return value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("u.Null()", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
                return false;
        }
        return true;
    }

    private static bool IsSimpleMemberPath(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        var parts = expression.Trim().Split('.');
        if (parts.Length == 0)
            return false;
        foreach (var part in parts)
        {
            if (!IsSimpleIdentifier(part))
                return false;
        }
        return true;
    }

    private static (string Left, string Operator, string Right)? SplitTopLevelBooleanBinaryExpression(string expression)
        => SplitTopLevelWordOperator(expression, ["OR", "AND", "||", "&&"]);

    private static (string Left, string Operator, string Right)? SplitMalformedTopLevelBooleanBinaryExpression(string expression)
        => SplitTopLevelBooleanBinaryExpression(expression);

    private static (string Left, string Operator, string Right)? SplitTopLevelComparisonExpression(string expression)
        => SplitTopLevelSymbolOperator(expression, ["==", "!=", "<=", ">=", "<>", "=", "<", ">"]);

    private static (string Left, string Operator, string Right)? SplitTopLevelAssignmentExpression(string expression)
        => SplitTopLevelSymbolOperator(expression, ["="]);

    private static (string Left, string Operator, string Right)? SplitTopLevelArithmeticExpression(string expression)
        => SplitTopLevelSymbolOperator(expression, ["+", "-", "*", "/", "%"]);

    private static (string Left, string Operator, string Right)? SplitTopLevelWordOperator(
        string expression,
        IReadOnlyList<string> operators)
        => SplitTopLevel(expression, operators, wordBoundaries: true);

    private static (string Left, string Operator, string Right)? SplitTopLevelSymbolOperator(
        string expression,
        IReadOnlyList<string> operators)
        => SplitTopLevel(expression, operators, wordBoundaries: false);

    private static (string Left, string Operator, string Right)? SplitTopLevel(
        string expression,
        IReadOnlyList<string> operators,
        bool wordBoundaries)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        var inString = false;
        var verbatim = false;
        var escaped = false;
        for (var i = expression.Length - 1; i >= 0; i--)
        {
            var ch = expression[i];
            if (inString)
            {
                if (verbatim)
                {
                    if (ch == '"' && (i == 0 || expression[i - 1] != '"'))
                    {
                        inString = false;
                        verbatim = false;
                    }
                    else if (ch == '"' && i > 0 && expression[i - 1] == '"')
                    {
                        i--;
                    }
                    continue;
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                verbatim = i > 0 && expression[i - 1] == '@';
                continue;
            }
            if (ch == ')') { depth++; continue; }
            if (ch == '(') { depth--; continue; }
            if (depth != 0)
                continue;

            foreach (var op in operators)
            {
                var start = i - op.Length + 1;
                if (start < 0 ||
                    !expression.AsSpan(start, op.Length).Equals(op, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (wordBoundaries &&
                    ((!IsBoundary(expression, start - 1)) ||
                     (!IsBoundary(expression, start + op.Length))))
                    continue;

                if (op == "=" &&
                    ((start > 0 && "=!<>".Contains(expression[start - 1])) ||
                     (start + 1 < expression.Length && expression[start + 1] == '=')))
                    continue;

                var left = expression[..start].Trim();
                var right = expression[(start + op.Length)..].Trim();
                if (left.Length == 0 || right.Length == 0)
                    continue;
                return (left, op, right);
            }
        }
        return null;
    }

    private static bool IsBoundary(string value, int index)
        => index < 0 ||
           index >= value.Length ||
           !(char.IsLetterOrDigit(value[index]) || value[index] == '_');

    private static bool CanSplitBooleanOperatorAfterLeftOperand(string expression, int operatorIndex)
        => operatorIndex > 0 && operatorIndex < expression.Length;
}
