using System;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteRolesSkeleton(ProjectSemantic parsed, string outputRoot, string appNamespace)
    {
        var rights = parsed.Rights
            .Where(r =>
                ShouldGenerateForTarget(r.SourceComponent) ||
                (_isComponentized && string.IsNullOrWhiteSpace(_targetComponent) && !string.IsNullOrWhiteSpace(r.SourceComponent)))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("using ENV.Security;");
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Roles</summary>");
        sb.AppendLine("public class Roles");
        sb.AppendLine("{");
        sb.AppendLine("    static Roles()");
        sb.AppendLine("    {");
        sb.AppendLine("        Administrator.ApplyAdminTo(typeof(Roles));");
        sb.AppendLine("    }");

        foreach (var right in rights.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = string.IsNullOrWhiteSpace(right.Key) ? right.Name : right.Key;
            var member = ToRoleMemberIdentifier(right.Name);
            sb.AppendLine($"    /// <summary>{Escape(right.Name)}</summary>");
            sb.AppendLine($"    public static readonly Role {member} = new Role(\"{Escape(right.Name)}\", \"{Escape(key)}\");");
        }

        sb.AppendLine("    /// <summary>Administrator</summary>");
        sb.AppendLine("    internal static readonly Role.Administrator Administrator = new Role.Administrator(\"Administrator\", \"Administrator\");");
        sb.AppendLine("    /// <summary>UserManager</summary>");
        sb.AppendLine("    internal static readonly Role UserManager = new Role(\"UserManager\", \"UserManager\", false);");
        sb.AppendLine("    /// <summary>DeveloperTools</summary>");
        sb.AppendLine("    internal static readonly Role DeveloperTools = new Role(\"DeveloperTools\", \"DeveloperTools\", false);");
        sb.AppendLine("}");
        WriteGeneratedSourceFile(outputRoot, "Roles", sb.ToString());
    }
}

