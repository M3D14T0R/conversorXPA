using System;
using System.Collections.Generic;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal enum EmittedExpressionTypeEvidenceKind
{
    Literal,
    XmlExpressionAttribute,
    TaskResourceColumn,
    DataViewColumn,
    FunctionContract,
    SinkExpectedType,
    RegisteredExpression
}

internal readonly record struct EmittedExpressionTypeEvidence(
    string ReturnType,
    XpaType XpaType,
    EmittedExpressionTypeEvidenceKind Kind,
    string SourceKey,
    bool IsExpectedType = false)
{
    internal bool IsKnown => XpaType != XpaType.Unknown || !string.IsNullOrWhiteSpace(ReturnType);
}

internal readonly record struct EmittedExpressionRequest(
    string Code,
    string SinkKind,
    string ExpectedReturnType,
    string TargetKey);

internal readonly record struct StrictEmittedExpression(
    string Code,
    string ReturnType,
    XpaType XpaType,
    EmittedExpressionTypeEvidenceKind EvidenceKind,
    string EvidenceSourceKey)
{
    internal bool HasType => XpaType != XpaType.Unknown || !string.IsNullOrWhiteSpace(ReturnType);
}

internal sealed class EmittedExpressionEngine
{
    internal bool TryEmitFromReliableEvidence(
        EmittedExpressionRequest request,
        IReadOnlyList<EmittedExpressionTypeEvidence> evidence,
        out StrictEmittedExpression emitted)
    {
        emitted = default;
        if (string.IsNullOrWhiteSpace(request.Code))
            return false;

        var source = SelectBestSourceEvidence(evidence);
        if (!source.IsKnown)
            return false;

        var expected = SelectBestExpectedType(request, evidence);
        var code = request.Code.Trim();
        if (!string.IsNullOrWhiteSpace(expected) &&
            !AreSameValueType(source.ReturnType, expected, source.XpaType))
        {
            if (!TryRenderExplicitSinkBridge(code, source.ReturnType, expected, out code))
                return false;
        }

        emitted = new StrictEmittedExpression(
            code,
            CanonicalReturnType(source.ReturnType),
            source.XpaType,
            source.Kind,
            source.SourceKey);
        return true;
    }

    internal bool TrySelectReliableSourceType(
        IReadOnlyList<EmittedExpressionTypeEvidence> evidence,
        out StrictEmittedExpression selected)
    {
        selected = default;
        var source = SelectBestSourceEvidence(evidence);
        if (!source.IsKnown)
            return false;

        selected = new StrictEmittedExpression(
            "",
            CanonicalReturnType(source.ReturnType),
            source.XpaType,
            source.Kind,
            source.SourceKey);
        return true;
    }

    private static EmittedExpressionTypeEvidence SelectBestSourceEvidence(IReadOnlyList<EmittedExpressionTypeEvidence> evidence)
    {
        if (evidence is null || evidence.Count == 0)
            return default;

        foreach (var kind in EvidencePriority)
        {
            for (var i = 0; i < evidence.Count; i++)
            {
                var item = evidence[i];
                if (item.Kind == kind && item.IsKnown && !item.IsExpectedType)
                    return item;
            }
        }

        return default;
    }

    private static string SelectBestExpectedType(
        EmittedExpressionRequest request,
        IReadOnlyList<EmittedExpressionTypeEvidence> evidence)
    {
        if (evidence is not null)
        {
            for (var i = 0; i < evidence.Count; i++)
            {
                var item = evidence[i];
                if (item.IsKnown && item.IsExpectedType)
                    return CanonicalReturnType(item.ReturnType);
            }
        }

        return CanonicalReturnType(request.ExpectedReturnType);
    }

    private static bool AreSameValueType(string sourceReturnType, string expectedReturnType, XpaType sourceXpaType)
    {
        var source = CanonicalReturnType(sourceReturnType);
        var expected = CanonicalReturnType(expectedReturnType);
        if (string.Equals(source, expected, StringComparison.Ordinal))
            return true;

        if (sourceXpaType == XpaType.Unknown)
            return false;

        if (IsArrayReturnType(source) || IsArrayReturnType(expected))
            return false;

        return sourceXpaType == MapReturnTypeToXpaType(expected);
    }

