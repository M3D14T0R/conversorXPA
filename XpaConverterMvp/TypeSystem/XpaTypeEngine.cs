using System;

namespace XpaConverterMvp.TypeSystem;

internal enum XpaType
{
    Unknown,
    Number,
    Text,
    Date,
    Time,
    Bool,
    Blob,
    Array,
    Object
}

internal static class XpaTypeEngine
{
    public static string CoerceToContext(
        string expression,
        Func<string, XpaType> resolveType,
        XpaType expectedType)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        if (expectedType == XpaType.Unknown)
            return expression;

        var intrinsic = resolveType(expression);
        if (intrinsic == expectedType || intrinsic == XpaType.Unknown)
            return expression;

        if (!CanCoerce(intrinsic, expectedType))
            return expression;

        return Coerce(expression, intrinsic, expectedType);
    }

    public static string Coerce(string expr, XpaType from, XpaType to)
    {
        if (from == to)
            return expr;

        return (from, to) switch
        {
            (XpaType.Number, XpaType.Text) => $"u.CastToText({expr})",
            (XpaType.Bool, XpaType.Text) => $"u.CastToText({expr})",
            (XpaType.Date, XpaType.Text) => $"u.CastToText({expr})",
            (XpaType.Time, XpaType.Text) => $"u.CastToText({expr})",
            (XpaType.Blob, XpaType.Text) => $"u.ByteArrayToText({expr})",
            (XpaType.Object, XpaType.Text) => $"u.CastToText({expr})",

            (XpaType.Text, XpaType.Number) => $"u.CastToNumber({expr})",
            (XpaType.Blob, XpaType.Number) => $"u.CastToNumber(u.ByteArrayToText({expr}))",
            (XpaType.Bool, XpaType.Number) => $"u.CastToNumber({expr})",
            (XpaType.Date, XpaType.Number) => $"u.ToNumber({expr})",
            (XpaType.Time, XpaType.Number) => $"u.ToNumber({expr})",
            (XpaType.Object, XpaType.Number) => $"u.CastToNumber({expr})",

            (XpaType.Text, XpaType.Date) => $"u.CastToDate({expr})",
            (XpaType.Number, XpaType.Date) => $"u.CastToDate({expr})",
            (XpaType.Blob, XpaType.Date) => $"u.CastToDate(u.ByteArrayToText({expr}))",
            (XpaType.Object, XpaType.Date) => $"u.CastToDate({expr})",
            (XpaType.Number, XpaType.Time) => $"u.ToTime({expr})",
            (XpaType.Text, XpaType.Time) => $"u.CastToTime({expr})",
            (XpaType.Blob, XpaType.Time) => $"u.CastToTime(u.ByteArrayToText({expr}))",
            (XpaType.Object, XpaType.Time) => $"u.CastToTime({expr})",

            (XpaType.Text, XpaType.Bool) => $"u.CastToBool({expr})",
            (XpaType.Number, XpaType.Bool) => $"u.CastToBool({expr})",
            (XpaType.Blob, XpaType.Bool) => $"u.CastToBool(u.ByteArrayToText({expr}))",
            (XpaType.Object, XpaType.Bool) => $"u.CastToBool({expr})",

            (XpaType.Text, XpaType.Blob) => $"u.CastToByteArray({expr})",
            (XpaType.Object, XpaType.Blob) => $"u.CastToByteArray({expr})",

            _ => expr
        };
    }

    public static string NormalizeIf(
        string condition,
        string whenTrue,
        string whenFalse,
        Func<string, XpaType> resolveType)
    {
        var trueType = resolveType(whenTrue);
        var falseType = resolveType(whenFalse);
        var finalType = Unify(trueType, falseType);

        var trueCoerced = Coerce(whenTrue, trueType, finalType);
        var falseCoerced = Coerce(whenFalse, falseType, finalType);
        return $"u.If({condition}, {trueCoerced}, {falseCoerced})";
    }

    public static XpaType Unify(XpaType a, XpaType b)
    {
        if (a == b)
            return a;

        if (a == XpaType.Unknown)
            return b;
        if (b == XpaType.Unknown)
            return a;

        if (IsNumericLike(a) && IsNumericLike(b))
            return XpaType.Number;

        if (a == XpaType.Object)
            return b;
        if (b == XpaType.Object)
            return a;

        if (a == XpaType.Text || b == XpaType.Text)
            return XpaType.Text;

        return XpaType.Text;
    }

    public static bool IsScalar(XpaType xpaType)
        => xpaType is XpaType.Number or XpaType.Text or XpaType.Date or XpaType.Time or XpaType.Bool or XpaType.Blob;

    public static bool IsNumericLike(XpaType xpaType)
        => xpaType is XpaType.Number or XpaType.Bool;

    public static bool IsTemporal(XpaType xpaType)
        => xpaType is XpaType.Date or XpaType.Time;

    public static bool IsTextLike(XpaType xpaType)
        => xpaType is XpaType.Text or XpaType.Blob;

    public static bool CanCoerce(XpaType from, XpaType to)
    {
        if (from == XpaType.Unknown || to == XpaType.Unknown || from == to)
            return true;

        return (from, to) switch
        {
            (XpaType.Number, XpaType.Text) => true,
            (XpaType.Bool, XpaType.Text) => true,
            (XpaType.Date, XpaType.Text) => true,
            (XpaType.Time, XpaType.Text) => true,
            (XpaType.Blob, XpaType.Text) => true,
            (XpaType.Object, XpaType.Text) => true,

            (XpaType.Text, XpaType.Number) => true,
            (XpaType.Blob, XpaType.Number) => true,
            (XpaType.Bool, XpaType.Number) => true,
            (XpaType.Date, XpaType.Number) => true,
            (XpaType.Time, XpaType.Number) => true,
            (XpaType.Object, XpaType.Number) => true,

            (XpaType.Text, XpaType.Date) => true,
            (XpaType.Number, XpaType.Date) => true,
            (XpaType.Blob, XpaType.Date) => true,
            (XpaType.Object, XpaType.Date) => true,
            (XpaType.Number, XpaType.Time) => true,
            (XpaType.Text, XpaType.Time) => true,
            (XpaType.Blob, XpaType.Time) => true,
            (XpaType.Object, XpaType.Time) => true,

            (XpaType.Text, XpaType.Bool) => true,
            (XpaType.Number, XpaType.Bool) => true,
            (XpaType.Blob, XpaType.Bool) => true,
            (XpaType.Object, XpaType.Bool) => true,

            (XpaType.Text, XpaType.Blob) => true,
            (XpaType.Object, XpaType.Blob) => true,

            _ => false
        };
    }

    public static bool RequiresNumericProjection(XpaType from, XpaType to)
        => to == XpaType.Number && IsTemporal(from);

    public static XpaType MapExpectedToXpaType(string? returnType)
    {
        return returnType switch
        {
            "Number" => XpaType.Number,
            "Text" => XpaType.Text,
            "Date" => XpaType.Date,
            "Time" => XpaType.Time,
            "Bool" => XpaType.Bool,
            "byte[]" => XpaType.Blob,
            "Text[]" or "Number[]" or "Date[]" or "Time[]" or "Bool[]" or "byte[][]" => XpaType.Array,
            "object" => XpaType.Object,
            _ => XpaType.Unknown
        };
    }

    public static string GetFunctionToken(XpaType xpaType)
    {
        return xpaType switch
        {
            XpaType.Text => "Text",
            XpaType.Number => "Number",
            XpaType.Date => "Date",
            XpaType.Time => "Time",
            XpaType.Bool => "Bool",
            XpaType.Blob => "ByteArray",
            _ => ""
        };
    }

    public static string GetScalarCastFunctionName(XpaType xpaType)
    {
        var token = GetFunctionToken(xpaType);
        return string.IsNullOrWhiteSpace(token)
            ? ""
            : $"u.CastTo{token}";
    }

    public static string GetArrayConversionFunctionName(XpaType xpaType)
    {
        var token = GetFunctionToken(xpaType);
        return string.IsNullOrWhiteSpace(token) || xpaType == XpaType.Blob
            ? ""
            : $"u.CastTo{token}Array";
    }

    public static string GetSharedValueArrayFunctionName(XpaType xpaType)
    {
        var token = GetFunctionToken(xpaType);
        return xpaType is not (XpaType.Text or XpaType.Number) || string.IsNullOrWhiteSpace(token)
            ? ""
            : $"u.SharedValGet{token}Array";
    }
}
