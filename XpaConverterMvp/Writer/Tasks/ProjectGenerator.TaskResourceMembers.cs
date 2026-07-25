using System.Text;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskResourceMembers(
        StringBuilder sb,
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<FieldModelDef> fieldModels)
    {
        var compatibilitySelects = t.SelectsSemantic.Items
            .Where(select => IsUnmappedRelationalSelect(select, t, dataObjects))
            .GroupBy(ResolveUnmappedRelationalSelectMemberName, System.StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (t.ResourcesSemantic.Ordered.Count == 0 && compatibilitySelects.Count == 0)
            return;

        sb.AppendLine("    #region Resource Columns");
        foreach (var c in t.ResourcesSemantic.Ordered)
        {
            var member = ResolveTaskResourceMemberName(t, c);
            var fieldPrefix = IsDotNetTaskResource(c) ? "    internal " : "    internal readonly ";
            sb.AppendLine($"{fieldPrefix}{BuildTaskResourceColumnDeclaration(c, fieldModels, t, member)};");
        }
        foreach (var select in compatibilitySelects)
        {
            var member = ResolveUnmappedRelationalSelectMemberName(select);
            var columnType = ResolveUnmappedRelationalSelectColumnType(select, t, dataObjects);
            sb.AppendLine($"    // Compatibility: mapped component metadata does not contain XPA select {select.Name} (ColumnId={select.ColumnId}).");
            sb.AppendLine($"    internal readonly {columnType} {member} = new {columnType}(\"{Escape(select.RealVarName ?? select.Name ?? member)}\") {{ OnChangeMarkRowAsChanged = false }};");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }
}