    private static string CanonicalReturnType(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return "";

        var trimmed = returnType.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        var unqualified = lastDot >= 0 && lastDot + 1 < trimmed.Length
            ? trimmed[(lastDot + 1)..]
            : trimmed;

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

    private static XpaType MapReturnTypeToXpaType(string returnType)
        => CanonicalReturnType(returnType) switch
        {
            "Text" => XpaType.Text,
            "Number" => XpaType.Number,
            "Date" => XpaType.Date,
            "Time" => XpaType.Time,
            "Bool" => XpaType.Bool,
            "byte[]" => XpaType.Blob,
            "Text[]" or "Number[]" or "Date[]" or "Time[]" or "Bool[]" or "byte[][]" => XpaType.Array,
            "string[]" => XpaType.Object,
            "object" => XpaType.Object,
            _ => XpaType.Unknown
        };

    private static bool TryRenderExplicitSinkBridge(
        string code,
        string sourceReturnType,
        string expectedReturnType,
        out string rendered)
    {
        rendered = code;
        var source = CanonicalReturnType(sourceReturnType);
        var expected = CanonicalReturnType(expectedReturnType);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(expected))
            return false;

        if (IsAlreadyRenderedForExpected(code, expected))
            return true;

        if (IsNullExpression(code) && IsArrayReturnType(expected))
        {
            rendered = "null";
            return true;
        }

        if (IsNullExpression(code) &&
            string.Equals(expected, "Date", StringComparison.Ordinal))
        {
            rendered = "XPARuntimeCore.Box.Date.Empty";
            return true;
        }

        if (TryBuildFunctionContractBridge(code, expected, out rendered))
            return true;

        rendered = expected switch
        {
            "Text" => $"u.CastToText({code})",
            "Number" when string.Equals(source, "Date", StringComparison.Ordinal) ||
                          string.Equals(source, "Time", StringComparison.Ordinal) => $"u.ToNumber({code})",
            "Number" => $"u.CastToNumber({code})",
            "Date" => $"u.CastToDate({code})",
            "Time" when string.Equals(source, "Number", StringComparison.Ordinal) => $"u.ToTime({code})",
            "Time" => $"u.CastToTime({code})",
            "Bool" => $"u.CastToBool({code})",
            "byte[]" when IsBlobBridgeSource(source) => $"u.CastToByteArray({code})",
            "byte[]" when source.StartsWith("ArrayColumn<", StringComparison.Ordinal) => $"u.CastToByteArray({code})",
            "byte[]" when IsObjectOrArraySource(source) => $"u.CastToByteArray({code})",
            "byte[]" when IsScalarValueSource(source) => $"u.CastToByteArray({code})",
            "Text[]" when IsObjectOrArraySource(source) => $"u.CastToTextArray({code})",
            "Number[]" when IsObjectOrArraySource(source) => $"u.CastToNumberArray({code})",
            "Date[]" when IsObjectOrArraySource(source) => $"u.CastToDateArray({code})",
            "Time[]" when IsObjectOrArraySource(source) => $"u.CastToTimeArray({code})",
            "Bool[]" when IsObjectOrArraySource(source) => $"u.CastToBoolArray({code})",
            "string[]" when IsObjectOrArraySource(source) => $"(string[])({code})",
            "System.IntPtr" when string.Equals(source, "Number", StringComparison.Ordinal) => $"new System.IntPtr((int){code})",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(rendered))
            return false;

        return true;
    }

