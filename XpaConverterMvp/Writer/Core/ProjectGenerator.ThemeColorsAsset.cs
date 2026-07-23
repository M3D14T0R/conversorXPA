using System.IO;
using System.Globalization;
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
            sb.AppendLine($"        ForeColor = {ResolveMagicColorExpression(fore)};");
            sb.AppendLine($"        BackColor = {ResolveMagicColorExpression(back)};");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(colorsDir, $"{className}.cs"), sb.ToString());
        }

        WriteColorSchemesMap(themeDir, appNamespace, classNames);
    }

    private static string ResolveMagicColorExpression(string rawHex)
    {
        if (!uint.TryParse(rawHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return "SystemColors.Control";

        if ((value & 0xFF000000u) == 0xFF000000u)
        {
            var systemColorIndex = unchecked((int)~value);
            return systemColorIndex switch
            {
                0 => "SystemColors.ScrollBar",
                1 => "SystemColors.Desktop",
                2 => "SystemColors.ActiveCaption",
                3 => "SystemColors.InactiveCaption",
                4 => "SystemColors.Menu",
                5 => "SystemColors.Window",
                6 => "SystemColors.WindowFrame",
                7 => "SystemColors.MenuText",
                8 => "SystemColors.WindowText",
                9 => "SystemColors.ActiveCaptionText",
                10 => "SystemColors.ActiveBorder",
                11 => "SystemColors.InactiveBorder",
                12 => "SystemColors.AppWorkspace",
                13 => "SystemColors.Highlight",
                14 => "SystemColors.HighlightText",
                15 => "SystemColors.Control",
                16 => "SystemColors.ControlDark",
                17 => "SystemColors.GrayText",
                18 => "SystemColors.ControlText",
                19 => "SystemColors.InactiveCaptionText",
                20 => "SystemColors.ControlLightLight",
                21 => "SystemColors.ControlDarkDark",
                22 => "SystemColors.ControlLight",
                23 => "SystemColors.InfoText",
                24 => "SystemColors.Info",
                26 => "SystemColors.HotTrack",
                27 => "SystemColors.GradientActiveCaption",
                28 => "SystemColors.GradientInactiveCaption",
                30 => "SystemColors.MenuBar",
                _ => "SystemColors.Control"
            };
        }

        return $"ColorTranslator.FromOle(unchecked((int)0x{value:X8}))";
    }

}

