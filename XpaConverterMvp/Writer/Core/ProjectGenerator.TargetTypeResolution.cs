using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    internal readonly record struct TargetValueInfo(
        TaskResourceColumnDef? Resource,
        string? ModelAttrObj,
        string AttrObj,
        string TargetMember,
        bool IsDotNet,
        bool IsArray,
        bool IsBlob,
        bool IsNumeric,
        bool IsBoolean);

    private static TargetValueInfo RecalibrateTargetInfoFromResolvedColumnType(TargetValueInfo targetInfo, string? resolvedColumnType)
    {
        var normalizedColumnType = NormalizeReturnTypeToken(resolvedColumnType);
        var normalizedValueType = GetValueReturnType(normalizedColumnType);
        if (!string.IsNullOrWhiteSpace(normalizedColumnType) &&
            ((normalizedColumnType.Contains('.', StringComparison.Ordinal) &&
              !normalizedColumnType.StartsWith("Types.", StringComparison.Ordinal) &&
              !string.Equals(normalizedValueType, "byte[]", StringComparison.Ordinal) &&
              !string.Equals(normalizedValueType, "byte[][]", StringComparison.Ordinal)) ||
             string.Equals(normalizedColumnType, "System.String[]", StringComparison.Ordinal)))
        {
            return targetInfo with
            {
                AttrObj = "",
                IsBlob = false,
                IsArray = false,
                IsDotNet = true,
                IsNumeric = false,
                IsBoolean = false
            };
        }

        var attrObj = ResolveAttrObjForPrimitiveColumnType(resolvedColumnType);
        if (string.IsNullOrWhiteSpace(attrObj))
            return targetInfo;

        return RecalibrateTargetInfoFromAttrObj(targetInfo, attrObj);
    }

    private static TargetValueInfo RecalibrateTargetInfoFromAttrObj(TargetValueInfo targetInfo, string? attrObj)
    {
        attrObj = NormalizeAttrObjKind(attrObj);
        if (string.IsNullOrWhiteSpace(attrObj))
            return targetInfo;

        return targetInfo with
        {
            AttrObj = attrObj,
            IsBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase),
            IsNumeric = IsNumericAttrObj(attrObj),
            IsBoolean =
                string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase),
            IsDotNet = false
        };
    }

    private static string ResolveAttrObjForPrimitiveColumnType(string? resolvedColumnType)
    {
        if (string.IsNullOrWhiteSpace(resolvedColumnType))
            return "";

        return resolvedColumnType.Trim() switch
        {
            "TextColumn" => "FIELD_ALPHA",
            "NumberColumn" => "FIELD_NUMERIC",
            "DateColumn" => "FIELD_DATE",
            "TimeColumn" => "FIELD_TIME",
            "BoolColumn" => "FIELD_BOOLEAN",
            "ByteArrayColumn" => "FIELD_BLOB",
            _ => ""
        };
    }

    private static string NormalizeAttrObjKind(string? attrObj)
    {
        if (string.IsNullOrWhiteSpace(attrObj))
            return "";

        return attrObj.Trim().ToUpperInvariant() switch
        {
            "FIELD_ALPHA" or "FIELD_NUMERIC" or "FIELD_DATE" or "FIELD_TIME" or "FIELD_BOOLEAN" or "FIELD_LOGICAL" or "FIELD_BLOB" => attrObj.Trim(),
            "FIELD_UNICODE" => "FIELD_ALPHA",
            "A" or "U" => "FIELD_ALPHA",
            "N" => "FIELD_NUMERIC",
            "D" => "FIELD_DATE",
            "T" => "FIELD_TIME",
            "L" or "B" => "FIELD_BOOLEAN",
            "O" => "FIELD_BLOB",
            _ => attrObj.Trim()
        };
    }

    private static string ResolveEffectiveTaskResourceAttrObj(TaskResourceColumnDef? resource, TaskSemantic? ownerTask = null)
    {
        if (resource is null)
            return "";

        if (IsDotNetTaskResource(resource))
            return "";

        if (ownerTask is null)
            _resourceOwnerByReference.TryGetValue(resource, out ownerTask);
        if (ownerTask is not null)
        {
            if (!_effectiveTaskResourceAttrObjCache.TryGetValue(ownerTask.Ordinal, out var taskCache))
            {
                taskCache = new Dictionary<TaskResourceColumnDef, string>(ReferenceEqualityComparer.Instance);
                _effectiveTaskResourceAttrObjCache[ownerTask.Ordinal] = taskCache;
            }

            if (taskCache.TryGetValue(resource, out var cached))
                return cached;

            var resolved = ResolveEffectiveTaskResourceAttrObjCore(resource, ownerTask);
            taskCache[resource] = resolved;
            return resolved;
        }

        return ResolveEffectiveTaskResourceAttrObjCore(resource, null);
    }

    private static string ResolveEffectiveTaskResourceAttrObjCore(TaskResourceColumnDef resource, TaskSemantic? ownerTask)
    {
        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            HasStrongTextScalarUsageEvidence(resource, ownerTask))
            return "FIELD_ALPHA";

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(resource, ownerTask))
            return "FIELD_LOGICAL";

        if (IsBlobTaskResourceCandidate(resource))
            return "FIELD_BLOB";

        if (!string.IsNullOrWhiteSpace(resource.AttrObj))
            return NormalizeAttrObjKind(resource.AttrObj);

        if (!string.IsNullOrWhiteSpace(resource.CellModelAttrObj))
            return NormalizeAttrObjKind(resource.CellModelAttrObj);

        return "";
    }

    private static bool IsBlobTaskResourceCandidate(TaskResourceColumnDef? resource)
    {
        if (resource is null)
            return false;

        if (IsDotNetTaskResource(resource))
            return false;

        if (string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resource.CellModelAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return true;

        return ContainsBlobTypeHint(resource.ObjectType) ||
               ContainsBlobTypeHint(resource.AttrObj) ||
               ContainsBlobTypeHint(resource.ModelRefObj);
    }

    private static bool ContainsBlobTypeHint(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.IndexOf("BlobAnsi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("BlobBinary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("FIELD_BLOB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("MOD_Blob", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("CLOB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("BLOB", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string ResolveAttrObjForColumnType(string? resolvedType, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (string.IsNullOrWhiteSpace(resolvedType))
            return "";

        var normalizedType = resolvedType.Trim();
        var cacheKey = currentTask.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + normalizedType;
        if (_attrObjForColumnTypeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var directAttrObj = normalizedType switch
        {
            "TextColumn" => "FIELD_ALPHA",
            "NumberColumn" => "FIELD_NUMERIC",
            "DateColumn" => "FIELD_DATE",
            "TimeColumn" => "FIELD_TIME",
            "BoolColumn" => "FIELD_BOOLEAN",
            "ByteArrayColumn" => "FIELD_BLOB",
            _ => ""
        };
        if (!string.IsNullOrWhiteSpace(directAttrObj))
            return _attrObjForColumnTypeCache[cacheKey] = directAttrObj;

        var matchingFieldModel = fieldModels.FirstOrDefault(fm =>
        {
            var resolvedReference = ResolveFieldTypeReference(fm, currentTask);
            if (string.Equals(resolvedReference, normalizedType, StringComparison.Ordinal))
                return true;

            var resolvedTypeName = resolvedReference.Split('.').LastOrDefault();
            var normalizedTypeName = normalizedType.Split('.').LastOrDefault();
            return !string.IsNullOrWhiteSpace(resolvedTypeName) &&
                   !string.IsNullOrWhiteSpace(normalizedTypeName) &&
                   string.Equals(resolvedTypeName, normalizedTypeName, StringComparison.Ordinal);
        });
        if (matchingFieldModel is not null)
            return _attrObjForColumnTypeCache[cacheKey] = NormalizeAttrObjKind(matchingFieldModel.AttrObj);

        return _attrObjForColumnTypeCache[cacheKey] = "";
    }

    private static string ResolveTaskResourceColumnType(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (!_taskResourceColumnTypeCache.TryGetValue(currentTask.Ordinal, out var taskCache))
        {
            taskCache = new Dictionary<TaskResourceColumnDef, string>(ReferenceEqualityComparer.Instance);
            _taskResourceColumnTypeCache[currentTask.Ordinal] = taskCache;
        }

        if (taskCache.TryGetValue(c, out var cached))
            return cached;

        var resolved = ResolveTaskResourceColumnTypeCore(c, fieldModels, currentTask);
        taskCache[c] = resolved;
        return resolved;
    }

    private static string ResolveTaskResourceColumnTypeCore(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (IsDotNetTaskResource(c))
            return NormalizeDotNetObjectType(c.ObjectType!);
        var normalizedAttrObj = (c.AttrObj ?? "").Trim();
        var mayOverrideToIntrinsicText =
            string.IsNullOrWhiteSpace(normalizedAttrObj) ||
            string.Equals(normalizedAttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase);
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            HasKeyLikeTextScalarIdentity(c, currentTask))
            return "TextColumn";
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            HasIntrinsicTextScalarIdentity(c, currentTask))
            return "TextColumn";
        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            mayOverrideToIntrinsicText &&
            !string.IsNullOrWhiteSpace(c.InputRange) &&
            BuildTaskResourceInferenceNames(c, currentTask).Any(name =>
                Regex.IsMatch(NormalizeSemanticNameForInference(name), @"(?:^|_)(alpha|alfa)(?:_|$)", RegexOptions.IgnoreCase)))
            return "TextColumn";
        if (string.IsNullOrWhiteSpace(normalizedAttrObj) &&
            !IsDeclaredTaskParameterResource(c, currentTask) &&
            HasStrongNumericScalarUsageEvidence(c, currentTask))
            return "NumberColumn";
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(c, currentTask))
            return "BoolColumn";
        var collectionType = ResolveArrayCollectionTypeReference(c, fieldModels, currentTask);
        var hasNameOnlyStructuredBlobHint =
            !IsDeclaredTaskParameterResource(c, currentTask) &&
            LooksLikeCollectionNamedBlobResource(c, currentTask);
        var hasExplicitStructuredBlobShape =
            HasExplicitArrayCellModel(c) ||
            LooksLikeStructuredVectorCollectionType(collectionType) ||
            hasNameOnlyStructuredBlobHint;
        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasScalarBlobUsageEvidence(c, currentTask))
            return "ByteArrayColumn";

        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            !hasExplicitStructuredBlobShape)
            return "ByteArrayColumn";

        if (string.Equals(normalizedAttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            (hasExplicitStructuredBlobShape ||
             HasStructuredBlobVectorEvidence(c, currentTask)))
        {
            var itemType = ResolveArrayColumnItemType(c, fieldModels, currentTask);
            return $"ArrayColumn<{itemType}>";
        }
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null)
            {
                if (string.Equals(ResolveFieldTypeReference(fm, currentTask), "NumberColumn", StringComparison.Ordinal) &&
                    !IsDeclaredTaskParameterResource(c, currentTask) &&
                    HasStrongTextScalarUsageEvidence(c, currentTask))
                    return "TextColumn";
                return ResolveFieldTypeReference(fm, currentTask);
            }
        }
        return normalizedAttrObj switch
        {
            "FIELD_NUMERIC" => "NumberColumn",
            "FIELD_DATE" => "DateColumn",
            "FIELD_TIME" => "TimeColumn",
            "FIELD_BOOLEAN" => "BoolColumn",
            "FIELD_LOGICAL" => "BoolColumn",
            "FIELD_BLOB" => "ByteArrayColumn",
            _ => "TextColumn"
        };
    }

    private static bool IsPrimitiveColumnType(string type)
        => type is "TextColumn" or "NumberColumn" or "DateColumn" or "TimeColumn" or "BoolColumn" or "ByteArrayColumn";

    private static bool IsDotNetTaskResource(TaskResourceColumnDef c)
        => !string.IsNullOrWhiteSpace(c.ObjectType);

    private static bool IsArrayTaskResource(TaskResourceColumnDef c)
        => string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
           Regex.IsMatch(c.CellModelAttrObj ?? "", "TABLE|GRID|VECTOR|OLE", RegexOptions.IgnoreCase);

    private static bool IsDeclaredTaskParameterResource(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        return currentTask.SelectsSemantic.Items.Any(select =>
            select.IsParameter &&
            !select.IsFunctionSelect &&
            select.ColumnId == c.Id);
    }

    private static string NormalizeDotNetObjectType(string objectType)
    {
        var normalized = NormalizeRawDotNetObjectType(objectType);
        if (string.Equals(normalized, "Cigam.Controls.WebBrowser.WebBrowser", StringComparison.Ordinal))
            return "Shared.Theme.Controls.WebBrowser";
        if (RequiresExternalTypeCompat(normalized))
            return "dynamic";
        return normalized switch
        {
            "String[]" => "System.String[]",
            "string[]" => "System.String[]",
            _ => normalized
        };
    }

    private static string NormalizeRawDotNetObjectType(string objectType)
    {
        var trimmed = objectType.Trim();
        return trimmed.StartsWith("DotNet.", StringComparison.Ordinal)
            ? trimmed["DotNet.".Length..]
            : trimmed;
    }

    private static bool RequiresExternalTypeCompat(string? objectType)
    {
        if (string.IsNullOrWhiteSpace(objectType))
            return false;

        var normalized = NormalizeRawDotNetObjectType(objectType);
        return normalized.StartsWith("Cigam.Utils.Upgrade.Mail.", StringComparison.Ordinal) ||
               normalized.StartsWith("Cigam.WebServices.Apis.Upgrade.", StringComparison.Ordinal);
    }

    private static string BuildExternalTypeCompatCreationExpression(string typeName, IReadOnlyList<string> args)
    {
        var escapedTypeName = Escape(typeName);
        var normalizedArgs = args
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(arg => arg.Trim())
            .ToArray();
        return normalizedArgs.Length == 0
            ? $"ExternalTypeCompat.Create(\"{escapedTypeName}\")"
            : $"ExternalTypeCompat.Create(\"{escapedTypeName}\", {string.Join(", ", normalizedArgs)})";
    }

    private static bool ShouldSuppressExplicitRowLocking(TaskSemantic task, string? rowLocking)
    {
        if (task.ResourceDbs.Count == 0)
            return true;
        return string.Equals(rowLocking, "LockingStrategy.OnUserEdit", StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(task.Execution.TransactionScope) &&
               task.ResourceDbs.Count == 1 &&
               string.Equals(task.ResourceDbs[0].Access, "W", StringComparison.OrdinalIgnoreCase) &&
               task.ResourceDbs[0].Cache == true;
    }

    private static bool HasKeyLikeTextScalarIdentity(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        return names.Any(name =>
        {
            var normalized = NormalizeSemanticNameForInference(name);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   Regex.IsMatch(normalized, @"(?:^|_)(chave|key|pin|versao|version)(?:_|$)", RegexOptions.IgnoreCase);
        });
    }

    private static string ResolveArrayColumnElementPrototypeExpression(string itemType)
    {
        return itemType switch
        {
            "Number" => "new NumberColumn()",
            "Date" => "new DateColumn()",
            "Time" => "new TimeColumn()",
            "Bool" => "new BoolColumn()",
            "byte[]" => "new ByteArrayColumn()",
            _ => "new TextColumn()"
        };
    }

    private static string ResolveArrayCollectionTypeReference(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask)
    {
        if (UsesFileListVectorSource(c, currentTask))
            return "Types.VectorString";

        if (c.CellModelObj.HasValue)
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == c.CellModelObj.Value);
            if (fm is not null)
                return ResolveFieldTypeReference(fm, currentTask);
        }
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null)
                return ResolveFieldTypeReference(fm, currentTask);
        }
        return "";
    }

    private static string ResolveArrayColumnItemType(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic? currentTask = null)
    {
        if (currentTask is not null && UsesFileListVectorSource(c, currentTask))
            return "Text";

        if (currentTask is not null &&
            TryInferArrayColumnItemTypeFromSemanticEvidence(c, currentTask, out var inferredItemType))
            return inferredItemType;

        var attrObj = c.CellModelAttrObj;
        if (string.IsNullOrWhiteSpace(attrObj) && c.CellModelObj.HasValue)
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == c.CellModelObj.Value);
            attrObj = fm?.AttrObj;
        }

        var collectionType = currentTask is null ? "" : ResolveArrayCollectionTypeReference(c, fieldModels, currentTask);
        if (collectionType.EndsWith("VectorString", StringComparison.Ordinal))
            return "Text";
        if (collectionType.EndsWith("VectorInteger", StringComparison.Ordinal) ||
            collectionType.EndsWith("VectorNumber", StringComparison.Ordinal))
            return "Number";
        if (collectionType.EndsWith("VectorDate", StringComparison.Ordinal))
            return "Date";
        if (collectionType.EndsWith("VectorTime", StringComparison.Ordinal))
            return "Time";
        if (collectionType.EndsWith("VectorLogical", StringComparison.Ordinal) ||
            collectionType.EndsWith("VectorBool", StringComparison.Ordinal))
            return "Bool";
        if (LooksLikeTextScalarCollectionType(collectionType))
            return "Text";
        if (LooksLikeBlobScalarCollectionType(collectionType))
            return "byte[]";

        if (string.IsNullOrWhiteSpace(attrObj) || string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return "byte[]";

        return attrObj switch
        {
            "FIELD_NUMERIC" => "Number",
            "FIELD_DATE" => "Date",
            "FIELD_TIME" => "Time",
            "FIELD_BOOLEAN" => "Bool",
            "FIELD_LOGICAL" => "Bool",
            _ => "Text"
        };
    }

    private static bool TryInferArrayColumnItemTypeFromSemanticEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, out string itemType)
    {
        itemType = "";

        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        var normalizedNameTokens = names
            .SelectMany(name => NormalizeSemanticNameForInference(name)
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var compactNames = names
            .Select(name => NormalizeSemanticNameForInference(name).Replace("_", "", StringComparison.Ordinal))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (compactNames.Any(name =>
                name.Contains("varsindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("fieldsindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("variableindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("fieldindex", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("controlindex", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Number";
            return true;
        }

        var texts = EnumerateTaskTextsForArrayItemInference(currentTask).ToArray();
        var updates = EnumerateTaskUpdatesForArrayItemInference(currentTask).ToArray();
        foreach (var name in names)
        {
            var escapedName = Regex.Escape(name);
            if (updates.Any(update =>
                    string.Equals(update.Variable, name, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(ResolveUpdateValueSourceSyntax(update, currentTask), @"\bDataViewVarsIndex\s*\(", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\bDataViewVarsIndex\s*\(", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, $@"\b{escapedName}\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\b(VarName|VarCurr|VarCurrN|VarAttr|VarPic|VarControlID|GetVarName)\s*\(\s*VecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }
        }

        if (normalizedNameTokens.Any(token =>
                token.Equals("alpha", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("alfa", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("array", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("arquivo", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("arquivos", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("files", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("lista", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vec", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vetor", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("vector", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("xml", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("texto", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("tipo", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("tipos", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("parametro", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("parametros", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inout", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Text";
            return true;
        }

        if (normalizedNameTokens.Any(token =>
                token.Equals("numeric", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numerico", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numero", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("numeros", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("integer", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("inteiro", StringComparison.OrdinalIgnoreCase)))
        {
            itemType = "Number";
            return true;
        }

        foreach (var name in names)
        {
            var escapedName = Regex.Escape(name);
            if (texts.Any(text => Regex.IsMatch(text, $@"\bVecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, @"\b(CastToText|Trim|LTrim|RTrim|RepStr|StrToken|Translate|InStr|Left|Right|Mid)\s*\(", RegexOptions.IgnoreCase)))
            {
                itemType = "Text";
                return true;
            }

            if (texts.Any(text => Regex.IsMatch(text, $@"\bVecGet\s*\(\s*{escapedName}\b", RegexOptions.IgnoreCase) &&
                                  Regex.IsMatch(text, @"\b(CastToNumber|ToNumber|Str)\s*\(|!=\s*0\b|==\s*0\b|<=\s*0\b|>=\s*0\b|<\s*0\b|>\s*0\b", RegexOptions.IgnoreCase)))
            {
                itemType = "Number";
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TaskUpdateDef> EnumerateTaskUpdatesForArrayItemInference(TaskSemantic currentTask)
    {
        if (_taskUpdatesForArrayItemInferenceCache.TryGetValue(currentTask.Ordinal, out var cached))
            return cached;

        var updates = currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)
            .Cast<TaskUpdateDef>()
            .ToArray();
        _taskUpdatesForArrayItemInferenceCache[currentTask.Ordinal] = updates;
        return updates;
    }

    private static string ResolveUpdateValueSourceSyntax(TaskUpdateDef update, TaskSemantic currentTask)
    {
        if (!int.TryParse(update.WithValue, out var expressionId) ||
            !currentTask.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(expressionId, out var expression) ||
            expression is null)
        {
            return update.WithValue;
        }

        return ResolveExpressionEntrySourceSyntax(expression);
    }

    private static string NormalizeSemanticNameForInference(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var withWordBoundaries = Regex.Replace(name.Trim(), "([a-z0-9])([A-Z])", "$1_$2");
        return withWordBoundaries.Replace('-', '_');
    }

    private static IEnumerable<string> EnumerateTaskTextsForArrayItemInference(TaskSemantic currentTask)
    {
        if (_taskTextsForArrayItemInferenceCache.TryGetValue(currentTask.Ordinal, out var cached))
            return cached;

        var texts = new List<string>();
        foreach (var expression in currentTask.Expressions)
        {
            if (!string.IsNullOrWhiteSpace(expression.Syntax))
                texts.Add(expression.Syntax);
        }

        foreach (var update in EnumerateTaskUpdatesForArrayItemInference(currentTask))
        {
            if (!string.IsNullOrWhiteSpace(update.WithValue))
                texts.Add(update.WithValue);

            var sourceSyntax = ResolveUpdateValueSourceSyntax(update, currentTask);
            if (!string.IsNullOrWhiteSpace(sourceSyntax) &&
                !string.Equals(sourceSyntax, update.WithValue, StringComparison.Ordinal))
            {
                texts.Add(sourceSyntax);
            }
        }

        var result = texts.ToArray();
        _taskTextsForArrayItemInferenceCache[currentTask.Ordinal] = result;
        return result;
    }

    private static bool HasStructuredBlobVectorEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (HasScalarBlobUsageEvidence(c, currentTask))
            return false;

        if (HasExplicitArrayCellModel(c))
            return true;

        if (!IsDeclaredTaskParameterResource(c, currentTask) &&
            LooksLikeCollectionNamedBlobResource(c, currentTask, memberName))
            return true;

        if (c.DefinitionId.HasValue &&
            Regex.IsMatch(c.CellModelAttrObj ?? "", "TABLE|GRID|VECTOR|OLE", RegexOptions.IgnoreCase))
            return true;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);

        if (names.Length == 0)
            return false;

        bool UsesVectorApiForAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "",
                $@"\b(VecSet|VecGet|VecCellAttr|VecSize|VariantGetVector|BufSetVector|BufGetVector)\s*\(\s*{Regex.Escape(name)}\b",
                RegexOptions.IgnoreCase));

        if (currentTask.Expressions.Any(e => UsesVectorApiForAnyName(e.Syntax)))
            return true;

        IEnumerable<string> updateExpressions =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update?.WithValue))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update?.WithValue))
            .Where(x => !string.IsNullOrWhiteSpace(x))!;

        if (updateExpressions.Any(UsesVectorApiForAnyName))
            return true;

        IEnumerable<TaskUpdateDef> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)!;

        return updates.Any(update =>
            names.Any(name => string.Equals(update!.Variable, name, StringComparison.OrdinalIgnoreCase)) &&
            UsesVectorApiForAnyName(update.WithValue));
    }

    private static bool LooksLikeCollectionNamedBlobResource(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = new[]
            {
                c.Name,
                memberName,
                ResolveTaskResourceMemberName(currentTask, c)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();

        return names.Any(name => Regex.IsMatch(name, @"(?:^|_)(list|lista|vetor|vector|vec|selecionados|camposchave|indexes?|arquivos?|files?)(?:_|$)", RegexOptions.IgnoreCase));
    }

    private static bool HasExplicitArrayCellModel(TaskResourceColumnDef c)
        => c.CellModelObj.HasValue || !string.IsNullOrWhiteSpace(c.CellModelAttrObj);

    private static bool HasScalarBlobUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        static bool UsesVectorApi(string text) =>
            Regex.IsMatch(text ?? "", @"\b(VecSet|VecGet|VecCellAttr|VecSize|VariantGetVector|BufSetVector|BufGetVector|FileListGet|ClientFileListGet)\s*\(", RegexOptions.IgnoreCase);

        static bool UsesScalarBlobApi(string text) =>
            Regex.IsMatch(text ?? "", @"\b(VariantGet|VariantCreate|Blb2File|Blob2Req|File2Blb|BlobToBase64|BlobFromBase64|Buffer)\b", RegexOptions.IgnoreCase);

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;
            if (UsesVectorApi(text))
                return false;
            if (UsesScalarBlobApi(text))
                return true;
        }

        IEnumerable<TaskUpdateDef> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update))
            .Where(x => x is not null)!;

        if (updates.Any(update =>
                names.Any(name => string.Equals(update!.Variable, name, StringComparison.OrdinalIgnoreCase)) &&
                UsesScalarBlobApi(update.WithValue)))
            return true;

        return false;
    }

    private static bool UsesFileListVectorSource(TaskResourceColumnDef c, TaskSemantic currentTask)
    {
        var names = BuildTaskResourceInferenceNames(c, currentTask);

        if (names.Length == 0)
            return false;

        bool TargetsResource(TaskUpdateDef? update) =>
            update is not null &&
            names.Any(name => string.Equals(update.Variable, name, StringComparison.OrdinalIgnoreCase));

        bool IsFileListExpr(string? expr)
        {
            var fn = TryGetTopLevelFunctionName(expr ?? "");
            return IsTopLevelCall(fn, "u.FileListGet") || IsTopLevelCall(fn, "u.ClientFileListGet");
        }

        IEnumerable<TaskUpdateDef?> updates =
            currentTask.StartLogics.SelectMany(x => x.Actions).Select(a => a.Update)
            .Concat(currentTask.EndLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.RowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.SavingRowLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.GroupLogics.SelectMany(x => x.Actions).Select(a => a.Update))
            .Concat(currentTask.HandlersSemantic.Bodies.SelectMany(b => b.OrderedActions).Select(a => a.Update));

        return updates.Any(update => TargetsResource(update) && IsFileListExpr(update?.WithValue));
    }

    private static bool HasStrongTextScalarUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var numericLikeResource =
            string.Equals(c.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CellModelAttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(c.Picture?.Trim() ?? "", @"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);

        if (!numericLikeResource)
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_textualNumericResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _textualNumericResourceOverrideCache[cacheKey] = false;

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        static bool UsesStrongTextApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(Trim|LTrim|RTrim|RepStr|StrToken|Translate|Left|Right|Mid|Flip|FileInfo|FileExist|Upper|Lower|InStr)\s*\(",
                RegexOptions.IgnoreCase);

        static bool UsesPathLiteral(string text) =>
            Regex.IsMatch(text ?? "",
                @"@""[^""]*[\\/][^""]*""|""[^""]*[\\/][^""]*""|""[^""]*\.[A-Za-z0-9]{1,6}""",
                RegexOptions.IgnoreCase);

        static bool UsesTextEmptyComparison(string text) =>
            Regex.IsMatch(text ?? "", @"==\s*""""|!=\s*""""", RegexOptions.IgnoreCase);

        static bool UsesStrongNumericApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(CastToNumber|ToNumber|Val|Abs|Round|Fix|Mod|Pow|Log|Sqrt|DBName|DifDateTime|DateAdd|TimeAdd)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"(?:==|!=|<=|>=|<|>)\s*-?\d+(?:\D|$)");

        static bool LooksLikeIntrinsicNumericResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(qtd|qtde|quantidade|contador|count|indice|index|posicao|position|ponteiro|pointer|sequencia|seq|numero|num|nro|linha|line|coluna|column|ordem|order|tamanho|maximo|minimo|limite|inicio|fim|start|end)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        static bool LooksLikeIntrinsicTextResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(senha|password|passwd|passphrase|charset|mailcharset|token|bearer|oauth|auth|login|extensao|extensÃ£o|suffix|sufixo|mascara|mask|pattern|padrao|padrÃ£o)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        bool UsesStrongNumericRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"(?<![\w.]){escaped}(?![\w.])\s*[-+*/]\s*\d|\d\s*[-+*/]\s*(?<![\w.]){escaped}(?![\w.])",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\b(Mid|Left|Right|StrToken|VecGet|VecSet|IndexOf|Len|Pos|Del)\s*\([^)]*,\s*(?<![\w.]){escaped}(?![\w.])(?:\s*,|\s*\))",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\b(Mid)\s*\([^)]*,[^)]*,\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?:==|!=|<=|>=|<|>)\s*(?<![\w.]){escaped}(?![\w.])|(?<![\w.]){escaped}(?![\w.])\s*(?:==|!=|<=|>=|<|>)",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        var textHits = 0;
        var numericHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;

            if (UsesStrongNumericApi(text) || UsesStrongNumericRole(text))
                numericHits++;

            if (UsesStrongTextApi(text) || UsesPathLiteral(text) || UsesTextEmptyComparison(text))
                textHits++;
        }

        if (names.Any(LooksLikeIntrinsicNumericResourceName) && numericHits > 0)
            return _textualNumericResourceOverrideCache[cacheKey] = false;

        if (names.Any(LooksLikeIntrinsicTextResourceName) && numericHits == 0)
            return _textualNumericResourceOverrideCache[cacheKey] = true;

        var result = textHits >= 2 && numericHits == 0;
        _textualNumericResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static bool HasIntrinsicTextScalarIdentity(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        return names.Any(name =>
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                Regex.IsMatch(normalized, @"(?:^|_)(alpha|alfa)(?:_|$)", RegexOptions.IgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(normalized) &&
                   Regex.IsMatch(
                       normalized,
                       @"(?:^|_)(extensao|extensÃ£o|suffix|sufixo|mascara|mask|pattern|padrao|padrÃ£o|letra|caracter|caractere|char)(?:_|$)",
                       RegexOptions.IgnoreCase);
        });
    }

    private static bool HasStrongNumericScalarUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (string.Equals(c.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return false;

        var inputRange = c.InputRange?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(inputRange) &&
            Regex.IsMatch(inputRange, @"[A-Za-zÃ€-Ã¿]", RegexOptions.CultureInvariant))
            return false;

        var picture = c.Picture?.Trim() ?? "";
        var hasNumericPicture =
            picture.StartsWith("N", StringComparison.OrdinalIgnoreCase) ||
            picture.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(picture, @"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);
        if (!hasNumericPicture)
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_numericTextResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _numericTextResourceOverrideCache[cacheKey] = false;

        bool MentionsAnyName(string text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        static bool UsesStrongNumericApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(CastToNumber|ToNumber|Val|Abs|Round|Fix|Mod|Pow|Log|Sqrt|Str|DenyUndoFor|SetParam)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"(?:==|!=|<=|>=|<|>)\s*-?\d+(?:\D|$)");

        static bool UsesStrongTextApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(Trim|LTrim|RTrim|RepStr|StrToken|Translate|Left|Right|Mid|Flip|FileInfo|FileExist|Upper|Lower|InStr)\s*\(",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "", @"==\s*""""|!=\s*""""", RegexOptions.IgnoreCase);

        static bool LooksLikeIntrinsicTextResourceName(string name)
        {
            var normalized = NormalizeSemanticNameForInference(name);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return Regex.IsMatch(
                normalized,
                @"(?:^|_)(senha|password|passwd|passphrase|charset|mailcharset|token|bearer|oauth|auth|login)(?:_|$)",
                RegexOptions.IgnoreCase);
        }

        bool UsesStrongNumericRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"\b(Str)\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?<![\w.]){escaped}(?![\w.])\s*[-+*/]\s*\d|\d\s*[-+*/]\s*(?<![\w.]){escaped}(?![\w.])",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bCastToNumber\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        bool UsesStrongTextRole(string text)
        {
            foreach (var name in names)
            {
                var escaped = Regex.Escape(name);
                if (Regex.IsMatch(text ?? "",
                        $@"\bCastToText\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*\)",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bVal\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"\bRepStr\s*\(\s*(?<![\w.]){escaped}(?![\w.])\s*,",
                        RegexOptions.IgnoreCase))
                    return true;

                if (Regex.IsMatch(text ?? "",
                        $@"(?:==|!=)\s*""[^""]*""\s*$|(?<![\w.]){escaped}(?![\w.])\s*(?:==|!=)\s*""",
                        RegexOptions.IgnoreCase))
                    return true;
            }

            return false;
        }

        var numericHits = 0;
        var textHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MentionsAnyName(text))
                continue;

            if (UsesStrongNumericApi(text) || UsesStrongNumericRole(text))
                numericHits++;

            if (UsesStrongTextApi(text) || UsesStrongTextRole(text))
                textHits++;
        }

        if (names.Any(LooksLikeIntrinsicTextResourceName))
            return _numericTextResourceOverrideCache[cacheKey] = false;

        var result =
            (numericHits >= 2 && numericHits > textHits) ||
            (numericHits >= 1 && textHits == 0);
        _numericTextResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static bool HasStrongLogicalBlobUsageEvidence(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase))
            return false;

        var cacheKey = string.Join("|",
            currentTask.Ordinal.ToString(),
            c.Id.ToString(),
            c.Name ?? "",
            memberName ?? "");
        if (_logicalBlobResourceOverrideCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = BuildTaskResourceInferenceNames(c, currentTask, memberName);
        if (names.Length == 0)
            return _logicalBlobResourceOverrideCache[cacheKey] = false;

        bool MatchesName(string? text) => names.Any(name =>
            Regex.IsMatch(text ?? "", $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])", RegexOptions.IgnoreCase));

        var dataObjects = (IReadOnlyList<DataObjectDef>)_dataObjectsByOrdinal.Values.ToList();
        var checkBoxBindingHit = currentTask.View.SelectedSupportedControls.Any(ctrl =>
        {
            if (!IsViewCheckBoxControl(ctrl))
                return false;

            var directBinding = ResolveControlDataExpression(ctrl, currentTask, _allTasks, dataObjects);
            return MatchesName(directBinding) ||
                   (!string.IsNullOrWhiteSpace(ctrl.DataColumn) && MatchesName(ctrl.DataColumn)) ||
                   (!string.IsNullOrWhiteSpace(ctrl.ControlName) && MatchesName(ctrl.ControlName));
        });

        static bool UsesBooleanBlobShape(string text) =>
            Regex.IsMatch(text ?? "",
                @"CastToByteArray\s*\(\s*(true|false)\s*\)|CastToByteArray\s*\(\s*u\.Not\s*\(|CastToByteArray\s*\(\s*[A-Za-z_][A-Za-z0-9_\.]*\s*\)",
                RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text ?? "",
                @"\b(Not|CastToBool)\s*\(",
                RegexOptions.IgnoreCase);

        static bool UsesStrongBlobApi(string text) =>
            Regex.IsMatch(text ?? "",
                @"\b(File2Blb|BlobToBase64|Base64ToBlob|ByteArrayToText|HTTPCall|SharedValSet|SetParam)\s*\(",
                RegexOptions.IgnoreCase);

        var booleanHits = 0;
        var blobHits = 0;
        foreach (var text in EnumerateTaskTextsForArrayItemInference(currentTask))
        {
            if (!MatchesName(text))
                continue;

            if (UsesBooleanBlobShape(text))
                booleanHits++;
            if (UsesStrongBlobApi(text))
                blobHits++;
        }

        var result = checkBoxBindingHit && booleanHits >= 1 && blobHits == 0;
        _logicalBlobResourceOverrideCache[cacheKey] = result;
        return result;
    }

    private static string[] BuildTaskResourceInferenceNames(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null)
    {
        var virtualSelects = currentTask.SelectsSemantic.Items.Where(s => s.Type == "V").ToList();
        var orderedResources = currentTask.ResourcesSemantic.Ordered.ToList();
        var resourceIndex = orderedResources.FindIndex(r => r.Id == c.Id);
        var positionalAliases = resourceIndex >= 0 && resourceIndex < virtualSelects.Count
            ? new[] { virtualSelects[resourceIndex].Name, virtualSelects[resourceIndex].RealVarName }
            : Array.Empty<string?>();

        return new[]
            {
                c.Name,
                memberName,
                ResolveTaskResourceMemberName(currentTask, c)
            }
            .Concat(currentTask.SelectsSemantic.Items
                .Where(s => s.ColumnId == c.Id)
                .SelectMany(s => new[] { s.Name, s.RealVarName }))
            .Concat(positionalAliases)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool LooksLikeTextScalarCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        if (!trimmed.StartsWith("Types.", StringComparison.Ordinal))
            return false;

        return !trimmed.Contains("Blob", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("ByteArray", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Date", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Time", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Number", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Numeric", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Bool", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Logical", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains("Vector", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBlobScalarCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        return trimmed.Contains("Blob", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("ByteArray", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStructuredVectorCollectionType(string? collectionType)
    {
        if (string.IsNullOrWhiteSpace(collectionType))
            return false;

        var trimmed = collectionType.Trim();
        return trimmed.EndsWith("VectorString", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorNumber", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorInteger", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorDate", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorTime", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorBool", StringComparison.Ordinal) ||
               trimmed.EndsWith("VectorLogical", StringComparison.Ordinal);
    }

    private static string BuildTypedResourceColumnInitializer(TaskResourceColumnDef c, TaskSemantic currentTask, string? memberName = null, string? actualColumnType = null, bool includeFormat = true, bool forceStructuredBlobVector = false)
    {
        var parts = new List<string>();
        if (includeFormat && !string.IsNullOrWhiteSpace(c.Picture))
            parts.Add($"Format = \"{Escape(c.Picture!)}\"");
        if (string.Equals(c.AttrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase))
            parts.Add("StorageType = TextStorageType.Unicode");
        if (!string.IsNullOrWhiteSpace(c.InputRange))
            parts.Add($"InputRange = \"{Escape(c.InputRange!)}\"");
        if (c.AllowNull.HasValue)
            parts.Add($"AllowNull = {(c.AllowNull.Value ? "true" : "false")}");
        if (c.NullDisplayText is not null)
            parts.Add($"NullDisplayText = \"{Escape(c.NullDisplayText)}\"");
        parts.Add("OnChangeMarkRowAsChanged = false");
        if (string.Equals(actualColumnType?.Trim(), "ByteArrayColumn", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            !HasStrongLogicalBlobUsageEvidence(c, currentTask, memberName) &&
            !(forceStructuredBlobVector || HasStructuredBlobVectorEvidence(c, currentTask, memberName)))
            parts.Add("ContentType = ByteArrayColumnContentType.Ansi");
        if (!string.IsNullOrWhiteSpace(c.DefaultValue))
        {
            var literal = ConvertDefaultValueLiteral(c, actualColumnType);
            if (!string.IsNullOrWhiteSpace(literal))
                parts.Add($"DefaultValue = {literal}");
        }
        if (parts.Count == 0)
            return "";
        return $" {{ {string.Join(", ", parts)} }}";
    }

    private static string ConvertDefaultValueLiteral(TaskResourceColumnDef c, string? actualColumnType = null)
    {
        var raw = c.DefaultValue?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var normalizedType = actualColumnType?.Trim() ?? "";
        if (normalizedType.StartsWith("TextColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "TextColumn", StringComparison.Ordinal))
            return $"\"{Escape(raw)}\"";

        if (normalizedType.StartsWith("NumberColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "NumberColumn", StringComparison.Ordinal))
            return raw;

        if (normalizedType.StartsWith("BoolColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "BoolColumn", StringComparison.Ordinal))
            return raw == "1" ? "true" : "false";

        if (normalizedType.StartsWith("DateColumn", StringComparison.Ordinal) ||
            string.Equals(normalizedType, "DateColumn", StringComparison.Ordinal))
        {
            if (raw == "0")
                return "XPARuntimeCore.Box.Date.Empty";
            if (int.TryParse(raw, out var typedSerial) && typedSerial > 0)
            {
                var dt = new DateTime(1, 1, 1).AddDays(typedSerial - 1);
                return $"new Date({dt.Year},{dt.Month},{dt.Day})";
            }
            if (raw.Length == 8 && raw.All(char.IsDigit))
                return $"new Date({raw[..4]},{raw[4..6]},{raw[6..8]})";
            return "XPARuntimeCore.Box.Date.Now";
        }

        if (c.AttrObj == "FIELD_DATE")
        {
            if (raw == "0")
                return "XPARuntimeCore.Box.Date.Empty";
            if (int.TryParse(raw, out var serial) && serial > 0)
            {
                var dt = new DateTime(1, 1, 1).AddDays(serial - 1);
                return $"new Date({dt.Year},{dt.Month},{dt.Day})";
            }
            if (raw.Length == 8 && raw.All(char.IsDigit))
                return $"new Date({raw[..4]},{raw[4..6]},{raw[6..8]})";
            return "XPARuntimeCore.Box.Date.Now";
        }
        if (c.AttrObj == "FIELD_NUMERIC")
            return raw;
        if (c.AttrObj is "FIELD_BOOLEAN" or "FIELD_LOGICAL")
            return raw == "1" ? "true" : "false";
        return $"\"{Escape(raw)}\"";
    }

    private static TargetValueInfo ResolveTargetValueInfo(
        TaskSemantic task,
        string? variableName,
        string target,
        TaskResourceColumnDef? resolvedResource = null)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            variableName ?? "",
            target ?? "",
            resolvedResource?.Id.ToString(CultureInfo.InvariantCulture) ?? "");
        if (_targetValueInfoCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (resolvedResource is null &&
            TryResolveDataViewMemberTargetValueInfo(task, target, out var dataViewInfo))
        {
            _targetValueInfoCache[cacheKey] = dataViewInfo;
            return dataViewInfo;
        }

        var resource = resolvedResource ?? ResolveTaskResourceForAssignment(task, variableName, target);
        var targetOwnerTask = ResolveTaskOwnerFromTargetPath(task, target);
        var resourceOwnerTask = resource is null
            ? null
            : targetOwnerTask is not null
                ? targetOwnerTask
                : task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
                ? task
                : ResolveOwningTaskForResource(resource);
        var modelAttrObj = NormalizeAttrObjKind(ResolveModelColumnAttrObj(task, target));
        var resourceAttrObj = ResolveEffectiveTaskResourceAttrObj(resource, resourceOwnerTask);
        var attrObj = ResolveEffectiveTargetAttrObj(resourceAttrObj, modelAttrObj);
        if (resource is not null && resourceOwnerTask is not null)
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, resourceOwnerTask);
            if (string.Equals(resolvedType, "ByteArrayColumn", StringComparison.Ordinal))
                attrObj = "FIELD_BLOB";
            var resolvedAttrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, resourceOwnerTask);
            if (!string.IsNullOrWhiteSpace(resolvedAttrObj))
                attrObj = resolvedAttrObj;
        }
        var targetMember = resource is null
            ? ""
            : ResolveTaskResourceMemberName(resourceOwnerTask ?? task, resource);
        var isDotNet = resource is not null && IsDotNetTaskResource(resource);
        var isArray = resource is not null && IsTaskResourceArrayLike(resource, resourceOwnerTask ?? task);
        var allowResourceHints = string.IsNullOrWhiteSpace(modelAttrObj);
        var isBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
                     (allowResourceHints && IsBlobTaskResourceCandidate(resource));
        var isNumeric = IsNumericAttrObj(attrObj) ||
                        (allowResourceHints && IsNumericTaskResourceCandidate(resource, resourceOwnerTask ?? task));
        var isBoolean =
            string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);

        var info = new TargetValueInfo(resource, modelAttrObj, attrObj, targetMember, isDotNet, isArray, isBlob, isNumeric, isBoolean);
        _targetValueInfoCache[cacheKey] = info;
        return info;
    }

    private static TaskSemantic? ResolveTaskOwnerFromTargetPath(TaskSemantic task, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var remaining = targetPath.Trim();
        if (remaining.StartsWith("Application.Instance.", StringComparison.Ordinal))
            return _applicationTask;

        var currentTask = task;
        var sawParentPrefix = false;
        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            sawParentPrefix = true;
            if (!currentTask.ParentOrdinal.HasValue)
                return null;

            var parentTask = GetTaskByOrdinal(currentTask.ParentOrdinal, _allTasks ?? Array.Empty<TaskSemantic>());
            if (parentTask is null)
                return null;

            currentTask = parentTask;
            remaining = remaining["_parent.".Length..];
        }

        return sawParentPrefix ? currentTask : null;
    }

    private static TaskSemantic? ResolveOwningTaskForResource(TaskResourceColumnDef resource)
    {
        if (_allTasks is null)
            return null;

        if (_resourceOwnerByReference.TryGetValue(resource, out var ownerByReference))
            return ownerByReference;

        return _allTasks.FirstOrDefault(t => t.ResourcesSemantic.Ordered.Any(r => r.Id == resource.Id));
    }

    private static bool TryResolveDataViewMemberTargetValueInfo(
        TaskSemantic task,
        string target,
        out TargetValueInfo info)
    {
        info = default;
        if (!TryResolveDataViewMemberColumn(task, target, out var normalizedTarget, out var column))
            return false;

        var attrObj = ResolveEffectiveDataColumnAttrObj(column);
        if (string.IsNullOrWhiteSpace(attrObj))
            return false;

        attrObj = NormalizeAttrObjKind(attrObj);
        var isBlob = string.Equals(attrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase);
        var isNumeric = IsNumericAttrObj(attrObj);
        var isBoolean =
            string.Equals(attrObj, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase);

        info = new TargetValueInfo(
            Resource: null,
            ModelAttrObj: attrObj,
            AttrObj: attrObj,
            TargetMember: normalizedTarget,
            IsDotNet: false,
            IsArray: false,
            IsBlob: isBlob,
            IsNumeric: isNumeric,
            IsBoolean: isBoolean);
        return true;
    }

    private static bool TryResolveDataViewMemberColumn(
        TaskSemantic task,
        string target,
        out string normalizedTarget,
        out DataColumnDef column)
    {
        normalizedTarget = "";
        column = default!;
        if (string.IsNullOrWhiteSpace(target) || !target.Contains('.', StringComparison.Ordinal))
            return false;

        var targetPath = target.Trim();
        if (targetPath.EndsWith(".Value", StringComparison.Ordinal))
            targetPath = targetPath[..^".Value".Length];

        var segments = targetPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        var currentTask = task;
        var index = 0;
        while (index < segments.Length && string.Equals(segments[index], "_parent", StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue || !_tasksByOrdinal.TryGetValue(currentTask.ParentOrdinal.Value, out var parentTask))
                return false;

            currentTask = parentTask;
            index++;
        }

        if (segments.Length - index != 2)
            return false;

        if (_dataObjectsByOrdinal.Count == 0)
            return false;

        var owner = segments[index];
        var member = segments[index + 1];
        var key = BuildDataViewMemberColumnKey(owner, member);
        if (!GetDataViewMemberColumnIndex(currentTask).TryGetValue(key, out column) &&
            !GetDataObjectMemberColumnIndex().TryGetValue(key, out column))
        {
            var ownerWithoutSequence = owner;
            var sequenceStart = ownerWithoutSequence.Length;
            while (sequenceStart > 0 && char.IsDigit(ownerWithoutSequence[sequenceStart - 1]))
                sequenceStart--;

            if (sequenceStart == ownerWithoutSequence.Length || sequenceStart == 0)
                return false;

            ownerWithoutSequence = ownerWithoutSequence[..sequenceStart];
            key = BuildDataViewMemberColumnKey(ownerWithoutSequence, member);
            if (!GetDataObjectMemberColumnIndex().TryGetValue(key, out column))
                return false;
        }

        normalizedTarget = targetPath;
        return true;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> GetDataViewMemberColumnIndex(TaskSemantic task)
    {
        if (_dataViewMemberColumnIndexCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var built = BuildDataViewMemberColumnIndex(task);
        _dataViewMemberColumnIndexCache[task.Ordinal] = built;
        return built;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> BuildDataViewMemberColumnIndex(TaskSemantic task)
    {
        var result = new Dictionary<string, DataColumnDef>(StringComparer.OrdinalIgnoreCase);
        if (_dataObjectsByOrdinal.Count == 0)
            return result;

        var dataObjectList = _dataObjectsByOrdinal.Values.ToList();
        var modelMembers = BuildModelMembers(task, dataObjectList);
        foreach (var mm in modelMembers)
            AddDataViewMemberColumnAliases(result, mm.MemberName, mm.DbObj);

        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        var linkMembers = BuildLinkMembers(task, dataObjectList, modelMembers, primaryObj);
        foreach (var lm in linkMembers)
            AddDataViewMemberColumnAliases(result, lm.MemberName, lm.Link.DbObj);

        return result;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> GetDataObjectMemberColumnIndex()
    {
        if (_dataObjectMemberColumnIndexCache is not null)
            return _dataObjectMemberColumnIndexCache;

        var built = BuildDataObjectMemberColumnIndex();
        _dataObjectMemberColumnIndexCache = built;
        return built;
    }

    private static IReadOnlyDictionary<string, DataColumnDef> BuildDataObjectMemberColumnIndex()
    {
        var result = new Dictionary<string, DataColumnDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var dataObject in _dataObjectsByOrdinal.Values)
        {
            var className = ResolveDataObjectTypeName(dataObject);
            AddDataViewMemberColumnAliases(result, className, dataObject.Ordinal);
            AddDataViewMemberColumnAliases(result, ToPascalIdentifier(dataObject.Name), dataObject.Ordinal);
            AddDataViewMemberColumnAliases(result, ToPascalIdentifier(dataObject.PhysicalName ?? ""), dataObject.Ordinal);
        }

        return result;
    }

    private static void AddDataViewMemberColumnAliases(
        Dictionary<string, DataColumnDef> result,
        string owner,
        int dataObjectOrdinal)
    {
        if (string.IsNullOrWhiteSpace(owner) || !_dataObjectsByOrdinal.TryGetValue(dataObjectOrdinal, out var dataObject))
            return;

        var columnMemberNames = ResolveDataObjectColumnMemberNames(dataObject);
        foreach (var column in dataObject.Columns)
        {
            if (columnMemberNames.TryGetValue(column.Id, out var emittedMemberName))
                AddDataViewMemberColumnAlias(result, owner, emittedMemberName, column);

            AddDataViewMemberColumnAlias(result, owner, ToPascalIdentifier(column.Name), column);
            AddDataViewMemberColumnAlias(result, owner, ToPascalIdentifier(column.DbColumnName ?? ""), column);
        }
    }

    private static void AddDataViewMemberColumnAlias(
        Dictionary<string, DataColumnDef> result,
        string owner,
        string member,
        DataColumnDef column)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(member))
            return;

        result.TryAdd(BuildDataViewMemberColumnKey(owner, member), column);
    }

    private static string BuildDataViewMemberColumnKey(string owner, string member)
        => owner + "." + member;

    private static bool IsDataObjectColumnMemberMatch(
        DataColumnDef column,
        string member,
        IReadOnlyDictionary<int, string> columnMemberNames)
    {
        if (columnMemberNames.TryGetValue(column.Id, out var emittedMemberName) &&
            string.Equals(emittedMemberName, member, StringComparison.Ordinal))
            return true;

        return string.Equals(ToPascalIdentifier(column.Name), member, StringComparison.Ordinal) ||
               string.Equals(ToPascalIdentifier(column.DbColumnName ?? ""), member, StringComparison.Ordinal);
    }

    private static string ResolveEffectiveTargetAttrObj(string resourceAttrObj, string modelAttrObj)
    {
        if (!string.IsNullOrWhiteSpace(resourceAttrObj))
            return resourceAttrObj;

        return modelAttrObj ?? "";
    }

    private static bool IsNumericTaskResourceCandidate(TaskResourceColumnDef? resource, TaskSemantic? ownerTask = null)
    {
        if (resource is null)
            return false;

        if (ownerTask is null)
            _resourceOwnerByReference.TryGetValue(resource, out ownerTask);
        if (ownerTask is not null)
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
            if (string.Equals(resolvedType, "TextColumn", StringComparison.Ordinal))
                return false;
        }

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) &&
            HasStrongTextScalarUsageEvidence(resource, ownerTask))
            return false;

        if (ownerTask is not null &&
            string.Equals(resource.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            HasStrongLogicalBlobUsageEvidence(resource, ownerTask))
            return false;

        if (string.Equals(resource.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return true;

        var picture = resource.Picture?.Trim();
        if (string.IsNullOrWhiteSpace(picture))
            return false;

        return picture.EndsWith("N", StringComparison.OrdinalIgnoreCase) ||
               picture.EndsWith("C", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTaskResourceArrayLike(TaskResourceColumnDef resource, TaskSemantic task)
    {
        if (IsArrayTaskResource(resource))
            return true;

        var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
        return resolvedType.StartsWith("ArrayColumn<", StringComparison.Ordinal);
    }

    private static string? ResolveModelColumnAttrObj(TaskSemantic task, string target)
    {
        if (TryResolveDataViewMemberColumn(task, target, out _, out var column))
            return ResolveEffectiveDataColumnAttrObj(column);

        return ResolveExternalManifestColumnAttrObj(target);
    }

    private static string? ResolveExternalManifestColumnAttrObj(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;

        var targetPath = target.Trim();
        if (targetPath.EndsWith(".Value", StringComparison.Ordinal))
            targetPath = targetPath[..^".Value".Length];

        var segments = targetPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        var owner = segments[^2];
        var member = segments[^1];
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(member))
            return null;

        var index = _externalManifestColumnAttrObjIndex;
        if (index is null || index.Count == 0)
            return null;

        return index.TryGetValue(BuildDataViewMemberColumnKey(owner, member), out var attrObj)
            ? attrObj
            : null;
    }

    private static IReadOnlyDictionary<string, string> BuildExternalManifestColumnAttrObjIndex()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_projectReferenceManifests.Count == 0)
            return result;

        foreach (var manifest in _projectReferenceManifests.Values)
        {
            foreach (var dataObject in manifest.DataObjectDetails)
            {
                var owners = BuildExternalManifestDataObjectOwnerAliases(manifest, dataObject);
                if (owners.Count == 0)
                    continue;

                foreach (var column in dataObject.Columns)
                {
                    var attrObj = NormalizeAttrObjKind(column.AttrObj);
                    if (string.IsNullOrWhiteSpace(attrObj))
                        attrObj = NormalizeAttrObjKind(column.Attribute ?? "");
                    if (string.IsNullOrWhiteSpace(attrObj))
                        continue;

                    var members = BuildExternalManifestColumnMemberAliases(column);
                    if (members.Count == 0)
                        continue;

                    foreach (var owner in owners)
                    foreach (var member in members)
                        result.TryAdd(BuildDataViewMemberColumnKey(owner, member), attrObj);
                }
            }
        }

        return result;
    }

    private static HashSet<string> BuildExternalManifestDataObjectOwnerAliases(ProjectManifest manifest, ProjectManifestDataObject dataObject)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddAlias(dataObject.Name);
        AddAlias(dataObject.PublicName);
        AddAlias(dataObject.PhysicalName);
        if (manifest.DataObjectsByIndex.TryGetValue(dataObject.ObjectIndex, out var generatedName))
            AddAlias(generatedName);

        return result;

        void AddAlias(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var trimmed = value.Trim();
            result.Add(trimmed);
            result.Add(ToPascalIdentifier(trimmed));
        }
    }

    private static HashSet<string> BuildExternalManifestColumnMemberAliases(ProjectManifestDataColumn column)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAlias(column.Name);
        AddAlias(column.DbColumnName);
        AddAlias(column.FieldPhysicalName);
        return result;

        void AddAlias(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var trimmed = value.Trim();
            result.Add(trimmed);
            result.Add(ToPascalIdentifier(trimmed));
        }
    }

    private static string ResolveEffectiveDataColumnAttrObj(DataColumnDef column)
    {
        if (!string.IsNullOrWhiteSpace(column.AttrObj))
            return NormalizeAttrObjKind(column.AttrObj);

        if (!string.IsNullOrWhiteSpace(column.Attribute))
            return NormalizeAttrObjKind(column.Attribute);

        if (column.ModelRefObj is not null &&
            int.TryParse(column.ModelRefObj, out var modelObj))
        {
            var fieldModel = _allFieldModels.FirstOrDefault(x => x.Ordinal == modelObj);
            if (fieldModel is not null && !string.IsNullOrWhiteSpace(fieldModel.AttrObj))
                return NormalizeAttrObjKind(fieldModel.AttrObj);
        }

        return NormalizeAttrObjKind(column.Attribute ?? column.AttrObj);
    }

    private static TaskResourceColumnDef? ResolveTaskResourceForAssignment(TaskSemantic task, string? variableName, string target)
    {
        var cacheKey = string.Join("|",
            task.Ordinal.ToString(CultureInfo.InvariantCulture),
            variableName ?? "",
            target?.Trim() ?? "");
        if (_taskResourceForAssignmentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var normalizedTarget = target?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var byPath = ResolveResourceByTargetPath(task, normalizedTarget, _allTasks ?? Array.Empty<TaskSemantic>());
            if (byPath is not null)
            {
                _taskResourceForAssignmentCache[cacheKey] = byPath;
                return byPath;
            }

            if (task.ResourcesSemantic.ByLegacyName.TryGetValue(normalizedTarget, out var byLegacy))
            {
                _taskResourceForAssignmentCache[cacheKey] = byLegacy;
                return byLegacy;
            }

            if (normalizedTarget.EndsWith(".Value", StringComparison.Ordinal))
            {
                normalizedTarget = normalizedTarget[..^".Value".Length];
                byPath = ResolveResourceByTargetPath(task, normalizedTarget, _allTasks ?? Array.Empty<TaskSemantic>());
                if (byPath is not null)
                {
                    _taskResourceForAssignmentCache[cacheKey] = byPath;
                    return byPath;
                }
            }

            foreach (var resource in task.ResourcesSemantic.Ordered)
            {
                var memberName = ResolveTaskResourceMemberName(task, resource);
                if (string.Equals(memberName, normalizedTarget, StringComparison.Ordinal))
                {
                    _taskResourceForAssignmentCache[cacheKey] = resource;
                    return resource;
                }

                var sanitizedCandidates = new[]
                {
                    ToCodeIdentifierPreservingCase(resource.Name ?? ""),
                    ToPascalIdentifier(resource.Name ?? ""),
                    ToLegacyVariableName(resource.Name ?? "")
                };

                if (sanitizedCandidates.Any(candidate => string.Equals(candidate, normalizedTarget, StringComparison.Ordinal)))
                {
                    _taskResourceForAssignmentCache[cacheKey] = resource;
                    return resource;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(variableName) && task.ResourcesSemantic.ByName.TryGetValue(variableName, out var byName))
        {
            _taskResourceForAssignmentCache[cacheKey] = byName;
            return byName;
        }

        _taskResourceForAssignmentCache[cacheKey] = null;
        return null;
    }

    private static TaskResourceColumnDef? ResolveAncestorTaskResourceForAssignment(TaskSemantic task, string? variableName, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            return null;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            if (!_tasksByOrdinal.TryGetValue(parentOrdinal.Value, out var parentTask))
                return null;
            if (parentTask is null)
                return null;

            if (parentTask.ResourcesSemantic.ByName.TryGetValue(variableName, out var parentResource))
                return parentResource;

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return null;
    }

    private static TaskResourceColumnDef? ResolveResourceByTargetPath(TaskSemantic task, string? targetPath, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var currentTask = task;
        var remaining = targetPath.Trim();
        const string applicationInstancePrefix = "Application.Instance.";
        if (remaining.StartsWith(applicationInstancePrefix, StringComparison.Ordinal))
        {
            var appTask = _applicationTask;
            if (appTask is null)
                return null;

            currentTask = appTask;
            remaining = remaining[applicationInstancePrefix.Length..];
        }

        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue)
                return null;

            var parentTask = GetTaskByOrdinal(currentTask.ParentOrdinal, allTasks);
            if (parentTask is null)
                return null;

            currentTask = parentTask;
            remaining = remaining["_parent.".Length..];
        }

        if (remaining.EndsWith(".Value", StringComparison.Ordinal))
            remaining = remaining[..^".Value".Length];

        if (GetTaskResourcesByMemberName(currentTask).TryGetValue(remaining, out var matchedResource))
            return matchedResource;

        var ancestorResource = ResolveAncestorResourceByMemberPath(task, targetPath);
        if (ancestorResource is not null)
            return ancestorResource;

        return null;
    }

    private static TaskResourceColumnDef? ResolveAncestorResourceByMemberPath(TaskSemantic task, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        var remaining = targetPath.Trim();
        var sawParentPrefix = false;
        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            sawParentPrefix = true;
            remaining = remaining["_parent.".Length..];
        }

        if (!sawParentPrefix || string.IsNullOrWhiteSpace(remaining) || remaining.Contains('.', StringComparison.Ordinal))
            return null;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            if (!_tasksByOrdinal.TryGetValue(parentOrdinal.Value, out var parentTask) || parentTask is null)
                return null;

            if (GetTaskResourcesByMemberName(parentTask).TryGetValue(remaining, out var resource))
                return resource;

            parentOrdinal = parentTask.ParentOrdinal;
        }

        return null;
    }


}