    private static bool IsAlreadyRenderedForExpected(string code, string expected)
    {
        var trimmed = code.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) && IsArrayReturnType(expected))
            return true;

        return expected switch
        {
            "Text" => trimmed.StartsWith("u.CastToText(", StringComparison.Ordinal),
            "Number" => trimmed.StartsWith("u.CastToNumber(", StringComparison.Ordinal) ||
                        trimmed.StartsWith("u.ToNumber(", StringComparison.Ordinal),
            "Date" => trimmed.StartsWith("u.CastToDate(", StringComparison.Ordinal),
            "Time" => trimmed.StartsWith("u.CastToTime(", StringComparison.Ordinal) ||
                      trimmed.StartsWith("u.ToTime(", StringComparison.Ordinal) ||
                      trimmed.StartsWith("UserMethods.ToTime(", StringComparison.Ordinal),
            "Bool" => trimmed.StartsWith("u.CastToBool(", StringComparison.Ordinal),
            "byte[]" => trimmed.StartsWith("u.CastToByteArray(", StringComparison.Ordinal),
            "Text[]" => trimmed.StartsWith("u.CastToTextArray(", StringComparison.Ordinal),
            "Number[]" => trimmed.StartsWith("u.CastToNumberArray(", StringComparison.Ordinal),
            "Date[]" => trimmed.StartsWith("u.CastToDateArray(", StringComparison.Ordinal),
            "Time[]" => trimmed.StartsWith("u.CastToTimeArray(", StringComparison.Ordinal),
            "Bool[]" => trimmed.StartsWith("u.CastToBoolArray(", StringComparison.Ordinal),
            "string[]" => trimmed.StartsWith("(string[])(", StringComparison.Ordinal),
            "System.IntPtr" => trimmed.StartsWith("new System.IntPtr(", StringComparison.Ordinal) ||
                               trimmed.StartsWith("new IntPtr(", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool TryBuildFunctionContractBridge(string code, string expected, out string rendered)
    {
        rendered = "";
        if (string.IsNullOrWhiteSpace(code) ||
            !TryReadTopLevelCall(code.Trim(), out var functionName, out var args))
            return false;

        if (string.Equals(expected, "Text[]", StringComparison.Ordinal))
        {
            if (IsCall(functionName, "u.GetTextParam") ||
                IsCall(functionName, "u.GetParam"))
            {
                rendered = $"u.CastToTextArray(u.GetParam({args}))";
                return true;
            }

            if (IsCall(functionName, "u.SharedValGet"))
            {
                rendered = $"u.SharedValGetTextArray({args})";
                return true;
            }
        }

        if (string.Equals(expected, "Number[]", StringComparison.Ordinal) &&
            IsCall(functionName, "u.SharedValGet"))
        {
            rendered = $"u.SharedValGetNumberArray({args})";
            return true;
        }

        return false;
    }

    private static bool TryReadTopLevelCall(string code, out string functionName, out string args)
    {
        functionName = "";
        args = "";
        var open = code.IndexOf('(');
        if (open <= 0 || !code.EndsWith(")", StringComparison.Ordinal))
            return false;

        var depth = 0;
        var inString = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"' && (i == 0 || code[i - 1] != '\\'))
                inString = !inString;
            if (inString)
                continue;

            if (ch == '(')
                depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0 && i != code.Length - 1)
                    return false;
            }
        }

        if (depth != 0)
            return false;

        functionName = code[..open].Trim();
        args = code[(open + 1)..^1].Trim();
        return functionName.Length > 0;
    }

    private static bool IsCall(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(actual, expected.StartsWith("u.", StringComparison.Ordinal) ? expected[2..] : $"u.{expected}", StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectOrArraySource(string source)
        => string.Equals(source, "object", StringComparison.Ordinal) ||
           source.EndsWith("[]", StringComparison.Ordinal);

    private static bool IsScalarValueSource(string source)
        => string.Equals(source, "Text", StringComparison.Ordinal) ||
           string.Equals(source, "Number", StringComparison.Ordinal) ||
           string.Equals(source, "Date", StringComparison.Ordinal) ||
           string.Equals(source, "Time", StringComparison.Ordinal) ||
           string.Equals(source, "Bool", StringComparison.Ordinal);

    private static bool IsBlobBridgeSource(string source)
        => string.Equals(source, "object", StringComparison.Ordinal) ||
           string.Equals(source, "Text", StringComparison.Ordinal) ||
           string.Equals(source, "byte[]", StringComparison.Ordinal);

    private static bool IsArrayReturnType(string returnType)
        => CanonicalReturnType(returnType).EndsWith("[]", StringComparison.Ordinal);

    private static bool IsNullExpression(string code)
    {
        var trimmed = code.Trim();
        return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "u.Null()", StringComparison.Ordinal) ||
               string.Equals(trimmed, "Null()", StringComparison.Ordinal);
    }

    private static readonly EmittedExpressionTypeEvidenceKind[] EvidencePriority =
    [
        EmittedExpressionTypeEvidenceKind.Literal,
        EmittedExpressionTypeEvidenceKind.XmlExpressionAttribute,
        EmittedExpressionTypeEvidenceKind.TaskResourceColumn,
        EmittedExpressionTypeEvidenceKind.DataViewColumn,
        EmittedExpressionTypeEvidenceKind.FunctionContract,
        EmittedExpressionTypeEvidenceKind.RegisteredExpression,
        EmittedExpressionTypeEvidenceKind.SinkExpectedType
    ];
}
