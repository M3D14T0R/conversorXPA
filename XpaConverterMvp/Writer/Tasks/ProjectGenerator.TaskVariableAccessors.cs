using System;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private const string VariableCurrentByNameHelper = "ResolveVariableCurrentByName";

    private static bool UsesVariableCurrentByName(TaskSemantic task)
        => task.ExpressionsSemantic.Items.Any(expression =>
            expression.Syntax.Contains("VarCurrN", StringComparison.OrdinalIgnoreCase));

    private static void EmitVariableCurrentByNameHelper(StringBuilder sb, TaskSemantic task)
    {
        if (!UsesVariableCurrentByName(task))
            return;

        sb.AppendLine($"    object {VariableCurrentByNameHelper}(Text selectedColumnName)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (selectedColumnName != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            var name = selectedColumnName.ToString();");
        foreach (var resource in task.ResourcesSemantic.Ordered
                     .Where(resource => !IsDotNetTaskResource(resource))
                     .GroupBy(resource => resource.Name, StringComparer.Ordinal)
                     .Select(group => group.Last()))
        {
            var member = ResolveTaskResourceMemberName(task, resource);
            sb.AppendLine($"            if (string.Equals(name, \"{Escape(resource.Name)}\", System.StringComparison.Ordinal))");
            sb.AppendLine($"                return {member}.Value;");
        }
        sb.AppendLine("            foreach (var column in Columns)");
        sb.AppendLine("                if (string.Equals(column.Caption, name, System.StringComparison.Ordinal))");
        sb.AppendLine("                    return column.Value;");
        sb.AppendLine("        }");
        sb.AppendLine("        return u.VarCurrN(selectedColumnName);");
        sb.AppendLine("    }");
        sb.AppendLine();
    }
}
