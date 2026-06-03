using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string MapColumnType(DataColumnDef c, IReadOnlyList<FieldModelDef> fieldModels)
    {
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null)
                return $"Types.{ResolveFieldModelTypeName(fm)}";
        }
        if (c.AttrObj == "FIELD_DATE")
            return "DateColumn";
        if (c.AttrObj == "FIELD_TIME")
            return "TimeColumn";
        if (c.AttrObj == "FIELD_BOOLEAN" || c.AttrObj == "FIELD_LOGICAL")
            return "BoolColumn";
        if (c.AttrObj == "FIELD_BLOB")
            return "ByteArrayColumn";
        if (c.AttrObj == "FIELD_NUMERIC")
            return "NumberColumn";
        return "TextColumn";
    }

    private static string GetColumnFormat(DataColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, string columnType)
    {
        if (!string.IsNullOrWhiteSpace(c.Picture))
            return Escape(c.Picture);
        if (!string.IsNullOrWhiteSpace(c.ModelRefObj) && int.TryParse(c.ModelRefObj, out var modelOrdinal))
        {
            var fm = fieldModels.FirstOrDefault(x => x.Ordinal == modelOrdinal);
            if (fm is not null && !string.IsNullOrWhiteSpace(fm.Picture))
                return Escape(fm.Picture);
        }
        return DefaultFormatFor(columnType);
    }

    private static string BuildDbObjectName(DataObjectDef d, IReadOnlyDictionary<string, string>? databaseKinds = null)
    {
        databaseKinds ??= ParseMagicDatabaseKinds(_sourceRoot);
        var isSqlite = !string.IsNullOrWhiteSpace(d.DataSource) &&
                       databaseKinds.TryGetValue(d.DataSource, out var databaseKind) &&
                       string.Equals(databaseKind, "SQLITE", StringComparison.OrdinalIgnoreCase);
        if (isSqlite)
            return d.PhysicalName;
        if (string.IsNullOrWhiteSpace(d.Owner))
            return d.PhysicalName;
        if (string.Equals(d.Owner, ".", StringComparison.OrdinalIgnoreCase))
            return d.PhysicalName;
        if (d.Owner.Contains('%', StringComparison.Ordinal))
            return d.PhysicalName;
        return $"{d.Owner}.{d.PhysicalName}";
    }

    private static string BuildTaskResourceColumnDeclaration(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask, string memberName)
    {
        var type = ResolveTaskResourceColumnType(c, fieldModels, currentTask);
        if (IsDotNetTaskResource(c))
            return BuildDotNetTaskResourceDeclaration(c, type, memberName);
        if (type.StartsWith("ArrayColumn<", StringComparison.Ordinal))
        {
            var itemType = ResolveArrayColumnItemType(c, fieldModels, currentTask);
            var elementPrototype = ResolveArrayColumnElementPrototypeExpression(itemType);
            return $"{type} {memberName} = new {type}({elementPrototype}, \"{Escape(c.Name)}\"){BuildTypedResourceColumnInitializer(c, currentTask, memberName, actualColumnType: type, forceStructuredBlobVector: true)}";
        }
        if (TryBuildArrayColumnDeclaration(c, fieldModels, currentTask, memberName, out var arrayDeclaration))
            return arrayDeclaration;
        if (IsPrimitiveColumnType(type) && !string.IsNullOrWhiteSpace(c.Picture))
            return $"{type} {memberName} = new {type}(\"{Escape(c.Name)}\", \"{Escape(c.Picture!)}\"){BuildTypedResourceColumnInitializer(c, currentTask, memberName, actualColumnType: type, includeFormat: false)}";
        return $"{type} {memberName} = new {type}(\"{Escape(c.Name)}\"){BuildTypedResourceColumnInitializer(c, currentTask, memberName, actualColumnType: type)}";
    }

    private static string BuildDotNetTaskResourceDeclaration(TaskResourceColumnDef c, string type, string memberName)
    {
        if (string.IsNullOrWhiteSpace(type))
            type = "object";

        if (type.EndsWith("[]", StringComparison.Ordinal))
            return $"{type} {memberName} = System.Array.Empty<{type[..^2]}>()";

        return $"{type} {memberName}";
    }

    private static bool TryBuildArrayColumnDeclaration(TaskResourceColumnDef c, IReadOnlyList<FieldModelDef> fieldModels, TaskSemantic currentTask, string memberName, out string declaration)
    {
        declaration = "";
        var collectionType = ResolveArrayCollectionTypeReference(c, fieldModels, currentTask);
        var hasNameOnlyStructuredBlobHint =
            !IsDeclaredTaskParameterResource(c, currentTask) &&
            LooksLikeCollectionNamedBlobResource(c, currentTask, memberName);
        var hasExplicitStructuredBlobShape =
            HasExplicitArrayCellModel(c) ||
            LooksLikeStructuredVectorCollectionType(collectionType) ||
            hasNameOnlyStructuredBlobHint;

        if (string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) &&
            (HasScalarBlobUsageEvidence(c, currentTask) || !hasExplicitStructuredBlobShape))
            return false;
        if (!string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
            !(hasExplicitStructuredBlobShape ||
              HasStructuredBlobVectorEvidence(c, currentTask, memberName)))
            return false;

        var itemType = ResolveArrayColumnItemType(c, fieldModels, currentTask);
        var elementPrototype = ResolveArrayColumnElementPrototypeExpression(itemType);
        declaration = $"ArrayColumn<{itemType}> {memberName} = new ArrayColumn<{itemType}>({elementPrototype}, \"{Escape(c.Name)}\"){BuildTypedResourceColumnInitializer(c, currentTask, memberName, actualColumnType: $"ArrayColumn<{itemType}>", forceStructuredBlobVector: true)}";
        return true;
    }
}
