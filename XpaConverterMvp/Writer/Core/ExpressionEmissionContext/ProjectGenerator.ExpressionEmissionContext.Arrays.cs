using System.Collections.Generic;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string TryRewriteSharedValueArrayGetter(string functionName, IReadOnlyList<string> args, string itemType)
    {
        if (!IsTopLevelCall(functionName, "u.SharedValGet"))
            return "";

        var renderedArgs = string.Join(", ", args);
        var sharedValueArrayFunction = GetSharedValueArrayFunctionName(itemType);
        if (!string.IsNullOrWhiteSpace(sharedValueArrayFunction))
            return $"{sharedValueArrayFunction}({renderedArgs})";

        return BuildArrayConversionExpression($"u.SharedValGet({renderedArgs})", itemType);
    }

    private static string TryRewriteParameterArrayGetter(string functionName, IReadOnlyList<string> args, string itemType)
    {
        if (!IsTopLevelCall(functionName, "u.GetTextParam") &&
            !IsTopLevelCall(functionName, "u.GetParam"))
            return "";

        var renderedArgs = string.Join(", ", args);
        var parameterExpression = $"u.GetParam({renderedArgs})";
        return BuildArrayConversionExpression(parameterExpression, itemType);
    }

    private static string BuildArrayConversionExpression(string expression, string itemType)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(itemType))
            return "";

        var conversionFunction = GetArrayConversionFunctionName(ResolveArrayItemXpaType(itemType));
        return string.IsNullOrWhiteSpace(conversionFunction)
            ? ""
            : $"{conversionFunction}({expression})";
    }

    private static string GetArrayConversionFunctionName(string itemType)
        => GetArrayConversionFunctionName(ResolveArrayItemXpaType(itemType));

    private static string GetArrayConversionFunctionName(XpaType xpaType)
        => XpaTypeEngine.GetArrayConversionFunctionName(xpaType);

    private static string GetSharedValueArrayFunctionName(string itemType)
        => GetSharedValueArrayFunctionName(ResolveArrayItemXpaType(itemType));

    private static string GetSharedValueArrayFunctionName(XpaType xpaType)
        => XpaTypeEngine.GetSharedValueArrayFunctionName(xpaType);
}
