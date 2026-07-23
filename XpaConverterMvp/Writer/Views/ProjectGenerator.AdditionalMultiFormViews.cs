using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteAdditionalMultiFormViews(
        TaskSemantic t,
        IReadOnlyList<TaskSemantic> tasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<ControlButtonModelDef> buttonModels,
        string taskViewsDir,
        string appNamespace)
    {
        var guiForms = t.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) && fe.Form is not null)
            .OrderBy(fe => fe.Index)
            .ToList();
        if (guiForms.Count <= 1)
            return;

        var selectedFormEntryIndex = t.View.SelectedFormEntry?.Index;
        foreach (var formEntry in guiForms.Where(fe => !selectedFormEntryIndex.HasValue || fe.Index != selectedFormEntryIndex.Value))
        {
            var selectedViewForm = formEntry.Form!;
            var viewClass = ResolveMultiFormViewClassName(t, formEntry, tasks);
            var controllerType = ResolveTaskTypeReference(t, tasks);
            var allFormControls = selectedViewForm.Controls
                .OrderBy(c => c.TabOrder ?? int.MaxValue)
                .ThenBy(c => c.Id)
                .ToList();
            var unsupportedControls = allFormControls.Where(c => !IsSupportedViewControl(c)).ToList();
            var controls = allFormControls.Where(IsSupportedViewControl).ToList();
            var staticContainerIds = controls
                .Where(c => string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
                .Where(c => controls.Any(ch => ch.ParentId == c.Id))
                .Select(c => c.Id)
                .ToHashSet();
            var viewVarNames = BuildUniqueViewVariableNames(controls, t, tasks, dataObjects, staticContainerIds);
            string Var(TaskFormControlDef c) => viewVarNames.TryGetValue(c.Id, out var v) ? v : ResolveViewControlVariableName(c, staticContainerIds);
            var controlById = controls.ToDictionary(c => c.Id);
            var controlTypeNameById = controls.ToDictionary(c => c.Id, c => ResolveViewControlTypeName(c, t, buttonModels, staticContainerIds));
            var controllerBindingStatements = new List<string>();

            var code = new StringBuilder();
            code.AppendLine($"using {appNamespace}.Shared.Theme;");
            code.AppendLine("using XPARuntimeCore.Box.UI.Advanced;");
            code.AppendLine();
            code.AppendLine($"namespace {appNamespace}.Views;");
            code.AppendLine();
            code.AppendLine("[System.ComponentModel.DesignerCategory(\"Form\")]");
            code.AppendLine($"public partial class {viewClass} : ENV.UI.Form");
            code.AppendLine("{");
            code.AppendLine($"    readonly {controllerType} _controller;");
            code.AppendLine($"    public {viewClass}()");
            code.AppendLine("    {");
            code.AppendLine("        InitializeComponent();");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine($"    internal {viewClass}({controllerType} controller)");
            code.AppendLine("    {");
            code.AppendLine("        _controller = controller;");
            code.AppendLine("        InitializeComponent();");
            code.AppendLine("        InitializeControllerBindings();");
            code.AppendLine("    }");
            if (selectedViewForm.XExpressionId.HasValue)
            {
                code.AppendLine();
                code.AppendLine("    void this_BindLeft(object sender, IntBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = _controller.Exp_{selectedViewForm.XExpressionId.Value}() * HorizontalScale;");
                code.AppendLine("    }");
            }
            if (selectedViewForm.YExpressionId.HasValue)
            {
                code.AppendLine();
                code.AppendLine("    void this_BindTop(object sender, IntBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = _controller.Exp_{selectedViewForm.YExpressionId.Value}() * VerticalScale;");
                code.AppendLine("    }");
            }
            var designer = new StringBuilder();
            designer.AppendLine("using System.Drawing;");
            designer.AppendLine($"using {appNamespace}.Shared.Theme;");
            designer.AppendLine();
            designer.AppendLine($"namespace {appNamespace}.Views;");
            designer.AppendLine();
            designer.AppendLine($"partial class {viewClass}");
            designer.AppendLine("{");
            foreach (var c in controls)
                designer.AppendLine($"    {controlTypeNameById[c.Id]} {Var(c)};");
            designer.AppendLine();
            designer.AppendLine("    void InitializeComponent()");
            designer.AppendLine("    {");
            designer.AppendLine("        SuspendLayout();");
            foreach (var uc in unsupportedControls)
                designer.AppendLine($"        // GAP: View control model '{uc.Model}' not mapped (ControlId={uc.Id}).");
            foreach (var c in controls)
                designer.AppendLine($"        {Var(c)} = new {controlTypeNameById[c.Id]}();");

            foreach (var c in controls)
            {
                var varName = Var(c);
                var typeName = controlTypeNameById[c.Id];
                var isNativeWinFormsControl = IsNativeWinFormsViewControl(c, typeName);
                designer.AppendLine($"        {varName}.Location = new Point({ScaleViewX(c.X)}, {ScaleViewY(c.Y)});");
                designer.AppendLine($"        {varName}.Size = new Size({Math.Max(10, ScaleViewX(c.Width))}, {Math.Max(10, ScaleViewY(c.Height))});");
                designer.AppendLine($"        {varName}.Name = \"{varName}\";");
                EmitViewControlPlacement(designer, c, varName);
                var tabIndex = c.TabOrder ?? c.TabbingOrder;
                if (tabIndex.HasValue)
                    designer.AppendLine($"        {varName}.TabIndex = {tabIndex.Value};");
                if (!string.IsNullOrWhiteSpace(c.ControlName) && !IsTableColumnViewControl(c))
                    designer.AppendLine($"        {varName}.Tag = {ToCSharpLiteral(c.ControlName)};");
                if (c.FontSchemeId.HasValue && !isNativeWinFormsControl)
                    designer.AppendLine($"        {varName}.FontScheme = FontSchemes.Find({c.FontSchemeId.Value});");
                var alignmentExpr = ResolveViewAlignmentExpression(c);
                if (!isNativeWinFormsControl && !string.IsNullOrWhiteSpace(alignmentExpr))
                    designer.AppendLine($"        {varName}.Alignment = {alignmentExpr};");
                if (!isNativeWinFormsControl && SupportsViewControlStyle(c))
                {
                    var styleExpr = ResolveViewControlStyleExpression(c);
                    if (!string.IsNullOrWhiteSpace(styleExpr))
                        designer.AppendLine($"        {varName}.Style = {styleExpr};");
                }
                if (!string.IsNullOrWhiteSpace(c.Text))
                {
                    if (IsViewEditControl(c))
                        designer.AppendLine($"        {varName}.Format = {ToCSharpLiteral(c.Text)};");
                    else
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.Text)};");
                }
                else if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    var buttonDesignText = ResolvePushButtonDesignText(c, t);
                    if (!string.IsNullOrWhiteSpace(buttonDesignText))
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(buttonDesignText)};");
                }
                var dataBindingExpr = ResolveControlDataExpressionBindingForView(c, t, dataObjects, tasks);
                if (!string.IsNullOrWhiteSpace(dataBindingExpr) && SupportsDirectViewDataAssignment(c))
                {
                    dataBindingExpr = NormalizeViewDataAssignmentExpression(c, dataBindingExpr, t, tasks);
                    dataBindingExpr = PrefixControllerReferencesForView(dataBindingExpr, t, dataObjects);
                    dataBindingExpr = EnsureControllerScopedViewBinding(dataBindingExpr);
                    if (dataBindingExpr.Contains("_controller.", StringComparison.Ordinal))
                        AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"{varName}.Data = {dataBindingExpr};");
                    else
                        designer.AppendLine($"        {varName}.Data = {dataBindingExpr};");
                }
                var hostResource = ResolveViewHostResource(c, t);
                if (hostResource is not null && IsDotNetTaskResource(hostResource))
                {
                    var hostMember = ResolveTaskResourceMemberName(t, hostResource);
                    AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"_controller.{hostMember} = {varName};");
                }
                var browserInit = BuildBrowserInitializationStatement(varName, typeName, c, t, dataObjects, tasks);
                if (!string.IsNullOrWhiteSpace(browserInit))
                {
                    if (browserInit.Contains("_controller.", StringComparison.Ordinal))
                        AppendRuntimeControllerBindingStatement(controllerBindingStatements, browserInit);
                    else
                        designer.AppendLine(browserInit);
                }
            }

            foreach (var c in controls)
                designer.AppendLine($"        Controls.Add({Var(c)});");
            designer.AppendLine($"        Text = {ToCSharpLiteral(selectedViewForm.FormText ?? selectedViewForm.FormName ?? t.Description)};");
            designer.AppendLine("        AutoScaleDimensions = new SizeF(5F, 13F);");
            designer.AppendLine("        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;");
            designer.AppendLine($"        ClientSize = new Size({Math.Max(10, ScaleViewX(selectedViewForm.Width))}, {Math.Max(10, ScaleViewY(selectedViewForm.Height))});");
            designer.AppendLine($"        Location = new Point({ScaleViewX(selectedViewForm.X)}, {ScaleViewY(selectedViewForm.Y)});");
            designer.AppendLine("        HorizontalExpressionFactor = 4D;");
            designer.AppendLine("        HorizontalScale = 5D;");
            EmitViewFormBehavior(designer, selectedViewForm);
            if (selectedViewForm.XExpressionId.HasValue)
                designer.AppendLine("        BindLeft += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.IntBindingEventArgs>(this.this_BindLeft);");
            if (selectedViewForm.YExpressionId.HasValue)
                designer.AppendLine("        BindTop += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.IntBindingEventArgs>(this.this_BindTop);");
            designer.AppendLine($"        Name = \"{viewClass}\";");
            designer.AppendLine("        VerticalExpressionFactor = 8D;");
            designer.AppendLine("        VerticalScale = 13D;");
            designer.AppendLine("        ResumeLayout(false);");
            designer.AppendLine("    }");
            designer.AppendLine("}");
            File.WriteAllText(Path.Combine(taskViewsDir, $"{viewClass}.Designer.cs"), designer.ToString());

            AppendViewControllerBindingsMethod(code, controllerBindingStatements);
            code.AppendLine("}");
            File.WriteAllText(Path.Combine(taskViewsDir, $"{viewClass}.cs"), code.ToString());
        }
    }
}

