using System;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static List<(int DbObj, string ModelType, string MemberName)> BuildModelMembers(TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_modelMembersCache.TryGetValue(t.Ordinal, out var cached))
            return cached;
        if (_modelMembersResolutionInProgress.Contains(t.Ordinal))
            return new List<(int DbObj, string ModelType, string MemberName)>();

        _modelMembersResolutionInProgress.Add(t.Ordinal);
        try
        {
        var result = new List<(int DbObj, string ModelType, string MemberName)>();
        var reservedNames = new HashSet<string>(StringComparer.Ordinal);
        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        reservedNames.Add(ResolveTaskClassName(t, allTasks));
        foreach (var rc in t.ResourcesSemantic.Ordered)
            reservedNames.Add(ResolveTaskResourceMemberName(t, rc));
        foreach (var dbObj in t.ResourceDataObjects.Distinct())
        {
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == dbObj);
            if (d is null)
                continue;
            var modelType = ResolveDataObjectTypeName(d);
            var baseMember = modelType;
            var member = baseMember;
            var suffix = 2;
            while (reservedNames.Contains(member) || result.Any(x => x.MemberName == member))
            {
                member = baseMember + suffix;
                suffix++;
            }
            result.Add((dbObj, modelType, member));
        }
        _modelMembersCache[t.Ordinal] = result;
        return result;
        }
        finally
        {
            _modelMembersResolutionInProgress.Remove(t.Ordinal);
        }
    }
}

