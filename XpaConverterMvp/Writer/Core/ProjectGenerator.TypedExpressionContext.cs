using System;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private enum ExpressionSinkKind
    {
        Default,
        ExpectedValue,
        BooleanCondition,
        BindValue,
        FilterComparison,
        RunArgument,
        ReturnValue,
        Assignment,
        ViewBinding,
        DisplayExpression,
        CallArgument,
        ProgramReference,
        ProgramIndex,
        IoArgument,
        MessageText,
        EvaluateStatement,
        SqlExpression,
    }

    private readonly record struct ExpectedTypeContext(
        string AttrObj,
        string ReturnType,
        bool IsBooleanCondition)
    {
        internal bool HasExpectation =>
            IsBooleanCondition ||
            !string.IsNullOrWhiteSpace(AttrObj) ||
            !string.IsNullOrWhiteSpace(ReturnType);
    }

    private readonly record struct ExpressionEmissionContext(
        ExpressionSinkKind SinkKind,
        ExpectedTypeContext Expected,
        string ParameterType,
        bool PreserveBinding,
        TargetValueInfo? TargetInfo,
        string BlobTarget)
    {
        internal bool HasExpectation => Expected.HasExpectation;
    }

    private static ExpressionEmissionContext CreateBooleanConditionEmissionContext()
        => new(ExpressionSinkKind.BooleanCondition, ExpectedBooleanCondition(), "", false, null, "");

    private static ExpressionEmissionContext CreateBindValueEmissionContext(TargetValueInfo targetInfo, string? blobTarget = null)
        => new(ExpressionSinkKind.BindValue, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, blobTarget ?? targetInfo.TargetMember);

    private static ExpressionEmissionContext CreateFilterComparisonEmissionContext(TargetValueInfo targetInfo)
        => new(ExpressionSinkKind.FilterComparison, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, "");

    private static ExpressionEmissionContext CreateRunArgumentEmissionContext(string parameterType, bool preserveBinding)
        => new(ExpressionSinkKind.RunArgument, ExpectedTypeForParameterType(parameterType), NormalizeReturnTypeToken(parameterType), preserveBinding, null, "");

    private static ExpressionEmissionContext CreateReturnValueEmissionContext(string returnType)
        => new(ExpressionSinkKind.ReturnValue, ExpectedTypeForReturnType(returnType), NormalizeReturnTypeToken(returnType), false, null, "");

    private static ExpressionEmissionContext CreateAssignmentEmissionContext(TargetValueInfo targetInfo, string blobTarget)
        => new(ExpressionSinkKind.Assignment, ExpectedTypeForTarget(targetInfo), "", false, targetInfo, blobTarget);

    private static ExpressionEmissionContext CreateViewBindingEmissionContext(string? expressionAttr)
        => new(ExpressionSinkKind.ViewBinding, ExpectedTypeForExpressionAttribute(expressionAttr), "", false, null, "");

    private static ExpressionEmissionContext CreateDisplayExpressionEmissionContext()
        => new(ExpressionSinkKind.DisplayExpression, ExpectedTypeForReturnType("Number"), "Number", false, null, "");

    private static ExpressionEmissionContext CreateCallArgumentEmissionContext()
        => new(ExpressionSinkKind.CallArgument, default, "", false, null, "");

    private static ExpressionEmissionContext CreateProgramReferenceEmissionContext()
        => new(ExpressionSinkKind.ProgramReference, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateProgramIndexEmissionContext()
        => new(ExpressionSinkKind.ProgramIndex, ExpectedTypeForReturnType("Number"), "Number", false, null, "");

    private static ExpressionEmissionContext CreateIoArgumentEmissionContext()
        => new(ExpressionSinkKind.IoArgument, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateMessageTextEmissionContext()
        => new(ExpressionSinkKind.MessageText, ExpectedTypeForReturnType("Text"), "Text", false, null, "");

    private static ExpressionEmissionContext CreateEvaluateStatementEmissionContext()
        => new(ExpressionSinkKind.EvaluateStatement, default, "", false, null, "");

    private static ExpressionEmissionContext CreateSqlExpressionEmissionContext()
        => new(ExpressionSinkKind.SqlExpression, default, "", false, null, "");

    private static ExpressionEmissionContext CreateExpectedEmissionContext(ExpectedTypeContext expected)
        => new(ExpressionSinkKind.ExpectedValue, expected, NormalizeReturnTypeToken(expected.ReturnType), false, null, "");

    private static ExpectedTypeContext ExpectedTypeForAttrObj(string? attrObj)
    {
        var normalized = NormalizeAttrObjKind(attrObj);
        return new ExpectedTypeContext(normalized, MapAttrObjToReturnType(normalized), false);
    }

    private static ExpectedTypeContext ExpectedTypeForReturnType(string? returnType)
    {
        var normalized = NormalizeReturnTypeToken(returnType);
        return new ExpectedTypeContext(MapReturnTypeToAttrObj(GetValueReturnType(normalized)), normalized, false);
    }

    private static ExpectedTypeContext ExpectedBooleanCondition()
        => new("", "Bool", true);

    private static ExpectedTypeContext ExpectedTypeForExpressionAttribute(string? attribute)
        => ExpectedTypeForAttrObj(attribute);

    private static ExpectedTypeContext ExpectedTypeForParameterType(string? parameterType)
        => ExpectedTypeForReturnType(parameterType);

    private static ExpectedTypeContext ExpectedTypeForTarget(TargetValueInfo targetInfo)
    {
        if (targetInfo.IsArray)
        {
            var itemType = targetInfo.Resource is null
                ? "Text"
                : ResolveArrayColumnItemType(
                    targetInfo.Resource,
                    _allFieldModels,
                    ResolveOwningTaskForResource(targetInfo.Resource));
            return ExpectedTypeForReturnType(itemType == "byte[]" ? "byte[][]" : itemType + "[]");
        }

        if (targetInfo.IsBlob)
            return ExpectedTypeForReturnType("byte[]");
        if (targetInfo.IsDotNet)
            return ExpectedTypeForReturnType(
                targetInfo.Resource is null
                    ? "object"
                    : NormalizeDotNetObjectType(targetInfo.Resource.ObjectType));

        var attr = !string.IsNullOrWhiteSpace(targetInfo.AttrObj)
            ? targetInfo.AttrObj
            : targetInfo.ModelAttrObj;
        return targetInfo.IsNumeric
            ? ExpectedTypeForAttrObj("FIELD_NUMERIC")
            : ExpectedTypeForAttrObj(attr);
    }

    private static string EmitExpressionForExpectedType(string code, TaskSemantic task, ExpectedTypeContext expected)
        => EmitExpressionForContext(code, task, CreateExpectedEmissionContext(expected));

    private static string EmitExpressionForAttrObj(string code, TaskSemantic task, string? attrObj)
        => EmitExpressionForExpectedType(code, task, ExpectedTypeForAttrObj(attrObj));

    private static string EmitExpressionForContext(
        string code,
        TaskSemantic task,
        ExpressionEmissionContext context)
    {
        // Uma string C# desacompanhada do tipo de origem não pode sofrer
        // conversão. A emissão tipada deve ocorrer antes de perder o
        // XpaTypedExpression, como no pipeline da v2.
        return code?.Trim() ?? "";
    }

    private static EmittedExpression EmitExpressionForContext(
        EmittedExpression expression,
        TaskSemantic task,
        ExpressionEmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression.Code))
            return expression;

        var sourceReturnType = expression.HasEffectiveType
            ? expression.EffectiveReturnType
            : expression.IntrinsicReturnType;
        var sourceType = expression.HasEffectiveType
            ? expression.EffectiveXpaType
            : expression.IntrinsicXpaType;
        if (sourceType == XpaType.Unknown)
            sourceType = XpaExpressionTypeMap.FromReturnType(sourceReturnType);

        var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
        var destinationType = XpaExpressionTypeMap.FromReturnType(expectedReturnType);
        if (sourceType == XpaType.Unknown ||
            destinationType == XpaType.Unknown ||
            !XpaExpressionTypeMap.TryApply(
                new XpaTypedExpression(expression.Code.Trim(), sourceReturnType, sourceType),
                new XpaExpressionDestination(expectedReturnType, destinationType),
                out var emitted))
        {
            return expression;
        }

        return expression with
        {
            Code = emitted.Code,
            EffectiveReturnType = emitted.ReturnType,
            EffectiveXpaType = emitted.Type,
            HasEffectiveType = emitted.Type != XpaType.Unknown,
            SinkKind = context.SinkKind.ToString(),
            ExpectedReturnType = expectedReturnType,
            EvidenceKind = "typed-expression",
            EvidenceSourceKey = expression.EvidenceSourceKey,
            FailureReason = ""
        };
    }

    private static string EmitCallArgumentForParameter(
        string expression,
        string parameterType,
        TaskSemantic? task = null,
        bool preserveBinding = false)
    {
        if (task is null || preserveBinding)
            return expression;

        var expectedReturnType = NormalizeReturnTypeToken(parameterType);
        return TryEmitExpectedArgumentFromReliableEvidence(
            expression,
            expectedReturnType,
            task,
            out var emitted)
            ? emitted
            : expression;
    }

    private static string ApplyExpectedTypeContextCentral(
        string code,
        TaskSemantic task,
        ExpectedTypeContext expected)
        => EmitExpressionForExpectedType(code, task, expected);

    private static string ApplyAttributeCastCentral(string expression, string? attrObj)
    {
        var source = new XpaTypedExpression(
            expression,
            "object",
            XpaType.Object);
        var destinationType = XpaExpressionTypeMap.FromReturnType(MapAttrObjToReturnType(attrObj));
        var destination = new XpaExpressionDestination(MapAttrObjToReturnType(attrObj), destinationType);
        return XpaExpressionTypeMap.TryApply(source, destination, out var result)
            ? result.Code
            : expression;
    }

    private static string ApplyAttributeCast(string expression, string? attrObj)
        => ApplyAttributeCastCentral(expression, attrObj);

    private static string NormalizeReturnTypeToken(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return "";

        var trimmed = returnType.Trim();
        if (trimmed.StartsWith("Func<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal))
            return $"Func<{NormalizeReturnTypeToken(trimmed[5..^1])}>";

        return trimmed switch
        {
            "TextParameter" or "TextColumn" or "string" or "String" or "System.String" => "Text",
            "NumberParameter" or "NumberColumn" => "Number",
            "DateParameter" or "DateColumn" => "Date",
            "TimeParameter" or "TimeColumn" => "Time",
            "BoolParameter" or "BoolColumn" or "Boolean" or "System.Boolean" => "Bool",
            "ByteArrayParameter" or "ByteArrayColumn" => "byte[]",
            _ => XpaExpressionTypeMap.CanonicalTypeName(trimmed)
        };
    }

    private static string GetValueReturnType(string? returnType)
    {
        var normalized = NormalizeReturnTypeToken(returnType);
        return normalized.StartsWith("Func<", StringComparison.Ordinal) && normalized.EndsWith(">", StringComparison.Ordinal)
            ? normalized[5..^1]
            : normalized;
    }

    private static string MapAttrObjToReturnType(string? attrObj)
        => NormalizeAttrObjKind(attrObj) switch
        {
            "FIELD_ALPHA" or "FIELD_UNICODE" => "Text",
            "FIELD_NUMERIC" => "Number",
            "FIELD_DATE" => "Date",
            "FIELD_TIME" => "Time",
            "FIELD_BOOLEAN" or "FIELD_LOGICAL" => "Bool",
            "FIELD_BLOB" => "byte[]",
            _ => ""
        };

    private static string MapReturnTypeToAttrObj(string? returnType)
        => GetValueReturnType(returnType) switch
        {
            "Text" => "FIELD_ALPHA",
            "Number" => "FIELD_NUMERIC",
            "Date" => "FIELD_DATE",
            "Time" => "FIELD_TIME",
            "Bool" => "FIELD_BOOLEAN",
            "byte[]" => "FIELD_BLOB",
            _ => ""
        };

    private static XpaType ResolveExpectedXpaType(ExpectedTypeContext expected)
        => XpaExpressionTypeMap.FromReturnType(ResolveReturnTypeForExpectedContext(expected));

    private static string GetExpressionContextTargetMemberCacheKey(ExpressionEmissionContext context)
        => context.TargetInfo?.TargetMember ?? "";

    private static string GetExpressionContextBlobTargetCacheKey(ExpressionEmissionContext context)
        => context.BlobTarget ?? "";

    private static bool IsInputParameterDirection(string? direction)
        => string.IsNullOrWhiteSpace(direction) ||
           direction.Equals("In", StringComparison.OrdinalIgnoreCase) ||
           direction.Equals("Input", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldPreserveParameterBinding(
        System.Collections.Generic.IReadOnlyList<string>? parameterDirections,
        int index)
        => parameterDirections is not null &&
           index >= 0 &&
           index < parameterDirections.Count &&
           !IsInputParameterDirection(parameterDirections[index]);
}
