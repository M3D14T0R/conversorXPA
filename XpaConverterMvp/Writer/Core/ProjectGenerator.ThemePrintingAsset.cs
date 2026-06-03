using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteThemePrinting(string printingDir, string appNamespace, bool hasThemeFonts, bool hasThemeColors)
    {
        var wrappers = new (string Name, string BaseType, string[] BodyLines)[]
        {
            ("Grid", "ENV.Printing.Grid", new[]
            {
                "        GridColumnType = typeof(GridColumn);",
                "        DefaultTextBoxType = typeof(TextBox);"
            }),
            ("GridColumn", "ENV.Printing.GridColumn", Array.Empty<string>()),
            ("GroupBox", "ENV.Printing.GroupBox", Array.Empty<string>()),
            ("Label", "ENV.Printing.Label", Array.Empty<string>()),
            ("Line", "XPARuntimeCore.Box.UI.Line", Array.Empty<string>()),
            ("PictureBox", "ENV.Printing.PictureBox", Array.Empty<string>()),
            ("ReportSection", "ENV.Printing.ReportSection", new[]
            {
                "        DefaultLabelType = typeof(Label);",
                "        DefaultTextBoxType = typeof(TextBox);"
            }),
            ("RichTextBox", "ENV.Printing.RichTextBox", Array.Empty<string>()),
            ("Shape", "ENV.Printing.Shape", Array.Empty<string>()),
            ("TextBox", "ENV.Printing.TextBox", new[]
            {
                "        if (!DesignMode)",
                "            FixedBackColorInNonFlatStyles = ENV.UserSettings.FixedBackColorInNonFlatStyles;"
            })
        };

        foreach (var wrapper in wrappers)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"namespace {appNamespace}.Shared.Theme.Printing;");
            sb.AppendLine();
            sb.AppendLine($"public partial class {wrapper.Name} : {wrapper.BaseType}");
            sb.AppendLine("{");
            sb.AppendLine($"    public {wrapper.Name}()");
            sb.AppendLine("    {");
            foreach (var line in wrapper.BodyLines)
                sb.AppendLine(line);
            sb.AppendLine("        InitializeComponent();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(printingDir, $"{wrapper.Name}.cs"), sb.ToString());

            var designer = new StringBuilder();
            designer.AppendLine($"namespace {appNamespace}.Shared.Theme.Printing;");
            designer.AppendLine();
            designer.AppendLine($"partial class {wrapper.Name}");
            designer.AppendLine("{");
            designer.AppendLine("    void InitializeComponent()");
            designer.AppendLine("    {");
            if (wrapper.Name == "TextBox")
            {
                if (hasThemeFonts)
                    designer.AppendLine($"        FontScheme = new {appNamespace}.Shared.Theme.Fonts.TableField();");
                if (hasThemeColors)
                    designer.AppendLine($"        ColorScheme = new {appNamespace}.Shared.Theme.Colors.WindowSDefault();");
                designer.AppendLine("        Style = XPARuntimeCore.Box.UI.ControlStyle.Flat;");
                designer.AppendLine("        Alignment = System.Drawing.ContentAlignment.MiddleLeft;");
                designer.AppendLine("        RightToLeftLayout = false;");
                designer.AppendLine("        RightToLeftByFormat = false;");
            }
            else if (wrapper.Name == "ReportLayout")
            {
                designer.AppendLine("        SectionType = typeof(ReportSection);");
            }
            designer.AppendLine("    }");
            designer.AppendLine("}");
            File.WriteAllText(Path.Combine(printingDir, $"{wrapper.Name}.Designer.cs"), designer.ToString());
        }

        var reportLayout = new StringBuilder();
        reportLayout.AppendLine($"namespace {appNamespace}.Shared.Theme.Printing;");
        reportLayout.AppendLine();
        reportLayout.AppendLine("public partial class ReportLayout : ENV.Printing.ReportLayout");
        reportLayout.AppendLine("{");
        reportLayout.AppendLine("    public ReportLayout()");
        reportLayout.AppendLine("    {");
        reportLayout.AppendLine("        InitializeComponent();");
        reportLayout.AppendLine("    }");
        reportLayout.AppendLine();
        reportLayout.AppendLine("    public ReportLayout(ENV.BusinessProcessBase controller) : base(controller)");
        reportLayout.AppendLine("    {");
        reportLayout.AppendLine("        InitializeComponent();");
        reportLayout.AppendLine("    }");
        reportLayout.AppendLine();
        reportLayout.AppendLine("    public ReportLayout(ENV.AbstractUIController controller) : base(controller)");
        reportLayout.AppendLine("    {");
        reportLayout.AppendLine("        InitializeComponent();");
        reportLayout.AppendLine("    }");
        reportLayout.AppendLine();
        reportLayout.AppendLine("    public ReportLayout(ENV.ApplicationControllerBase controller) : base(controller)");
        reportLayout.AppendLine("    {");
        reportLayout.AppendLine("        InitializeComponent();");
        reportLayout.AppendLine("    }");
        reportLayout.AppendLine("}");
        File.WriteAllText(Path.Combine(printingDir, "ReportLayout.cs"), reportLayout.ToString());

        var reportLayoutDesigner = new StringBuilder();
        reportLayoutDesigner.AppendLine($"namespace {appNamespace}.Shared.Theme.Printing;");
        reportLayoutDesigner.AppendLine();
        reportLayoutDesigner.AppendLine("partial class ReportLayout");
        reportLayoutDesigner.AppendLine("{");
        reportLayoutDesigner.AppendLine("    void InitializeComponent()");
        reportLayoutDesigner.AppendLine("    {");
        reportLayoutDesigner.AppendLine("        SectionType = typeof(ReportSection);");
        reportLayoutDesigner.AppendLine("    }");
        reportLayoutDesigner.AppendLine("}");
        File.WriteAllText(Path.Combine(printingDir, "ReportLayout.Designer.cs"), reportLayoutDesigner.ToString());
    }

}

