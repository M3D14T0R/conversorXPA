using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool UsesViewCompat(ProjectSemantic parsed)
        => parsed.Tasks.Any(task => task.Expressions.Any(expr => LooksLikeViewCompatSyntax(expr.Syntax)));

    private static bool LooksLikeViewCompatSyntax(string? syntax)
        => !string.IsNullOrWhiteSpace(syntax) &&
           Regex.IsMatch(syntax, @"(?<![\w.])ImageReload\s*\(", RegexOptions.IgnoreCase);

    private static void WriteViewCompatAsset(string outputRoot, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("internal static class ViewCompat");
        sb.AppendLine("{");
        sb.AppendLine("    internal static bool ImageReload(object path)");
        sb.AppendLine("    {");
        sb.AppendLine("        _ = path;");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "ViewCompat.cs"), sb.ToString(), Encoding.UTF8);
    }
}
