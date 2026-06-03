using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool ShouldReanchorExplicitTypedCast(string expression, string? expectedAttrObj)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(expectedAttrObj))
            return false;

        if (!TryParseFunctionCall(expression.Trim(), out var functionName, out var args) || args.Count != 1)
            return false;

        var upperExpected = NormalizeAttrObjKind(expectedAttrObj).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(upperExpected))
            return false;

        var expectedCastFunction = GetExpectedAttrCastFunctionName(upperExpected);
        if (string.IsNullOrWhiteSpace(expectedCastFunction))
            return false;

        if (IsExpectedAttrCastFunction(functionName, upperExpected))
            return false;

        var explicitCastType = ResolveExplicitCastXpaType(functionName);
        return explicitCastType != XpaType.Unknown &&
               explicitCastType != ResolveAttrObjXpaType(upperExpected);
    }
}
