using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string GetExpectedAttrCastFunctionPrefix(string attrObj)
        => GetExpectedAttrCastFunctionName(attrObj) is string functionName && !string.IsNullOrWhiteSpace(functionName)
            ? functionName + "("
            : "";

    private static string GetExpectedAttrCastFunctionName(string attrObj)
        => GetScalarCastFunctionName(ResolveAttrObjXpaType(attrObj));

    private static XpaType ResolveAttrObjXpaType(string? attrObj)
        => XpaTypeEngine.MapExpectedToXpaType(MapAttrObjToReturnType(attrObj));

    private static XpaType ResolveArrayItemXpaType(string itemType)
        => XpaTypeEngine.MapExpectedToXpaType(itemType);

    private static string GetScalarCastFunctionName(XpaType xpaType)
        => XpaTypeEngine.GetScalarCastFunctionName(xpaType);

    private static bool IsExpectedAttrCastFunction(string functionName, string? attrObj)
    {
        var expectedFunction = GetExpectedAttrCastFunctionName(attrObj ?? "");
        return !string.IsNullOrWhiteSpace(expectedFunction) &&
               IsTopLevelCall(functionName, expectedFunction);
    }

    private static bool IsKnownScalarCastFunction(string functionName)
        => ResolveExplicitCastXpaType(functionName) != XpaType.Unknown;

    private static bool IsScalarCastFunctionName(string functionName, XpaType xpaType)
    {
        var castFunction = GetScalarCastFunctionName(xpaType);
        return !string.IsNullOrWhiteSpace(castFunction) &&
               IsTopLevelCall(functionName, castFunction);
    }

    private static bool IsAnyScalarCastFunctionName(string functionName)
    {
        return IsScalarCastFunctionName(functionName, XpaType.Blob) ||
               IsScalarCastFunctionName(functionName, XpaType.Text) ||
               IsScalarCastFunctionName(functionName, XpaType.Number) ||
               IsScalarCastFunctionName(functionName, XpaType.Date) ||
               IsScalarCastFunctionName(functionName, XpaType.Time) ||
               IsScalarCastFunctionName(functionName, XpaType.Bool);
    }

    private static XpaType ResolveExplicitCastXpaType(string functionName)
    {
        if (IsScalarCastFunctionName(functionName, XpaType.Blob))
            return XpaType.Blob;
        if (IsScalarCastFunctionName(functionName, XpaType.Text))
            return XpaType.Text;
        if (IsScalarCastFunctionName(functionName, XpaType.Number) || IsNumericProjectionFunctionName(functionName))
            return XpaType.Number;
        if (IsScalarCastFunctionName(functionName, XpaType.Date) || IsTopLevelCall(functionName, "u.ToDate"))
            return XpaType.Date;
        if (IsScalarCastFunctionName(functionName, XpaType.Time) || IsTopLevelCall(functionName, "u.ToTime") || IsTopLevelCall(functionName, "UserMethods.ToTime"))
            return XpaType.Time;
        if (IsScalarCastFunctionName(functionName, XpaType.Bool))
            return XpaType.Bool;

        return XpaType.Unknown;
    }
}
