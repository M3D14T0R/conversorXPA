using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskModelMembers(StringBuilder sb, TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var modelMembers = BuildModelMembers(t, dataObjects);
        var primaryObj = t.PrimaryDbObj ?? t.InformationDbObj;
        var linkMembers = BuildLinkMembers(t, dataObjects, modelMembers, primaryObj);
        if (modelMembers.Count == 0 && linkMembers.Count == 0)
            return;

        sb.AppendLine("    #region Models");
        foreach (var mm in modelMembers)
        {
            var modelInit = ResolveTaskModelInitializer(t, mm.DbObj, primaryObj);
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == mm.DbObj);
            if (d is null)
                continue;
            var modelTypeRef = ResolveModelTypeReference(d, t);
            sb.AppendLine($"    internal readonly {modelTypeRef} {mm.MemberName} = new {modelTypeRef}(){modelInit};");
        }
        foreach (var lm in linkMembers)
        {
            if (modelMembers.Any(m => string.Equals(m.MemberName, lm.MemberName, StringComparison.Ordinal)))
                continue;
            var d = dataObjects.FirstOrDefault(x => x.Ordinal == lm.Link.DbObj);
            if (d is null)
                continue;
            var modelInit = ResolveTaskModelInitializer(t, lm.Link.DbObj, primaryObj, isLinkMember: true);
            var modelTypeRef = ResolveModelTypeReference(d, t);
            sb.AppendLine($"    internal readonly {modelTypeRef} {lm.MemberName} = new {modelTypeRef}(){modelInit};");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }
}

