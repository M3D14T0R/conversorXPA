using System;
using System.Globalization;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static ExpectedTypeContext ResolveExpectedTypeFromExpressionEvidence(
        TaskSemantic task,
        string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return default;

        var source = expression.Trim();
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{task.Ordinal}|{source}");
        if (_expectedTypeEvidenceCache.TryGetValue(cacheKey, out var cachedReturnType))
            return string.IsNullOrWhiteSpace(cachedReturnType)
                ? default
                : ExpectedTypeForReturnType(cachedReturnType);

        if (!TryResolveKnownExpressionReturnTypeWithoutLegacy(task, source, out var returnType))
        {
            _expectedTypeEvidenceCache[cacheKey] = "";
            return default;
        }

        returnType = XpaExpressionTypeMap.CanonicalTypeName(returnType);
        _expectedTypeEvidenceCache[cacheKey] = returnType;
        return ExpectedTypeForReturnType(returnType);
    }

    private static bool TryEmitExpectedArgumentFromReliableEvidence(
        string code,
        string expectedReturnType,
        TaskSemantic task,
        out string rendered)
    {
        rendered = code;
        return !string.IsNullOrWhiteSpace(code) &&
               !string.IsNullOrWhiteSpace(expectedReturnType) &&
               TryResolveKnownExpressionReturnTypeWithoutLegacy(task, code, out var sourceReturnType) &&
               TryApplyTypedDestination(code, sourceReturnType, expectedReturnType, out rendered);
    }

    private static string EmitFromReliableTypeEvidence(
        string expression,
        string sourceReturnType,
        string expectedReturnType,
        string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        return TryApplyTypedDestination(
            expression,
            sourceReturnType,
            expectedReturnType,
            out var emitted)
            ? emitted
            : expression.Trim();
    }

    private static bool TryApplyTypedDestination(
        string expression,
        string sourceReturnType,
        string expectedReturnType,
        out string emitted)
    {
        emitted = expression;
        var sourceTypeName = XpaExpressionTypeMap.CanonicalTypeName(GetValueReturnType(sourceReturnType));
        var destinationTypeName = XpaExpressionTypeMap.CanonicalTypeName(GetValueReturnType(expectedReturnType));
        var sourceType = XpaExpressionTypeMap.FromReturnType(sourceTypeName);
        var destinationType = XpaExpressionTypeMap.FromReturnType(destinationTypeName);
        if (string.IsNullOrWhiteSpace(expression) ||
            sourceType == XpaType.Unknown ||
            destinationType == XpaType.Unknown)
        {
            return false;
        }

        if (!XpaExpressionTypeMap.TryApply(
                new XpaTypedExpression(expression.Trim(), sourceTypeName, sourceType),
                new XpaExpressionDestination(destinationTypeName, destinationType),
                out var converted))
        {
            return false;
        }

        emitted = converted.Code;
        return true;
    }

    private static bool TryResolveTaskResourceStrictReturnType(
        TaskResourceColumnDef resource,
        TaskSemantic ownerTask,
        out string returnType)
    {
        var columnType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
        if (IsDotNetTaskResource(resource))
        {
            returnType = "object";
            return true;
        }

        if (columnType.StartsWith("ArrayColumn<", StringComparison.Ordinal))
        {
            returnType = columnType;
            return true;
        }

        var attrObj = ResolveAttrObjForColumnType(columnType, _allFieldModels, ownerTask);
        if (string.IsNullOrWhiteSpace(attrObj))
            attrObj = ResolveEffectiveTaskResourceAttrObj(resource, ownerTask);

        returnType = MapAttrObjToReturnType(attrObj);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static string CanonicalReturnType(string? returnType)
        => XpaExpressionTypeMap.CanonicalTypeName(returnType);

    private static string ScalarReturnType(string? returnType)
    {
        var canonical = CanonicalReturnType(returnType);
        return canonical switch
        {
            "Text?" => "Text",
            "Number?" => "Number",
            "Date?" => "Date",
            "Time?" => "Time",
            "Bool?" => "Bool",
            _ => canonical
        };
    }
}
