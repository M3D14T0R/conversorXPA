using System;
using System.Collections.Generic;
using System.Linq;

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

internal readonly record struct XpaTypedExpression(
    string Code,
    string ReturnType,
    XpaType Type,
    string? LiteralValue = null,
    string? BindingCode = null);

internal readonly record struct XpaExpressionDestination(
    string ReturnType,
    XpaType Type);

/// <summary>
/// Único ponto autorizado a converter uma expressão tipada para seu destino.
/// A decisão usa exclusivamente o tipo produzido e o tipo esperado; o código
/// C# emitido nunca é analisado ou reescrito para inferir coerção.
/// </summary>
internal static class XpaExpressionTypeMap
{
    private static readonly IReadOnlyDictionary<(XpaType Source, XpaType Destination), Func<string, string>>
        ScalarConversions =
            new Dictionary<(XpaType, XpaType), Func<string, string>>
            {
                [(XpaType.Number, XpaType.Text)] = static code => $"u.CastToText({code})",
                [(XpaType.Bool, XpaType.Text)] = static code => $"u.CastToText({code})",
                [(XpaType.Date, XpaType.Text)] = static code => $"u.CastToText({code})",
                [(XpaType.Time, XpaType.Text)] = static code => $"u.CastToText({code})",
                [(XpaType.Blob, XpaType.Text)] = static code => $"u.ByteArrayToText({code})",
                [(XpaType.Array, XpaType.Text)] = static code => $"u.CastToText({code})",
                [(XpaType.Object, XpaType.Text)] = static code => $"u.CastToText({code})",

                [(XpaType.Text, XpaType.Number)] = static code => $"u.CastToNumber({code})",
                [(XpaType.Blob, XpaType.Number)] = static code => $"u.CastToNumber(u.ByteArrayToText({code}))",
                [(XpaType.Bool, XpaType.Number)] = static code => $"u.CastToNumber({code})",
                [(XpaType.Date, XpaType.Number)] = static code => $"u.ToNumber({code})",
                [(XpaType.Time, XpaType.Number)] = static code => $"u.ToNumber({code})",
                [(XpaType.Array, XpaType.Number)] = static code => $"u.CastToNumber({code})",
                [(XpaType.Object, XpaType.Number)] = static code => $"u.CastToNumber({code})",

                [(XpaType.Text, XpaType.Date)] = static code => $"u.CastToDate({code})",
                [(XpaType.Number, XpaType.Date)] = static code => $"u.CastToDate({code})",
                [(XpaType.Time, XpaType.Date)] = static code => $"u.CastToDate({code})",
                [(XpaType.Bool, XpaType.Date)] = static code => $"u.CastToDate({code})",
                [(XpaType.Blob, XpaType.Date)] = static code => $"u.CastToDate(u.ByteArrayToText({code}))",
                [(XpaType.Array, XpaType.Date)] = static code => $"u.CastToDate({code})",
                [(XpaType.Object, XpaType.Date)] = static code => $"u.CastToDate({code})",

                [(XpaType.Number, XpaType.Time)] = static code => $"u.ToTime({code})",
                [(XpaType.Text, XpaType.Time)] = static code => $"u.CastToTime({code})",
                [(XpaType.Date, XpaType.Time)] = static code => $"u.CastToTime({code})",
                [(XpaType.Bool, XpaType.Time)] = static code => $"u.CastToTime({code})",
                [(XpaType.Blob, XpaType.Time)] = static code => $"u.CastToTime(u.ByteArrayToText({code}))",
                [(XpaType.Array, XpaType.Time)] = static code => $"u.CastToTime({code})",
                [(XpaType.Object, XpaType.Time)] = static code => $"u.CastToTime({code})",

                [(XpaType.Text, XpaType.Bool)] = static code => $"u.CastToBool({code})",
                [(XpaType.Number, XpaType.Bool)] = static code => $"u.CastToBool({code})",
                [(XpaType.Date, XpaType.Bool)] = static code => $"u.CastToBool({code})",
                [(XpaType.Time, XpaType.Bool)] = static code => $"u.CastToBool({code})",
                [(XpaType.Blob, XpaType.Bool)] = static code => $"u.CastToBool(u.ByteArrayToText({code}))",
                [(XpaType.Array, XpaType.Bool)] = static code => $"u.CastToBool({code})",
                [(XpaType.Object, XpaType.Bool)] = static code => $"u.CastToBool({code})",

                [(XpaType.Text, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Number, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Date, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Time, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Bool, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Array, XpaType.Blob)] = static code => $"u.CastToByteArray({code})",
                [(XpaType.Object, XpaType.Blob)] = static code => $"u.CastToByteArray({code})"
            };

