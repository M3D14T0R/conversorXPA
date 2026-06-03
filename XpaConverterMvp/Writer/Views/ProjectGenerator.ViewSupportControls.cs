using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteViewSupportControls(
        ProjectSemantic parsed,
        string viewsDir,
        string appNamespace,
        IReadOnlyList<ControlButtonModelDef> buttonModels,
        IReadOnlyList<TaskSemantic> tasks)
    {
        var controlsDir = Path.Combine(viewsDir, "Controls");
        Directory.CreateDirectory(controlsDir);

        var tableCode = new StringBuilder();
        tableCode.AppendLine("using XPARuntimeCore.Box.UI;");
        tableCode.AppendLine();
        tableCode.AppendLine($"namespace {appNamespace}.Views.Controls;");
        tableCode.AppendLine();
        tableCode.AppendLine("public partial class V9CompatibleDefaultTable : Shared.Theme.Controls.CompatibleGrid");
        tableCode.AppendLine("{");
        tableCode.AppendLine("    public V9CompatibleDefaultTable()");
        tableCode.AppendLine("    {");
        tableCode.AppendLine("        InitializeComponent();");
        tableCode.AppendLine("    }");
        tableCode.AppendLine("}");
        File.WriteAllText(Path.Combine(controlsDir, "V9CompatibleDefaultTable.cs"), tableCode.ToString());

        var tableDesigner = new StringBuilder();
        tableDesigner.AppendLine($"namespace {appNamespace}.Views.Controls;");
        tableDesigner.AppendLine();
        tableDesigner.AppendLine("partial class V9CompatibleDefaultTable");
        tableDesigner.AppendLine("{");
        tableDesigner.AppendLine("    void InitializeComponent()");
        tableDesigner.AppendLine("    {");
        tableDesigner.AppendLine("        ActiveRowStyle = XPARuntimeCore.Box.UI.GridActiveRowStyle.Border;");
        tableDesigner.AppendLine("        DrawPartialRow = false;");
        tableDesigner.AppendLine("    }");
        tableDesigner.AppendLine("}");
        File.WriteAllText(Path.Combine(controlsDir, "V9CompatibleDefaultTable.Designer.cs"), tableDesigner.ToString());

        var usedButtonModelObjs = parsed.UsedButtonModelObjectIds;

        foreach (var bm in buttonModels.Where(m => usedButtonModelObjs.Contains(m.Obj)))
        {
            var className = ToPascalIdentifier(bm.Name);
            var clickCommand = ResolveButtonModelCommandExpression(bm.InternalEventId);

            var code = new StringBuilder();
            code.AppendLine("using XPARuntimeCore.Box;");
            code.AppendLine("using XPARuntimeCore.Box.UI;");
            code.AppendLine();
            code.AppendLine($"namespace {appNamespace}.Views.Controls;");
            code.AppendLine();
            code.AppendLine($"[System.ComponentModel.Description(\"{Escape(bm.Name)}\")]");
            code.AppendLine($"public partial class {className} : Shared.Theme.Controls.Button");
            code.AppendLine("{");
            code.AppendLine($"    public {className}()");
            code.AppendLine("    {");
            code.AppendLine("        InitializeComponent();");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine("    void this_Click(object sender, XPARuntimeCore.Box.UI.Advanced.ButtonClickEventArgs e)");
            code.AppendLine("    {");
            if (!string.IsNullOrWhiteSpace(clickCommand))
                code.AppendLine($"        e.Raise({clickCommand});");
            else
                code.AppendLine($"        // InternalEventID={bm.InternalEventId?.ToString() ?? "?"} not mapped.");
            code.AppendLine("    }");
            code.AppendLine("}");
            File.WriteAllText(Path.Combine(controlsDir, $"{className}.cs"), code.ToString());

            var designerCode = new StringBuilder();
            designerCode.AppendLine("using XPARuntimeCore.Box.UI;");
            designerCode.AppendLine();
            designerCode.AppendLine($"namespace {appNamespace}.Views.Controls;");
            designerCode.AppendLine();
            designerCode.AppendLine($"partial class {className}");
            designerCode.AppendLine("{");
            designerCode.AppendLine("    void InitializeComponent()");
            designerCode.AppendLine("    {");
            designerCode.AppendLine("        this.ClickEventRegistrationErasesPreviouslyRegisteredClickHandlers = true;");
            if (bm.FontSchemeId.HasValue)
                designerCode.AppendLine($"        this.FontScheme = {appNamespace}.Shared.Theme.FontSchemes.Find({bm.FontSchemeId.Value});");
            if (!string.IsNullOrWhiteSpace(bm.Format))
                designerCode.AppendLine($"        this.Format = \"{Escape(bm.Format!)}\";");
            designerCode.AppendLine("        this.Click += new XPARuntimeCore.Box.UI.Advanced.ButtonClickEventHandler(this.this_Click);");
            designerCode.AppendLine("    }");
            designerCode.AppendLine("}");
            File.WriteAllText(Path.Combine(controlsDir, $"{className}.Designer.cs"), designerCode.ToString());
        }
    }
}

