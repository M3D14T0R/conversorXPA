using System;
using System.Collections.Generic;
using System.Linq;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool IsCounterExpression(string expression)
        => expression.Equals("Counter", StringComparison.OrdinalIgnoreCase) ||
           expression.Equals("u.LoopCounter()", StringComparison.OrdinalIgnoreCase);

    private static bool IsScalarCastFunctionName(string functionName, XpaType type)
    {
        var expected = XpaExpressionTypeMap.ScalarConversionFunction(type);
        return !string.IsNullOrWhiteSpace(expected) &&
               IsTopLevelCall(functionName, expected);
    }

    private static bool IsVarCurrentLikeFunctionName(string? functionName)
        => IsTopLevelCall(functionName, "u.VarCurr") ||
           IsTopLevelCall(functionName, "u.VarPrev") ||
           IsTopLevelCall(functionName, "u.VarCurrent");

    private static bool IsKnownTextProducingFunction(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;
        return TryResolveKnownXpaFunctionReturnType(
                   functionName,
                   Array.Empty<string>(),
                   out var returnType) &&
               GetValueReturnType(returnType) == "Text";
    }

    private static bool IsNumericProjectionFunctionName(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return false;
        return TryResolveKnownXpaFunctionReturnType(
                   functionName,
                   Array.Empty<string>(),
                   out var returnType) &&
               GetValueReturnType(returnType) == "Number";
    }

    private static bool IsObjectProducingExpression(string? functionName)
        => IsTopLevelCall(functionName, "u.DataViewToDNDataTable") ||
           IsTopLevelCall(functionName, "u.JGet") ||
           IsTopLevelCall(functionName, "u.JCall") ||
           IsTopLevelCall(functionName, "u.DotNet");

    private static bool ExpectedTypesMatch(ExpectedTypeContext left, ExpectedTypeContext right)
        => ResolveExpectedXpaType(left) == ResolveExpectedXpaType(right) &&
           string.Equals(
               XpaExpressionTypeMap.CanonicalTypeName(ResolveReturnTypeForExpectedContext(left)),
               XpaExpressionTypeMap.CanonicalTypeName(ResolveReturnTypeForExpectedContext(right)),
               StringComparison.Ordinal);

    private static string EmitExpressionForExpectedScalarType(
        string expression,
        string expectedReturnType,
        TaskSemantic? task = null)
    {
        if (task is null)
            return expression;
        return EmitExpressionForExpectedType(
            expression,
            task,
            ExpectedTypeForReturnType(expectedReturnType));
    }

    private static string EmitKnownFunctionScalarArgument(
        string expression,
        string? valueReturnType,
        TaskSemantic? task)
        => EmitExpressionForExpectedScalarType(expression, valueReturnType ?? "", task);

    private static string EmitKnownFunctionArgument(
        string expression,
        string expectedReturnType,
        TaskSemantic task)
        => EmitExpressionForExpectedScalarType(expression, expectedReturnType, task);

    private static string ApplyExpectedTypeContext(
        string expression,
        TaskSemantic task,
        ExpectedTypeContext expected)
        => EmitExpressionForExpectedType(expression, task, expected);

    private static string ResolveExpressionReturnType(
        string? attr,
        string? code = null,
        TaskSemantic? task = null)
    {
        if (!string.IsNullOrWhiteSpace(code) &&
            TryResolveKnownExpressionReturnTypeWithoutLegacy(task, code, out var returnType))
            return returnType;

        return MapAttrObjToReturnType(attr);
    }

    private static string ResolveScalarReturnTypeForContext(ExpressionEmissionContext context)
        => GetValueReturnType(ResolveReturnTypeForExpectedContext(context.Expected));

    private static string ResolveScalarReturnTypeForContext(
        ExpressionEmissionContext context,
        string? attrObj)
    {
        var resolved = ResolveScalarReturnTypeForContext(context);
        return string.IsNullOrWhiteSpace(resolved)
            ? MapAttrObjToReturnType(attrObj)
            : resolved;
    }

    private static string BuildTypedGapFallbackExpression(string? attrObj)
        => XpaExpressionTypeMap.FromReturnType(MapAttrObjToReturnType(attrObj)) switch
        {
            XpaType.Number => "0",
            XpaType.Bool => "false",
            XpaType.Date => "XPARuntimeCore.Box.Date.Empty",
            XpaType.Time => "XPARuntimeCore.Box.Time.Empty",
            XpaType.Blob => "Array.Empty<byte>()",
            _ => "\"\""
        };

    private static bool TryGetAccessibleFunctionArgumentType(
        TaskSemantic task,
        string functionName,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        if (!TryGetAccessibleFunctionContract(task, functionName, out var contract, out _) ||
            argumentIndex < 0 ||
            argumentIndex >= contract.Parameters.Count)
            return false;

        returnType = contract.Parameters[argumentIndex].ParameterType;
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static bool TryGetComponentFunctionArgumentType(
        string functionName,
        int argumentIndex,
        out string returnType)
    {
        returnType = "";
        if (!TryGetComponentFunctionCallContract(functionName, out var contract) ||
            argumentIndex < 0 ||
            argumentIndex >= contract.ParameterTypes.Count)
            return false;

        returnType = contract.ParameterTypes[argumentIndex];
        return !string.IsNullOrWhiteSpace(returnType);
    }
}
