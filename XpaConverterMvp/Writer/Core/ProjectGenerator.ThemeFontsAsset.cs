using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteThemeFonts(string fontFile, string fontsDir, string themeDir, string appNamespace)
    {
        if (!File.Exists(fontFile))
        {
            WriteFontSchemesMap(themeDir, appNamespace, Array.Empty<string>());
            return;
        }
        var lines = File.ReadAllLines(fontFile);
        var duplicateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var classNames = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parts = line.Split(',');
            if (parts.Length < 3)
                continue;
            var rawName = parts[0].Trim();
            var className = BuildIndexedThemeClassName(rawName, duplicateCounts);
            var face = parts[1].Trim();
            if (!float.TryParse(parts[2].Trim(), out var size))
                size = 8f;
            var styles = parts
                .Skip(5)
                .Select(p => p.Trim())
                .Where(p =>
                    p.Equals("Bold", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Italic", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Underline", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Strikeout", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var styleExpr = "System.Drawing.FontStyle.Regular";
            if (styles.Count > 0)
                styleExpr = string.Join(" | ", styles.Select(s => $"System.Drawing.FontStyle.{s}"));
            classNames.Add(className);

            var sb = new StringBuilder();
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using ENV.UI;");
            sb.AppendLine();
            sb.AppendLine($"namespace {appNamespace}.Shared.Theme.Fonts;");
            sb.AppendLine();
            sb.AppendLine($"public class {className} : LoadableFontScheme");
            sb.AppendLine("{");
            sb.AppendLine($"    public {className}()");
            sb.AppendLine("    {");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine($"            Font = new Font(\"{Escape(face)}\", {size.ToString(System.Globalization.CultureInfo.InvariantCulture)}F, {styleExpr}, GraphicsUnit.Point, 0);");
            sb.AppendLine("        }");
            sb.AppendLine("        catch");
            sb.AppendLine("        {");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(fontsDir, $"{className}.cs"), sb.ToString());
        }

        WriteFontSchemesMap(themeDir, appNamespace, classNames);
    }



}

