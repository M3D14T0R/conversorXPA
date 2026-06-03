using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void EmitTaskResourceMembers(StringBuilder sb, TaskSemantic t, IReadOnlyList<FieldModelDef> fieldModels)
    {
        if (t.ResourcesSemantic.Ordered.Count == 0)
            return;

        sb.AppendLine("    #region Resource Columns");
        foreach (var c in t.ResourcesSemantic.Ordered)
        {
            var member = ResolveTaskResourceMemberName(t, c);
            var fieldPrefix = IsDotNetTaskResource(c) ? "    internal " : "    internal readonly ";
            sb.AppendLine($"{fieldPrefix}{BuildTaskResourceColumnDeclaration(c, fieldModels, t, member)};");
        }
        sb.AppendLine("    #endregion");
        sb.AppendLine();
    }
}

