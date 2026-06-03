using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveDataObjectTypeName(DataObjectDef d)
    {
        if (!string.IsNullOrWhiteSpace(d.TypeNameOverride))
            return d.TypeNameOverride!;

        var baseName = ToEntityTypeName(d.Name);
        var allObjects = _dataObjectsByOrdinal?.Values;
        if (allObjects is null)
            return baseName;

        var hasCollision = allObjects.Any(other =>
            other.Ordinal != d.Ordinal &&
            string.Equals(other.Name, d.Name, StringComparison.OrdinalIgnoreCase));
        if (!hasCollision)
            return baseName;

        var physicalStem = Path.GetFileNameWithoutExtension(d.PhysicalName ?? "");
        var suffix = ToEntityTypeName(physicalStem);
        if (string.IsNullOrWhiteSpace(suffix) || string.Equals(suffix, baseName, StringComparison.OrdinalIgnoreCase))
            suffix = "Obj" + d.Ordinal.ToString(CultureInfo.InvariantCulture);
        return baseName + "_" + suffix;
    }

    private static Dictionary<int, string> ResolveDataObjectIndexMemberNames(DataObjectDef dataObject, string className)
    {
        var result = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.Ordinal) { className };
        foreach (var columnName in ResolveDataObjectColumnMemberNames(dataObject, className).Values)
            used.Add(columnName);

        foreach (var index in dataObject.Indexes.OrderBy(x => x.Id))
        {
            var baseName = ToPascalIdentifier($"SortBy{index.Name}");
            var resolved = baseName;
            if (string.IsNullOrWhiteSpace(resolved))
                resolved = "SortBy_";

            if (used.Contains(resolved))
                resolved += "_";

            var suffix = 2;
            while (used.Contains(resolved))
            {
                resolved = $"{baseName}_{suffix}";
                suffix++;
            }

            used.Add(resolved);
            result[index.Id] = resolved;
        }

        return result;
    }

}

