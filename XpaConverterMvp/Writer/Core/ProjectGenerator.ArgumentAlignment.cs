using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static IReadOnlyList<string> AlignArgumentSequenceForParameters(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        var argCount = args.Count;
        var paramCount = parameters.Count;
        var costs = new int[argCount + 1, paramCount + 1];
        var actions = new byte[argCount + 1, paramCount + 1];
        const int inf = 1_000_000;

        for (var i = 0; i <= argCount; i++)
        for (var j = 0; j <= paramCount; j++)
            costs[i, j] = inf;

        costs[0, 0] = 0;

        for (var i = 0; i <= argCount; i++)
        {
            for (var j = 0; j <= paramCount; j++)
            {
                var current = costs[i, j];
                if (current >= inf)
                    continue;

                if (i < argCount && j < paramCount)
                {
                    var matchCost = current + GetArgumentMatchCost(args[i], parameters[j], currentTask);
                    if (matchCost < costs[i + 1, j + 1])
                    {
                        costs[i + 1, j + 1] = matchCost;
                        actions[i + 1, j + 1] = 1;
                    }
                }

                if (j < paramCount)
                {
                    var skipParamCost = current + GetSkippedParameterCost(parameters[j]);
                    if (skipParamCost < costs[i, j + 1])
                    {
                        costs[i, j + 1] = skipParamCost;
                        actions[i, j + 1] = 2;
                    }
                }

                if (i < argCount && ShouldDropOptionalArgument(args[i]))
                {
                    var skipArgCost = current + GetDroppedArgumentCost(args[i]);
                    if (skipArgCost < costs[i + 1, j])
                    {
                        costs[i + 1, j] = skipArgCost;
                        actions[i + 1, j] = 3;
                    }
                }
            }
        }

        var result = Enumerable.Repeat("null", paramCount).ToArray();
        var ai = argCount;
        var pj = paramCount;
        while (ai > 0 || pj > 0)
        {
            var action = actions[ai, pj];
            switch (action)
            {
                case 1:
                    result[pj - 1] = args[ai - 1];
                    ai--;
                    pj--;
                    break;
                case 2:
                    result[pj - 1] = "null";
                    pj--;
                    break;
                case 3:
                    ai--;
                    break;
                default:
                    if (pj > 0)
                    {
                        result[pj - 1] = ai > 0 ? args[ai - 1] : "null";
                        ai = Math.Max(0, ai - 1);
                        pj--;
                    }
                    else if (ai > 0)
                    {
                        ai--;
                    }
                    break;
            }
        }

        var lastNonNull = Array.FindLastIndex(result, value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase));
        return lastNonNull >= 0 ? result.Take(lastNonNull + 1).ToArray() : result;
    }

    private static bool HasPotentialOptionalTailAmbiguity(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        int optionalStartIndex,
        TaskSemantic currentTask)
    {
        for (var i = optionalStartIndex; i < args.Count && i < parameters.Count; i++)
        {
            var argument = args[i].Trim();
            if (string.Equals(argument, "null", StringComparison.OrdinalIgnoreCase))
                continue;

            var expected = ExpectedTypeForParameterType(parameters[i].ParameterType);
            if (!expected.HasExpectation)
                continue;

            var actual = ResolveExpectedTypeFromExpressionEvidence(currentTask, argument);
            if (actual.HasExpectation && !ExpectedTypesMatch(actual, expected))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> AlignOptionalArgumentSuffix(
        IReadOnlyList<string> args,
        IReadOnlyList<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)> parameters,
        TaskSemantic currentTask)
    {
        var argCount = args.Count;
        var paramCount = parameters.Count;
        var costs = new int[argCount + 1, paramCount + 1];
        var actions = new byte[argCount + 1, paramCount + 1];
        const int inf = 1_000_000;

        for (var i = 0; i <= argCount; i++)
        for (var j = 0; j <= paramCount; j++)
            costs[i, j] = inf;

        costs[0, 0] = 0;

        for (var i = 0; i <= argCount; i++)
        {
            for (var j = 0; j <= paramCount; j++)
            {
                var current = costs[i, j];
                if (current >= inf)
                    continue;

                if (i < argCount && j < paramCount)
                {
                    var matchCost = current + GetArgumentMatchCost(args[i], parameters[j], currentTask);
                    if (matchCost < costs[i + 1, j + 1])
                    {
                        costs[i + 1, j + 1] = matchCost;
                        actions[i + 1, j + 1] = 1;
                    }
                }

                if (j < paramCount)
                {
                    var skipParamCost = current + GetSkippedParameterCost(parameters[j]);
                    if (skipParamCost < costs[i, j + 1])
                    {
                        costs[i, j + 1] = skipParamCost;
                        actions[i, j + 1] = 2;
                    }
                }

                if (i < argCount && ShouldDropOptionalArgument(args[i]))
                {
                    var skipArgCost = current + GetDroppedArgumentCost(args[i]);
                    if (skipArgCost < costs[i + 1, j])
                    {
                        costs[i + 1, j] = skipArgCost;
                        actions[i + 1, j] = 3;
                    }
                }
            }
        }

        var result = Enumerable.Repeat("null", paramCount).ToArray();
        var ai = argCount;
        var pj = paramCount;
        while (ai > 0 || pj > 0)
        {
            var action = actions[ai, pj];
            switch (action)
            {
                case 1:
                    result[pj - 1] = args[ai - 1];
                    ai--;
                    pj--;
                    break;
                case 2:
                    result[pj - 1] = "null";
                    pj--;
                    break;
                case 3:
                    ai--;
                    break;
                default:
                    if (pj > 0)
                    {
                        result[pj - 1] = ai > 0 ? args[ai - 1] : "null";
                        ai = Math.Max(0, ai - 1);
                        pj--;
                    }
                    else if (ai > 0)
                    {
                        ai--;
                    }
                    break;
            }
        }

        var lastNonNull = Array.FindLastIndex(result, value => !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase));
        return lastNonNull >= 0 ? result.Take(lastNonNull + 1).ToArray() : result;
    }

    private static int GetArgumentMatchCost(
        string argument,
        (string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter,
        TaskSemantic currentTask)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return 1;

        var expected = ExpectedTypeForParameterType(parameter.ParameterType);
        if (!expected.HasExpectation)
            return 2;

        var actual = ResolveExpectedTypeFromExpressionEvidence(currentTask, trimmed);
        if (actual.HasExpectation && ExpectedTypesMatch(actual, expected))
            return 0;

        if (string.Equals(parameter.ColumnMember, "r_erro", StringComparison.OrdinalIgnoreCase))
            return 12;

        if (!IsInputParameterDirection(parameter.ParameterDirection))
            return 8;

        return 6;
    }

    private static int GetSkippedParameterCost((string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection) parameter)
    {
        if (string.Equals(parameter.ColumnMember, "r_erro", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (!IsInputParameterDirection(parameter.ParameterDirection))
            return 1;

        if (string.Equals(parameter.ParameterType, "ByteArrayParameter", StringComparison.Ordinal))
            return 1;

        return 3;
    }

    private static bool ShouldDropOptionalArgument(string argument)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.IndexOf(".r_erro", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private static int GetDroppedArgumentCost(string argument)
    {
        var trimmed = argument?.Trim() ?? "";
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (trimmed.IndexOf(".r_erro", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;

        return 10;
    }

    private static string ResolveObservedArgumentTypeToken(TaskSemantic callerTask, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        var trimmed = expression.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return "";

        var expected = ResolveExpectedTypeFromExpressionEvidence(callerTask, trimmed);
        var returnType = GetValueReturnType(expected.ReturnType);
        if (!string.IsNullOrWhiteSpace(returnType))
            return returnType switch
            {
                "Text" => "TextParameter",
                "Number" => "NumberParameter",
                "Date" => "DateParameter",
                "Time" => "TimeParameter",
                "Bool" => "BoolParameter",
                "byte[]" => "ByteArrayParameter",
                _ => returnType
            };

        return MapAttrObjToReturnType(expected.AttrObj) switch
        {
            "Text" => "TextParameter",
            "Number" => "NumberParameter",
            "Date" => "DateParameter",
            "Time" => "TimeParameter",
            "Bool" => "BoolParameter",
            "byte[]" => "ByteArrayParameter",
            _ => ""
        };
    }
}
