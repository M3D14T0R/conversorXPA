using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveModelTypeReference(DataObjectDef d, TaskSemantic currentTask)
    {
        var modelType = ResolveDataObjectTypeName(d);
        if (!_isComponentized)
            return $"Models.{modelType}";
        var currentNs = ResolveNamespaceForComponent(currentTask.SourceComponent);
        var sourceNs = ResolveNamespaceForComponent(d.SourceComponent);
        return string.Equals(currentNs, sourceNs, StringComparison.Ordinal)
            ? $"Models.{modelType}"
            : $"{sourceNs}.Models.{modelType}";
    }

    private static Dictionary<int, string> ResolveDataObjectColumnMemberNames(DataObjectDef dataObject, string className)
    {
        var result = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.Ordinal) { className };
        foreach (var column in dataObject.Columns.OrderBy(x => x.Id))
        {
            var baseName = ToPascalIdentifier(column.Name);
            var resolved = baseName;
            if (string.IsNullOrWhiteSpace(resolved))
                resolved = "_";

            if (used.Contains(resolved))
                resolved += "_";

            var suffix = 2;
            while (used.Contains(resolved))
            {
                resolved = $"{baseName}_{suffix}";
                suffix++;
            }

            used.Add(resolved);
            result[column.Id] = resolved;
        }

        return result;
    }

    private static IReadOnlyDictionary<int, string> ResolveDataObjectColumnMemberNames(DataObjectDef dataObject)
    {
        if (_dataObjectColumnMemberNamesByObjectOrdinal.TryGetValue(dataObject.Ordinal, out var cached))
            return cached;

        var built = ResolveDataObjectColumnMemberNames(dataObject, ResolveDataObjectTypeName(dataObject));
        _dataObjectColumnMemberNamesByObjectOrdinal[dataObject.Ordinal] = built;
        return built;
    }

    private static string ResolveDataObjectColumnMemberName(DataObjectDef dataObject, DataColumnDef column)
    {
        var map = ResolveDataObjectColumnMemberNames(dataObject);
        return map.TryGetValue(column.Id, out var memberName)
            ? memberName
            : ToPascalIdentifier(column.Name);
    }

}
