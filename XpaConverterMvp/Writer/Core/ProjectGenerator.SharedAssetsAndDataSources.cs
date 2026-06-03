using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteSharedThemeAssets(string sourceRoot, string sharedDir, string appNamespace, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> tasks)
    {
        var themeDir = Path.Combine(sharedDir, "Theme");
        var colorsDir = Path.Combine(themeDir, "Colors");
        var fontsDir = Path.Combine(themeDir, "Fonts");
        var controlsDir = Path.Combine(themeDir, "Controls");
        var printingDir = Path.Combine(themeDir, "Printing");
        var textIoDir = Path.Combine(themeDir, "TextIO");
        Directory.CreateDirectory(themeDir);
        Directory.CreateDirectory(colorsDir);
        Directory.CreateDirectory(fontsDir);
        Directory.CreateDirectory(controlsDir);
        Directory.CreateDirectory(printingDir);
        Directory.CreateDirectory(textIoDir);

        var runtimeFiles = ResolveRuntimeThemeFiles(sourceRoot);
        WriteSharedDataSources(sourceRoot, sharedDir, appNamespace, dataObjects, tasks);
        WriteSharedDebugHelper(sharedDir, appNamespace);
        var hasThemeColors = File.Exists(runtimeFiles.ColorFilePath);
        var hasThemeFonts = File.Exists(runtimeFiles.FontFilePath);
        WriteThemeControls(controlsDir, appNamespace);
        WriteThemePrinting(printingDir, appNamespace, hasThemeFonts, hasThemeColors);
        WriteThemeTextIo(textIoDir, appNamespace);
        WriteThemeColors(runtimeFiles.ColorFilePath, colorsDir, themeDir, appNamespace);
        WriteThemeFonts(runtimeFiles.FontFilePath, fontsDir, themeDir, appNamespace);
    }

    private static bool UsesSharedPrintingAssets(ProjectSemantic parsed)
    {
        return parsed.Tasks.Any(t =>
            t.Layout.PrintForms.Count > 0 ||
            t.Layout.TextForms.Count > 0 ||
            t.Ios.Count > 0 ||
            t.Io is not null);
    }

    private static void WriteSharedPrintingAssets(string sharedDir, string appNamespace, IReadOnlyList<TaskSemantic> tasks)
    {
        var printingDir = Path.Combine(sharedDir, "Printing");
        Directory.CreateDirectory(printingDir);
        var printerNames = tasks
            .SelectMany(t => t.Ios)
            .Select(io => io.Machine)
            .Where(machine => !string.IsNullOrWhiteSpace(machine))
            .Select(machine => ToPascalIdentifier(machine!))
            .Where(machine => !string.IsNullOrWhiteSpace(machine))
            .Append("Printer1")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(machine => machine, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = new List<string>
        {
            "using ENV.Printing;",
            $"namespace {appNamespace}.Shared.Printing;",
            "",
            "public class Printers",
            "{",
            "    static Printers()",
            "    {",
            "    }",
        };
        foreach (var printerName in printerNames)
        {
            lines.Add("");
            lines.Add($"    /// <summary>{printerName}</summary>");
            lines.Add($"    public static readonly Printer {printerName} = new Printer(\"{printerName}\");");
        }
        lines.Add("}");

        File.WriteAllText(Path.Combine(printingDir, "Printers.cs"), string.Join(Environment.NewLine, lines));
    }

}

