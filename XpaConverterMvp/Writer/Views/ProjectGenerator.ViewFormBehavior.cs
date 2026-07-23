using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    /// <summary>
    /// Emits the window behavior already normalized by the parser. Keep this in one
    /// place so the primary and additional displays follow the same WinForms contract.
    /// </summary>
    private static void EmitViewFormBehavior(StringBuilder designer, TaskFormDef form)
    {
        // Magic WindowType 9 is Fit-to-MDI. FitToMDI is also what makes the
        // runtime attach the task form to the active MDI container.
        if (string.Equals(form.WindowType, "9", StringComparison.OrdinalIgnoreCase))
            designer.AppendLine("        FitToMDI = true;");

        // A Fit-to-MDI window fills the MDI client area. StartupMode 2 is the
        // explicit maximized startup mode used by other window types.
        if (string.Equals(form.WindowType, "9", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(form.StartupMode, "2", StringComparison.OrdinalIgnoreCase))
            designer.AppendLine("        WindowState = System.Windows.Forms.FormWindowState.Maximized;");

        if (form.TitleBar == false)
            designer.AppendLine("        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;");

        if (form.MaximizeButton.HasValue)
            designer.AppendLine($"        MaximizeBox = {(form.MaximizeButton.Value ? "true" : "false")};");

        if (form.MinimizeButton.HasValue)
            designer.AppendLine($"        MinimizeBox = {(form.MinimizeButton.Value ? "true" : "false")};");

        if (form.SystemMenu.HasValue)
            designer.AppendLine($"        ControlBox = {(form.SystemMenu.Value ? "true" : "false")};");
    }

    private static void EmitViewControlPlacement(StringBuilder designer, TaskFormControlDef control, string variableName)
    {
        var anchors = new List<string>();

        if (control.PlacementX.GetValueOrDefault() < 100)
            anchors.Add("System.Windows.Forms.AnchorStyles.Left");
        if (control.PlacementX.GetValueOrDefault() > 0 || control.PlacementWidth.GetValueOrDefault() > 0)
            anchors.Add("System.Windows.Forms.AnchorStyles.Right");
        if (control.PlacementY.GetValueOrDefault() < 100)
            anchors.Add("System.Windows.Forms.AnchorStyles.Top");
        if (control.PlacementY.GetValueOrDefault() > 0 || control.PlacementHeight.GetValueOrDefault() > 0)
            anchors.Add("System.Windows.Forms.AnchorStyles.Bottom");

        if (anchors.Count > 0 &&
            (control.PlacementX.GetValueOrDefault() != 0 ||
             control.PlacementWidth.GetValueOrDefault() != 0 ||
             control.PlacementY.GetValueOrDefault() != 0 ||
             control.PlacementHeight.GetValueOrDefault() != 0))
            designer.AppendLine($"        {variableName}.Anchor = {string.Join(" | ", anchors)};");
    }
}
