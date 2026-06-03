using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveFieldTypeReference(FieldModelDef fm, TaskSemantic currentTask)
    {
        var typeName = ResolveFieldModelTypeName(fm);
        if (!_isComponentized)
            return $"Types.{typeName}";
        var currentNs = ResolveNamespaceForComponent(currentTask.SourceComponent);
        var sourceNs = ResolveNamespaceForComponent(fm.SourceComponent);
        return string.Equals(currentNs, sourceNs, StringComparison.Ordinal)
            ? $"Types.{typeName}"
            : $"{sourceNs}.Types.{typeName}";
    }

    private static string ResolveFieldModelTypeName(FieldModelDef fm)
    {
        if (_fieldModelTypeNameByOrdinal.TryGetValue(fm.Ordinal, out var cached))
            return cached;

        foreach (var item in BuildFieldModelTypeNameMap(_allFieldModels))
            _fieldModelTypeNameByOrdinal[item.Key] = item.Value;

        return _fieldModelTypeNameByOrdinal.TryGetValue(fm.Ordinal, out cached)
            ? cached
            : ToPascalIdentifier(fm.Name);
    }

    private static Dictionary<int, string> BuildFieldModelTypeNameMap(IReadOnlyList<FieldModelDef> fieldModels)
    {
        var result = new Dictionary<int, string>();
        foreach (var group in fieldModels
                     .GroupBy(m => ResolveNamespaceForComponent(m.SourceComponent), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in group.OrderBy(m => m.Ordinal))
            {
                var baseName = ToPascalIdentifier(model.Name);
                var candidate = baseName;
                if (used.Contains(candidate))
                {
                    candidate = $"{baseName}_{model.Ordinal}";
                    var suffix = 2;
                    while (used.Contains(candidate))
                    {
                        candidate = $"{baseName}_{model.Ordinal}_{suffix}";
                        suffix++;
                    }
                }

                used.Add(candidate);
                result[model.Ordinal] = candidate;
            }
        }

        return result;
    }

    private static string ResolveTaskTypeReferenceForCall(TaskSemantic currentTask, TaskSemantic targetTask)
    {
        var className = ResolveTaskTypeReference(targetTask, _allTasks ?? Array.Empty<TaskSemantic>());
        return className;
    }

    private static string ResolveTaskTypeReferenceForRegistry(TaskSemantic targetTask)
    {
        var className = ResolveTaskClassName(targetTask, _allTasks ?? Array.Empty<TaskSemantic>());
        if (!_isComponentized)
            return className;
        var targetNs = ResolveNamespaceForComponent(targetTask.SourceComponent);
        return string.Equals(targetNs, _targetNamespace, StringComparison.Ordinal)
            ? className
            : $"{targetNs}.{className}";
    }
}

