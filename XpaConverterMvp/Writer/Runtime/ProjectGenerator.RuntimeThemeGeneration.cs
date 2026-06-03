using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string BuildIndexedThemeClassName(string rawName, Dictionary<string, int> duplicateCounts)
    {
        var baseName = ToPascalIdentifier(rawName);
        if (!duplicateCounts.TryGetValue(baseName, out var count))
            count = 0;
        duplicateCounts[baseName] = count + 1;

        return count switch
        {
            0 => baseName,
            1 => baseName + "_",
            _ => baseName + "_" + (count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static void WriteColorSchemesMap(string themeDir, string appNamespace, IReadOnlyList<string> classNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using XPARuntimeCore.Box.UI;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Shared.Theme;");
        sb.AppendLine();
        sb.AppendLine("public static class ColorSchemes");
        sb.AppendLine("{");
        sb.AppendLine("    static readonly Dictionary<Number, ColorScheme> _map = new Dictionary<Number, ColorScheme>();");
        sb.AppendLine();
        sb.AppendLine("    static ColorSchemes()");
        sb.AppendLine("    {");
        for (var i = 0; i < classNames.Count; i++)
            sb.AppendLine($"        _map.Add({i + 1}, new Colors.{classNames[i]}());");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static ColorScheme Find(Number index)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (index == null || !_map.ContainsKey(index))");
        sb.AppendLine("            return null;");
        sb.AppendLine("        return _map[index];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static ColorScheme Find(int index)");
        sb.AppendLine("    {");
        sb.AppendLine("        return Find((Number)index);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(themeDir, "ColorSchemes.cs"), sb.ToString());
    }

    private static void WriteFontSchemesMap(string themeDir, string appNamespace, IReadOnlyList<string> classNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using XPARuntimeCore.Box.UI;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Shared.Theme;");
        sb.AppendLine();
        sb.AppendLine("public static class FontSchemes");
        sb.AppendLine("{");
        sb.AppendLine("    static readonly Dictionary<Number, FontScheme> _map = new Dictionary<Number, FontScheme>();");
        sb.AppendLine();
        sb.AppendLine("    static FontSchemes()");
        sb.AppendLine("    {");
        for (var i = 0; i < classNames.Count; i++)
            sb.AppendLine($"        _map.Add({i + 1}, new Fonts.{classNames[i]}());");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static FontScheme Find(Number index)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (index == null || !_map.ContainsKey(index))");
        sb.AppendLine("            return null;");
        sb.AppendLine("        return _map[index];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static FontScheme Find(int index)");
        sb.AppendLine("    {");
        sb.AppendLine("        return Find((Number)index);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(themeDir, "FontSchemes.cs"), sb.ToString());
    }
}

