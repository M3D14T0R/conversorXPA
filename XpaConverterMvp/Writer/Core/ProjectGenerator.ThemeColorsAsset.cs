using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteThemeColors(string clrFile, string colorsDir, string themeDir, string appNamespace)
    {
        if (!File.Exists(clrFile))
        {
            WriteColorSchemesMap(themeDir, appNamespace, Array.Empty<string>());
            return;
        }
        var lines = File.ReadAllLines(clrFile);
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
            var fore = parts[1].Trim();
            var back = parts[2].Trim();
            classNames.Add(className);

            var sb = new StringBuilder();
            sb.AppendLine("using System.Drawing;");
            sb.AppendLine("using XPARuntimeCore.Box.UI;");
            sb.AppendLine();
            sb.AppendLine($"namespace {appNamespace}.Shared.Theme.Colors;");
            sb.AppendLine();
            sb.AppendLine($"public class {className} : ColorScheme");
            sb.AppendLine("{");
            sb.AppendLine($"    public {className}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        ForeColor = ColorTranslator.FromOle(unchecked((int)0x{fore}));");
            sb.AppendLine($"        BackColor = ColorTranslator.FromOle(unchecked((int)0x{back}));");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(colorsDir, $"{className}.cs"), sb.ToString());
        }

        WriteColorSchemesMap(themeDir, appNamespace, classNames);
    }

}