    internal static bool TryApply(
        XpaTypedExpression source,
        XpaExpressionDestination destination,
        out XpaTypedExpression result)
    {
        result = source;
        if (string.IsNullOrWhiteSpace(source.Code) ||
            destination.Type == XpaType.Unknown ||
            source.Type == XpaType.Unknown)
        {
            return false;
        }

        var destinationReturnType = CanonicalTypeName(destination.ReturnType);
        if (AreEquivalent(source, destination))
        {
            result = source with { ReturnType = destinationReturnType };
            return true;
        }

        if (destination.Type == XpaType.Number &&
            string.Equals(
                CanonicalTypeName(source.ReturnType),
                "System.Type",
                StringComparison.Ordinal) &&
            decimal.TryParse(
                source.LiteralValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            // An XPA DSOURCE literal has two valid representations. Functions
            // such as DBName consume the entity Type, while numeric expressions
            // consume the data-source ordinal. The literal resolver carries that
            // ordinal as typed metadata so this decision remains in the central
            // type map and never depends on parsing emitted C#.
            result = new XpaTypedExpression(
                source.LiteralValue!,
                destinationReturnType,
                XpaType.Number,
                source.LiteralValue);
            return true;
        }

        if (string.Equals(destinationReturnType, "System.IntPtr", StringComparison.Ordinal) &&
            source.Type == XpaType.Number)
        {
            result = new XpaTypedExpression(
                $"new global::System.IntPtr((int)({source.Code}))",
                destinationReturnType,
                XpaType.Object);
            return true;
        }

        if (destination.Type == XpaType.Array)
        {
            if (!TryConvertArray(source, destinationReturnType, out var arrayCode))
                return false;

            result = new XpaTypedExpression(arrayCode, destinationReturnType, XpaType.Array);
            return true;
        }

        if (!ScalarConversions.TryGetValue((source.Type, destination.Type), out var conversion))
            return false;

        result = new XpaTypedExpression(
            conversion(source.Code),
            destinationReturnType,
            destination.Type);
        return true;
    }

    internal static bool CanConvert(XpaType source, XpaType destination)
        => source == destination ||
           source == XpaType.Unknown ||
           destination == XpaType.Unknown ||
           ScalarConversions.ContainsKey((source, destination)) ||
           (destination == XpaType.Array && source is XpaType.Array or XpaType.Object);

    internal static string Convert(string code, XpaType source, XpaType destination)
    {
        if (source == destination)
            return code;

        var typed = new XpaTypedExpression(code, TypeName(source), source);
        var target = new XpaExpressionDestination(TypeName(destination), destination);
        return TryApply(typed, target, out var result) ? result.Code : code;
    }

    internal static string Convert(
        string code,
        XpaType source,
        XpaType destination,
        string runtimeAccessor)
    {
        var converted = Convert(code, source, destination);
        if (string.Equals(runtimeAccessor, "u", StringComparison.Ordinal))
            return converted;

        return converted.Replace(
            "u.",
            runtimeAccessor.TrimEnd('.') + ".",
            StringComparison.Ordinal);
    }

    internal static XpaType Unify(XpaType left, XpaType right)
    {
        if (left == right)
            return left;
        if (left == XpaType.Unknown)
            return right;
        if (right == XpaType.Unknown)
            return left;
        if (left == XpaType.Object)
            return right;
        if (right == XpaType.Object)
            return left;
        if (IsNumericLike(left) && IsNumericLike(right))
            return XpaType.Number;
        if (left == XpaType.Text || right == XpaType.Text)
            return XpaType.Text;
        return XpaType.Text;
    }

    internal static XpaType FromReturnType(string? returnType)
        => CanonicalTypeName(returnType) switch
        {
            "Text" => XpaType.Text,
            "Number" => XpaType.Number,
            "Date" => XpaType.Date,
            "Time" => XpaType.Time,
            "Bool" => XpaType.Bool,
            "byte[]" => XpaType.Blob,
            "Text[]" or "Number[]" or "Date[]" or "Time[]" or "Bool[]" or "byte[][]" => XpaType.Array,
            "object" or "string[]" or "System.IntPtr" or "ColumnBase" => XpaType.Object,
            _ => XpaType.Unknown
        };

    internal static string CanonicalTypeName(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return "";

        var trimmed = returnType.Trim().Replace("global::", "", StringComparison.Ordinal);
        var unqualified = CanonicalizeQualifiedTypeName(trimmed);

        return unqualified switch
        {
            "TextParameter" or "TextColumn" or "Text" => "Text",
            "NumberParameter" or "NumberColumn" or "Number" or "MVariableIndex" => "Number",
            "DateParameter" or "DateColumn" or "Date" => "Date",
            "TimeParameter" or "TimeColumn" or "Time" => "Time",
            "BoolParameter" or "BoolColumn" or "Bool" => "Bool",
            "ByteArrayParameter" or "ByteArrayColumn" or "byte[]" => "byte[]",
            "ArrayColumn<Text>" or "ArrayParameter<Text>" or "Text[]" => "Text[]",
            "ArrayColumn<Number>" or "ArrayParameter<Number>" or "Number[]" => "Number[]",
            "ArrayColumn<Date>" or "ArrayParameter<Date>" or "Date[]" => "Date[]",
            "ArrayColumn<Time>" or "ArrayParameter<Time>" or "Time[]" => "Time[]",
            "ArrayColumn<Bool>" or "ArrayParameter<Bool>" or "Bool[]" => "Bool[]",
            "ArrayColumn<byte[]>" or "ArrayParameter<byte[]>" or "byte[][]" => "byte[][]",
            "String[]" or "System.String[]" or "string[]" => "string[]",
            "IntPtr" or "System.IntPtr" => "System.IntPtr",
            _ => trimmed
        };
    }

    private static string CanonicalizeQualifiedTypeName(string typeName)
    {
        var trimmed = typeName.Trim();
        var genericStart = trimmed.IndexOf('<');
        if (genericStart > 0 && trimmed.EndsWith(">", StringComparison.Ordinal))
        {
            var outerType = UnqualifyTypeName(trimmed[..genericStart]);
            var genericArguments = SplitGenericArguments(trimmed[(genericStart + 1)..^1])
                .Select(CanonicalTypeName);
            return $"{outerType}<{string.Join(",", genericArguments)}>";
        }

        return UnqualifyTypeName(trimmed);
    }

    private static string UnqualifyTypeName(string typeName)
    {
        var trimmed = typeName.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < trimmed.Length
            ? trimmed[(lastDot + 1)..]
            : trimmed;
    }

    private static IReadOnlyList<string> SplitGenericArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        result.Add(arguments[start..].Trim());
        return result;
    }

