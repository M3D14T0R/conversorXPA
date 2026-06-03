using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveUpdateValueExpression(string rawWithValue, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects)
        => ResolveUpdateValueExpression(rawWithValue, t, dataObjects, default);

    private static string ResolveUpdateValueExpression(
        string rawWithValue,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        ExpressionEmissionContext context)
    {
        var contextToken = string.Create(
            CultureInfo.InvariantCulture,
            $"{context.SinkKind}|{context.Expected.ReturnType}|{context.Expected.AttrObj}|{context.ParameterType}|{context.PreserveBinding}|{GetExpressionContextBlobTargetCacheKey(context)}|{context.TargetInfo?.AttrObj}|{GetExpressionContextTargetMemberCacheKey(context)}|{context.TargetInfo?.IsBlob}|{context.TargetInfo?.IsArray}|{context.TargetInfo?.IsBoolean}|{context.TargetInfo?.IsNumeric}|{context.TargetInfo?.IsDotNet}");
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{t.Ordinal}|{rawWithValue}|{contextToken}");
        if (_updateValueExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var value = rawWithValue;
        if (int.TryParse(rawWithValue, out var expId))
        {
            t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expId, out var exp);
            if (exp is not null)
            {
                var translated = ResolveExpressionCode(rawWithValue, t, dataObjects, context);
                if (string.IsNullOrWhiteSpace(translated))
                {
                    var special = TryTranslateWholeExpressionSemantically(exp, t);
                    if (!string.IsNullOrWhiteSpace(special))
                    {
                        translated = EmitExpressionForContext(NormalizeDateConstructorMappings(special), t, context);
                    }
                    else if (exp.IsStringLiteral)
                    {
                        translated = ResolveStringLiteralExpressionCode(exp);
                        if (context.SinkKind != default)
                            translated = EmitExpressionForContext(translated, t, context);
                    }
                    else
                    {
                        translated = NormalizeDateConstructorMappings(TranslateXpaExpressionToCSharp(exp.LiteralNormalizedSyntax, t, dataObjects, exp.Attribute));
                        translated = NormalizeVarSetValueExpressions(translated);
                        if (string.Equals(exp.Attribute, "A", StringComparison.OrdinalIgnoreCase))
                        {
                            var trimmed = translated.Trim();
                            const string uriPrefix = "new System.Uri(";
                            if (trimmed.StartsWith(uriPrefix, StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
                                translated = trimmed.Substring(uriPrefix.Length, trimmed.Length - uriPrefix.Length - 1).Trim();
                        }
                        translated = NormalizeExpressionByAttribute(t, exp.Attribute, translated);
                        translated = EmitExpressionForContext(translated, t, context);
                    }
                }

                if (!string.IsNullOrWhiteSpace(translated) &&
                    context.SinkKind == ExpressionSinkKind.Assignment &&
                    context.TargetInfo is TargetValueInfo targetInfo &&
                    targetInfo.IsBlob)
                {
                    translated = EmitExpressionForContext(translated, t, context);
                }

                value = string.IsNullOrWhiteSpace(translated) ? exp.Syntax : translated;
            }
        }
        else if (context.Expected.HasExpectation)
        {
            var expectedReturnType = ResolveReturnTypeForExpectedContext(context.Expected);
            if (TryNormalizeDirectResourceReference(rawWithValue, t, out var directResourceReference))
            {
                value = directResourceReference;
            }
            else if (!string.IsNullOrWhiteSpace(expectedReturnType))
            {
                var translated = TranslateSourceFunctionArgument(rawWithValue, expectedReturnType, t, dataObjects, 0);
                if (!string.IsNullOrWhiteSpace(translated) &&
                    !string.Equals(translated.Trim(), rawWithValue.Trim(), StringComparison.Ordinal))
                    value = translated;
            }
        }
        _updateValueExpressionCache[cacheKey] = value;
        return value;
    }

    private static bool TryNormalizeDirectResourceReference(string rawValue, TaskSemantic task, out string reference)
    {
        reference = "";
        var trimmed = (rawValue ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains('(') ||
            trimmed.Contains(' ') ||
            trimmed.Contains('+') ||
            trimmed.Contains('-') ||
            trimmed.Contains('*') ||
            trimmed.Contains('/'))
            return false;

        var depth = 0;
        while (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
        {
            depth++;
            trimmed = trimmed["_parent.".Length..];
        }

        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains('.', StringComparison.Ordinal))
            return false;

        var owner = task;
        if (depth > 0)
        {
            if (_allTasks is null)
                return false;

            var parentOrdinal = task.ParentOrdinal;
            for (var i = 0; i < depth; i++)
            {
                if (!parentOrdinal.HasValue)
                    return false;

                owner = GetTaskByOrdinal(parentOrdinal, _allTasks) ?? task;
                if (ReferenceEquals(owner, task))
                    return false;

                parentOrdinal = owner.ParentOrdinal;
            }
        }

        if (!TryResolveTaskResourceByNameOrLegacy(owner, trimmed, out var resource))
        {
            if (depth == 0 && TryResolveParentSourceResourceBinding(trimmed, task, out reference, out _))
                return true;

            return false;
        }

        reference = string.Concat(Enumerable.Repeat("_parent.", depth)) + ResolveTaskResourceMemberName(owner, resource);
        return true;
    }

    private static bool TryBuildUnsupportedUpdateExpressionComment(string rawWithValue, TaskSemantic t, out string comment)
    {
        comment = "";
        string? originalSyntax = null;
        if (int.TryParse(rawWithValue, out var expId))
        {
            if (t.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expId, out var exp) && exp is not null)
                originalSyntax = exp.Syntax;
        }
        else
        {
            originalSyntax = rawWithValue;
        }

        if (string.IsNullOrWhiteSpace(originalSyntax))
            return false;

        var unsupportedFunction = GetUnsupportedCompoundStorageFunctionName(originalSyntax);
        if (string.IsNullOrWhiteSpace(unsupportedFunction))
            return false;

        comment = $"/*Update //Couldn't write expression '{originalSyntax}' - Unknown function {unsupportedFunction}{Environment.NewLine}\t\t*/";
        return true;
    }

    private static string? GetUnsupportedCompoundStorageFunctionName(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var match = Regex.Match(expression, @"\b(BufSetUnicode|BufGetUnicode|BufSetBit|BufGetVector|BufSetVector|VariantGetVector|SharedValPack|SharedValUnPack)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        return match.Groups[1].Value;
    }

    private static void EmitUpdateAssignmentStatements(StringBuilder sb, string pad, TaskUpdateDef update, string target, string value, TaskSemantic task, bool preferValueForResourceAssignments = false, bool suppressForcedUndo = false)
    {
        if (TryBuildUnsupportedUpdateExpressionComment(update.WithValue, task, out var comment))
        {
            sb.AppendLine($"{pad}{comment}");
            return;
        }
        sb.AppendLine($"{pad}{BuildUpdateAssignment(update, target, value, task, preferValueForResourceAssignments)}");
        var resource = ResolveTaskResourceForAssignment(task, update.Variable, target);
        if (update.ForcedUpdate && !suppressForcedUndo && !(resource is not null && IsDotNetTaskResource(resource)))
            sb.AppendLine($"{pad}u.DenyUndoFor({target});");
    }

    private static string BuildUpdateAssignment(TaskUpdateDef update, string target, string value, TaskSemantic task, bool preferValueForResourceAssignments = false)
    {
        var resource = ResolveTaskResourceForAssignment(task, update.Variable, target);
        var targetInfo = ResolveTargetValueInfo(task, update.Variable, target, resource);
        string? resolvedColumnType = null;
        if (resource is not null)
        {
            var resourceOwnerTask = ResolveTaskOwnerFromTargetPath(task, target) ?? task;
            resolvedColumnType = ResolveTaskResourceColumnType(resource, _allFieldModels, resourceOwnerTask);
            if (!targetInfo.IsBlob)
                targetInfo = RecalibrateTargetInfoFromResolvedColumnType(targetInfo, resolvedColumnType);
        }
        if (TryNormalizeDirectResourceReference(value, task, out var normalizedDirectReference))
            value = normalizedDirectReference;
        var topLevelCall = TryGetTopLevelFunctionName(value);
        var assignmentContext = CreateAssignmentEmissionContext(targetInfo, target);
        if (TryEmitThroughStrictEmittedExpression(value, task, assignmentContext, out var strictValue))
            value = strictValue;
        else if (TryEmitForDeclaredAssignmentType(value, task, resolvedColumnType, out var declaredValue))
            value = declaredValue;
        value = RenderDeclaredAssignmentBridge(value, resolvedColumnType);
        if (targetInfo.IsArray &&
            value.Trim().Equals("u.CastToByteArray(u.Null())", StringComparison.Ordinal))
            value = "u.CastToTextArray(u.Null())";

        if (update.Incremental)
        {
            var isBooleanTarget = targetInfo.IsBoolean;
            if (((resource is not null &&
                string.Equals(resource.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
                  target.IndexOf("ForceRefresh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                  IsFormattingFunctionExpression(topLevelCall))) ||
                IsBooleanLiteralExpression(value) ||
                isBooleanTarget)
                return $"{target}.Value = {value};";
            return BuildIncrementalAssignment(target, value);
        }
        if (target.StartsWith("_parent.", StringComparison.Ordinal))
            value = Regex.Replace(value, @"\bN\b", target);
        if (IsTopLevelCall(topLevelCall, "u.Blb2File"))
            return $"{target}.Value = {value};";
        if (IsTopLevelCall(topLevelCall, "u.IOCurr"))
            return $"{target}.Value = {value};";
        if (target.StartsWith("_parent.", StringComparison.Ordinal) &&
            targetInfo.IsDotNet &&
            resource is not null)
            return $"{target} = {NormalizeDotNetAssignmentExpression(value, resource.ObjectType)};";
        if (resource is not null &&
            !string.IsNullOrWhiteSpace(resolvedColumnType) &&
            resolvedColumnType.StartsWith("Types.", StringComparison.Ordinal))
            return $"{target}.Value = {value};";
        var isModelColumnTarget = IsSimpleMemberPath(target) &&
                                  !target.StartsWith("Application.", StringComparison.Ordinal);
        var isResourceVariable = resource is not null || target.StartsWith("_parent.", StringComparison.Ordinal);
        if (isModelColumnTarget)
        {
            if (TryNormalizeBlobWrappedNewClrExpression(value, out var modelClrAssignmentValue))
                value = modelClrAssignmentValue;
            return $"{target}.Value = {value};";
        }
        if (target.StartsWith("_parent.", StringComparison.Ordinal))
        {
            if (TryNormalizeBlobWrappedNewClrExpression(value, out var parentClrAssignmentValue))
                value = parentClrAssignmentValue;
            return $"{target}.Value = {value};";
        }
        if (isResourceVariable)
        {
            if (resource is not null && targetInfo.IsDotNet)
                return $"{target} = {NormalizeDotNetAssignmentExpression(value, resource.ObjectType)};";
            if (TryNormalizeBlobWrappedNewClrExpression(value, out var resourceClrAssignmentValue))
                value = resourceClrAssignmentValue;
            if (TryBuildBlobVariantAssignment(target, value, resource?.AttrObj, preferValueForResourceAssignments || update.ForcedUpdate, out var blobVariantAssignment))
                return blobVariantAssignment;
            if (IsTopLevelCall(topLevelCall, "u.Blb2File"))
                return $"{target}.Value = {value};";
            if (targetInfo.IsBlob &&
                !targetInfo.IsArray &&
                resource is not null)
            {
                if (string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase))
                    return $"{target}.Value = null;";
                return $"{target}.Value = {value};";
            }
            if (resource is not null && targetInfo.IsBoolean)
                return $"{target}.Value = {value};";
            if (resource is not null)
            {
                if (targetInfo.IsArray)
                    return $"{target}.Value = {value};";
                if (preferValueForResourceAssignments || update.ForcedUpdate)
                    return $"{target}.Value = {value};";
                return $"{target}.SilentSet({value});";
            }
            return $"{target}.Value = {value};";
        }
        return $"{target}.Value = {value};";
    }

    private static bool TryEmitForDeclaredAssignmentType(
        string value,
        TaskSemantic task,
        string? declaredColumnType,
        out string emitted)
    {
        emitted = "";
        var expectedReturnType = declaredColumnType switch
        {
            "TextColumn" => "Text",
            "NumberColumn" => "Number",
            "DateColumn" => "Date",
            "TimeColumn" => "Time",
            "BoolColumn" => "Bool",
            "ByteArrayColumn" => "byte[]",
            "ArrayColumn<Text>" => "Text[]",
            "ArrayColumn<Number>" => "Number[]",
            "ArrayColumn<Date>" => "Date[]",
            "ArrayColumn<Time>" => "Time[]",
            "ArrayColumn<Bool>" => "Bool[]",
            "ArrayColumn<byte[]>" => "byte[][]",
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(expectedReturnType))
            return false;

        var context = CreateExpectedEmissionContext(ExpectedTypeForReturnType(expectedReturnType));
        return TryEmitThroughStrictEmittedExpression(value, task, context, out emitted);
    }

    private static string RenderDeclaredAssignmentBridge(string value, string? declaredColumnType)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return value;

        return declaredColumnType switch
        {
            "NumberColumn" when IsObjectReturningRuntimeCall(trimmed) => $"u.CastToNumber({trimmed})",
            "TextColumn" when IsObjectReturningRuntimeCall(trimmed) => $"u.CastToText({trimmed})",
            "ByteArrayColumn" when IsObjectReturningRuntimeCall(trimmed) => $"u.CastToByteArray({trimmed})",
            _ => value
        };
    }

    private static bool IsObjectReturningRuntimeCall(string value)
    {
        var functionName = TryGetTopLevelFunctionName(value);
        if (string.IsNullOrWhiteSpace(functionName))
            return false;

        return IsTopLevelCall(functionName, "JavaCompat.JGetStatic") ||
               IsTopLevelCall(functionName, "u.JGet") ||
               IsTopLevelCall(functionName, "u.JCall") ||
               IsTopLevelCall(functionName, "u.JCallStatic") ||
               IsTopLevelCall(functionName, "u.RqQueLst") ||
               IsTopLevelCall(functionName, "u.SharedValGet") ||
               functionName.EndsWith(".RunByPublicName", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFormattingFunctionExpression(string? topLevelCall)
        => IsTopLevelCall(topLevelCall, "u.TStr") || IsTopLevelCall(topLevelCall, "u.MTStr");

}

