using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteThemeControls(string controlsDir, string appNamespace)
    {
        var controls = new (string Name, string BaseType)[]
        {
            ("CompatibleForm", "ENV.UI.Form"),
            ("CompatibleGrid", "ENV.UI.Grid"),
            ("CompatibleGridColumn", "ENV.UI.GridColumn"),
            ("SubForm", "ENV.UI.SubForm"),
            ("CompatibleTextBox", "ENV.UI.TextBox"),
            ("CompatibleLabel", "ENV.UI.Label"),
            ("ComboBox", "ENV.UI.ComboBox"),
            ("Button", "ENV.UI.Button"),
            ("WebBrowser", "ENV.UI.WebBrowser"),
            ("GroupBox", "ENV.UI.GroupBox"),
            ("Shape", "ENV.UI.Shape"),
            ("Line", "XPARuntimeCore.Box.UI.Line")
        };

        foreach (var c in controls)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"namespace {appNamespace}.Shared.Theme.Controls;");
            sb.AppendLine();
            sb.AppendLine($"public class {c.Name} : {c.BaseType}");
            sb.AppendLine("{");
            sb.AppendLine($"    public {c.Name}()");
            sb.AppendLine("    {");
            if (c.Name == "CompatibleGrid")
            {
                sb.AppendLine("        GridColumnType = typeof(CompatibleGridColumn);");
                sb.AppendLine("        DefaultTextBoxType = typeof(CompatibleTextBox);");
            }
            if (c.Name == "WebBrowser")
            {
                sb.AppendLine("        if (!DesignMode)");
                sb.AppendLine("            FixedBackColorInNonFlatStyles = ENV.UserSettings.FixedBackColorInNonFlatStyles;");
            }
            sb.AppendLine("    }");
            if (c.Name == "WebBrowser")
            {
                sb.AppendLine();
                sb.AppendLine("    public bool ScriptErrorsSuppressed { get; set; }");
                sb.AppendLine();
                sb.AppendLine("    public void Navigate(string target)");
                sb.AppendLine("    {");
                sb.AppendLine("        LoadPathOrHtml(target);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    public void Navigate2(string target)");
                sb.AppendLine("    {");
                sb.AppendLine("        LoadPathOrHtml(target);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    void LoadPathOrHtml(string target)");
                sb.AppendLine("    {");
                sb.AppendLine("        if (string.IsNullOrWhiteSpace(target))");
                sb.AppendLine("        {");
                sb.AppendLine("            DocumentText = string.Empty;");
                sb.AppendLine("            return;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        var normalized = target.Trim().Trim('\"');");
                sb.AppendLine("        DocumentText = System.IO.File.Exists(normalized) ? System.IO.File.ReadAllText(normalized) : normalized;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(controlsDir, $"{c.Name}.cs"), sb.ToString());
        }
    }

}