    internal static bool IsScalar(XpaType type)
        => type is XpaType.Number or XpaType.Text or XpaType.Date or XpaType.Time or XpaType.Bool or XpaType.Blob;

    internal static bool IsNumericLike(XpaType type)
        => type is XpaType.Number or XpaType.Bool;

    internal static bool IsTemporal(XpaType type)
        => type is XpaType.Date or XpaType.Time;

    internal static string ScalarFunctionToken(XpaType type)
        => type switch
        {
            XpaType.Text => "Text",
            XpaType.Number => "Number",
            XpaType.Date => "Date",
            XpaType.Time => "Time",
            XpaType.Bool => "Bool",
            XpaType.Blob => "ByteArray",
            _ => ""
        };

    internal static string ScalarConversionFunction(XpaType type)
    {
        var token = ScalarFunctionToken(type);
        return string.IsNullOrWhiteSpace(token) ? "" : $"u.CastTo{token}";
    }

    internal static string ArrayConversionFunction(XpaType itemType)
    {
        var token = ScalarFunctionToken(itemType);
        return string.IsNullOrWhiteSpace(token) || itemType == XpaType.Blob ? "" : $"u.CastTo{token}Array";
    }

    internal static string SharedValueArrayFunction(XpaType itemType)
    {
        var token = ScalarFunctionToken(itemType);
        return itemType is not (XpaType.Text or XpaType.Number) || string.IsNullOrWhiteSpace(token)
            ? ""
            : $"u.SharedValGet{token}Array";
    }

    private static bool AreEquivalent(
        XpaTypedExpression source,
        XpaExpressionDestination destination)
    {
        if (source.Type != destination.Type)
            return false;

        if (source.Type != XpaType.Array)
            return true;

        return string.Equals(
            CanonicalTypeName(source.ReturnType),
            CanonicalTypeName(destination.ReturnType),
            StringComparison.Ordinal);
    }

    private static bool TryConvertArray(
        XpaTypedExpression source,
        string destinationReturnType,
        out string code)
    {
        code = "";
        if (source.Type is not (XpaType.Array or XpaType.Object))
            return false;

        code = destinationReturnType switch
        {
            "Text[]" => $"u.CastToTextArray({source.Code})",
            "Number[]" => $"u.CastToNumberArray({source.Code})",
            "Date[]" => $"u.CastToDateArray({source.Code})",
            "Time[]" => $"u.CastToTimeArray({source.Code})",
            "Bool[]" => $"u.CastToBoolArray({source.Code})",
            "byte[][]" => $"u.TextArrayToByteArrayArray(u.CastToTextArray({source.Code}))",
            "string[]" => $"(string[])({source.Code})",
            _ => ""
        };
        return code.Length > 0;
    }

    internal static string TypeName(XpaType type)
        => type switch
        {
            XpaType.Text => "Text",
            XpaType.Number => "Number",
            XpaType.Date => "Date",
            XpaType.Time => "Time",
            XpaType.Bool => "Bool",
            XpaType.Blob => "byte[]",
            XpaType.Array => "object[]",
            XpaType.Object => "object",
            _ => ""
        };
}
