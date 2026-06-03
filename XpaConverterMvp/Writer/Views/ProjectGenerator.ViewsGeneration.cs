using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteViews(
        ProjectSemantic parsed,
        IReadOnlyList<TaskSemantic> tasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<ControlButtonModelDef> buttonModels,
        string viewsDir,
        string appNamespace,
        TaskSemantic? mainTask,
        bool includeApplicationView = true)
    {
        WriteViewSupportControls(parsed, viewsDir, appNamespace, buttonModels, tasks);
        var outputRoot = Directory.GetParent(viewsDir)?.FullName ?? viewsDir;

        foreach (var t in tasks.Where(t => !t.MainProgram && !IsStructuralTask(t)).Where(ShouldGenerateView))
        {
            var taskFolder = ResolveEffectiveTaskOutputFolder(t, tasks);
            var taskViewsDir = string.IsNullOrWhiteSpace(taskFolder)
                ? viewsDir
                : Path.Combine(outputRoot, taskFolder, "Views");
            Directory.CreateDirectory(taskViewsDir);
            var viewClass = t.View.ClassName;
            var viewStopwatch = Stopwatch.StartNew();
            LogProgress($"View start: {viewClass} [{t.Description}]");
            var controllerType = ResolveTaskTypeReference(t, tasks);
            var clickHandlers = t.View.ClickHandlers;
            var subformBindings = t.View.SubformBindings;
            var bindListHandlers = t.View.BindListHandlers;
            var booleanBindings = t.View.BooleanBindings;
            var selectedViewFormEntry = t.View.SelectedFormEntry;
            var selectedViewForm = t.View.SelectedForm;
            var formTitleExpressionId = t.View.SelectedFormTextExpressionId;
            var bindFormTitle = formTitleExpressionId.HasValue;
            var formLeftExpressionId = selectedViewForm?.XExpressionId;
            var formTopExpressionId = selectedViewForm?.YExpressionId;
            var bindFormLeft = formLeftExpressionId.HasValue;
            var bindFormTop = formTopExpressionId.HasValue;
            string? resolvedFormTitleLiteralCode = null;
            if (!string.IsNullOrWhiteSpace(t.View.SelectedFormText))
            {
                resolvedFormTitleLiteralCode = ToCSharpLiteral(t.View.SelectedFormText!);
            }
            else if (formTitleExpressionId.HasValue)
            {
                if (TryResolveExpressionAsStringLiteralCode(t, formTitleExpressionId.Value, dataObjects, out var titleLiteralCode))
                    resolvedFormTitleLiteralCode = titleLiteralCode;
            }
            var allFormControls = t.View.SelectedFormControls.ToList();
            var unsupportedControls = t.View.SelectedUnsupportedControls.ToList();
            var controls = t.View.SelectedSupportedControls.ToList();
            var controlById = controls.ToDictionary(c => c.Id);
            var staticContainerIds = t.View.StaticContainerIds;
            var viewVarNames = t.View.ControlVariableNameById;
            string Var(TaskFormControlDef c) => viewVarNames.TryGetValue(c.Id, out var v) ? v : ResolveViewControlVariableName(c, staticContainerIds);
            var tables = t.View.TableControls.ToList();
            var tableColumns = t.View.TableColumnControls.ToList();
            var leafControls = t.View.LeafControls.ToList();
            var containerControls = t.View.ContainerControls.ToList();
            var controlTypeNameById = t.View.ControlTypeNameById;
            var selectMap = BuildSelectNameToExpressionMap(t, dataObjects);
            var tableAttachmentByLeaf = t.View.TableAttachmentByLeaf.ToDictionary(x => x.Key, x => x.Value);
            var columnAttachmentByLeaf = t.View.ColumnAttachmentByLeaf.ToDictionary(x => x.Key, x => x.Value);
            var columnChildIdsByColumn = t.View.ColumnChildIdsByColumn.ToDictionary(x => x.Key, x => x.Value);
            var tableChildIdsByTable = t.View.TableChildIdsByTable.ToDictionary(x => x.Key, x => x.Value);
            var rootControlIds = t.View.RootControlIds.ToList();
            var groupBoxBindingByControlId = t.View.GroupBoxBindingByControlId.ToDictionary(x => x.Key, x => x.Value);
            var tableColumnStartX = t.View.TableColumnStartXByTableId.ToDictionary(
                x => x.Key,
                x => x.Value.ToDictionary(y => y.Key, y => y.Value));
            var usedViewHandlerNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in clickHandlers)
                usedViewHandlerNames.Add(h.HandlerName);
            foreach (var h in bindListHandlers)
                usedViewHandlerNames.Add(h.HandlerName);
            foreach (var expId in booleanBindings.Select(x => x.ExpressionId).Distinct())
                usedViewHandlerNames.Add($"Exp_{expId}_Bindings");
            if (bindFormTitle)
                usedViewHandlerNames.Add("this_BindText");
            if (bindFormLeft)
                usedViewHandlerNames.Add("this_BindLeft");
            if (bindFormTop)
                usedViewHandlerNames.Add("this_BindTop");

            var controlHandlerBindings = new List<(int ControlId, string EventName, string MethodName, string WrapperName)>();
            foreach (var control in controls)
            {
                foreach (var controlHandler in t.HandlersSemantic.Items.Where(h =>
                             string.Equals(h.Level, "C", StringComparison.OrdinalIgnoreCase) &&
                             h.Type is "P" or "S" or "V" &&
                             DoesControlMatchHandlerReference(h, control, t, tasks, dataObjects)))
                {
                    var eventName = ResolveControlHandlerEventName(controlHandler);
                    if (string.IsNullOrWhiteSpace(eventName))
                        continue;

                    var methodName = ResolveControlHandlerMethodName(controlHandler, t, tasks, dataObjects);
                    var wrapperName = BuildUniqueViewControlEventWrapperName(Var(control), eventName, methodName, usedViewHandlerNames);
                    controlHandlerBindings.Add((control.Id, eventName, methodName, wrapperName));
                }
            }

            var code = new StringBuilder();
            code.AppendLine($"using {appNamespace}.Shared.Theme;");
            code.AppendLine("using XPARuntimeCore.Box;");
            code.AppendLine("using XPARuntimeCore.Box.UI.Advanced;");
            code.AppendLine();
            code.AppendLine($"namespace {appNamespace}.Views;");
            code.AppendLine();
            code.AppendLine($"public partial class {viewClass} : Shared.Theme.Controls.CompatibleForm");
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
            foreach (var s in subformBindings)
            {
                var control = controls.FirstOrDefault(c => c.Id == s.ControlId);
                if (control is null)
                    continue;
                var controlVar = Var(control);
                code.AppendLine($"        {controlVar}.SetController(_controller.{s.FieldName}, _controller.{s.MethodName});");
            }
            foreach (var control in controls.Where(c => string.Equals(c.Model, "CTRL_GUI0_TREE", StringComparison.OrdinalIgnoreCase)))
                EmitTreeControlInitialization(code, control, Var(control), t, tasks, dataObjects);
            code.AppendLine("    }");
            foreach (var h in clickHandlers)
            {
                var raiseExpression = CanonicalizeViewRaiseExpression(h.RaiseExpression, t);
                if (controlById.TryGetValue(h.ControlId, out var clickControl))
                {
                    var canonicalRaiseExpression = ResolveViewControlRaiseExpression(clickControl, t, buttonModels);
                    if (!string.IsNullOrWhiteSpace(canonicalRaiseExpression))
                        raiseExpression = CanonicalizeViewRaiseExpression(canonicalRaiseExpression, t);
                }
                var clickArgs = ResolveArgumentExpressions(h.ArgumentDefs, h.Arguments, t, dataObjects, selectMap, tasks);
                clickArgs = clickArgs
                    .Select(arg => PrefixControllerReferencesForView(arg, t, dataObjects))
                    .ToList();
                var raiseCall = clickArgs.Count == 0
                    ? raiseExpression
                    : $"{raiseExpression}, {string.Join(", ", clickArgs)}";
                code.AppendLine();
                code.AppendLine($"    void {h.HandlerName}(object sender, ButtonClickEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Raise({raiseCall});");
                code.AppendLine("    }");
            }
            if (bindFormTitle && formTitleExpressionId.HasValue)
            {
                code.AppendLine();
                code.AppendLine("    void this_BindText(object sender, StringBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = _controller.Exp_{formTitleExpressionId.Value}();");
                code.AppendLine("    }");
            }
            if (bindFormLeft && formLeftExpressionId.HasValue)
            {
                code.AppendLine();
                code.AppendLine("    void this_BindLeft(object sender, IntBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = _controller.Exp_{formLeftExpressionId.Value}() * HorizontalScale;");
                code.AppendLine("    }");
            }
            if (bindFormTop && formTopExpressionId.HasValue)
            {
                code.AppendLine();
                code.AppendLine("    void this_BindTop(object sender, IntBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = _controller.Exp_{formTopExpressionId.Value}() * VerticalScale;");
                code.AppendLine("    }");
            }
            foreach (var h in bindListHandlers)
            {
                code.AppendLine();
                code.AppendLine($"    void {h.HandlerName}(object sender, System.EventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        var {h.EntityVarName} = new Models.{h.EntityTypeName}();");
                code.AppendLine($"        {h.ComboVarName}.ListSource = {h.EntityVarName};");
                if (!string.IsNullOrWhiteSpace(h.ValueColumnName))
                    code.AppendLine($"        {h.ComboVarName}.ValueColumn = {h.EntityVarName}.{h.ValueColumnName};");
                if (!string.IsNullOrWhiteSpace(h.DisplayColumnName))
                    code.AppendLine($"        {h.ComboVarName}.DisplayColumn = {h.EntityVarName}.{h.DisplayColumnName};");
                if (!string.IsNullOrWhiteSpace(h.OrderByName))
                    code.AppendLine($"        {h.ComboVarName}.ListOrderBy = {h.EntityVarName}.{h.OrderByName};");
                code.AppendLine("    }");
            }
            foreach (var expId in booleanBindings.Select(x => x.ExpressionId).Distinct().OrderBy(x => x))
            {
                code.AppendLine();
                code.AppendLine($"    void Exp_{expId}_Bindings(object sender, XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Value = {ResolveBooleanBindingExpression(t, expId)};");
                code.AppendLine("    }");
            }
            foreach (var binding in controlHandlerBindings)
            {
                code.AppendLine();
                code.AppendLine($"    void {binding.WrapperName}()");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        _controller.{binding.MethodName}();");
                code.AppendLine("    }");
            }
            var controllerBindingStatements = new List<string>();
            var designer = new StringBuilder();
            designer.AppendLine("using System.Drawing;");
            designer.AppendLine($"using {appNamespace}.Shared.Theme;");
            designer.AppendLine();
            designer.AppendLine($"namespace {appNamespace}.Views;");
            designer.AppendLine();
            designer.AppendLine($"partial class {viewClass}");
            designer.AppendLine("{");
            foreach (var c in controls)
            {
                var typeName = ResolveEffectiveViewControlTypeName(c, t, controlTypeNameById, buttonModels, staticContainerIds);
                var varName = Var(c);
                designer.AppendLine($"    {typeName} {varName};");
            }
            designer.AppendLine();
            designer.AppendLine("    void InitializeComponent()");
            designer.AppendLine("    {");
            designer.AppendLine("        SuspendLayout();");
            foreach (var uc in unsupportedControls)
                designer.AppendLine($"        // GAP: View control model '{uc.Model}' not mapped (ControlId={uc.Id}).");
            foreach (var c in controls)
            {
                var typeName = ResolveEffectiveViewControlTypeName(c, t, controlTypeNameById, buttonModels, staticContainerIds);
                var varName = Var(c);
                designer.AppendLine($"        {varName} = new {typeName}();");
            }

            foreach (var c in controls)
            {
                var typeName = ResolveEffectiveViewControlTypeName(c, t, controlTypeNameById, buttonModels, staticContainerIds);
                var varName = Var(c);
                var isNativeWinFormsControl = IsNativeWinFormsViewControl(c, typeName);
                var locationX = c.X;
                var locationY = c.Y;
                if (columnAttachmentByLeaf.TryGetValue(c.Id, out var parentColumnId))
                {
                    var parentColumn = controlById[parentColumnId];
                    var parentTable = parentColumn.ParentId.HasValue && controlById.TryGetValue(parentColumn.ParentId.Value, out var tCtrl) ? tCtrl : null;
                    if (parentTable is not null && tableColumnStartX.TryGetValue(parentTable.Id, out var starts) && starts.TryGetValue(parentColumnId, out var startX))
                    {
                        var titleHeight = parentTable.TitleHeight ?? 0;
                        locationX = Math.Max(0, c.X - startX);
                        locationY = Math.Max(1, c.Y - parentTable.Y - titleHeight);
                    }
                }
                else if (tableAttachmentByLeaf.TryGetValue(c.Id, out var parentTableId) && controlById.TryGetValue(parentTableId, out var parentTable))
                {
                    locationX = Math.Max(0, c.X - parentTable.X);
                    locationY = Math.Max(0, c.Y - parentTable.Y);
                }

                if (string.Equals(c.Model, "CTRL_GUI0_LINE", StringComparison.OrdinalIgnoreCase))
                {
                    designer.AppendLine($"        {varName}.Start = new Point({ScaleViewX(c.X)}, {ScaleViewY(c.Y)});");
                    designer.AppendLine($"        {varName}.End = new Point({ScaleViewX(c.Width)}, {ScaleViewY(c.Height)});");
                }
                else
                {
                    designer.AppendLine($"        {varName}.Location = new Point({ScaleViewX(locationX)}, {ScaleViewY(locationY)});");
                    designer.AppendLine($"        {varName}.Size = new Size({Math.Max(10, ScaleViewX(c.Width))}, {Math.Max(10, ScaleViewY(c.Height))});");
                }
                designer.AppendLine($"        {varName}.Name = \"{varName}\";");
                var tabIndex = c.TabOrder ?? c.TabbingOrder;
                if (tabIndex.HasValue)
                    designer.AppendLine($"        {varName}.TabIndex = {tabIndex.Value};");
                if (c.VisibleValue == false)
                    designer.AppendLine($"        {varName}.Visible = false;");
                if (c.EnabledValue == false)
                    designer.AppendLine($"        {varName}.Enabled = false;");
                else if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase) && c.EnabledExpressionId.HasValue)
                    designer.AppendLine($"        {varName}.Enabled = false;");
                if (!string.IsNullOrWhiteSpace(c.ControlName) && !IsTableColumnViewControl(c))
                    designer.AppendLine($"        {varName}.Tag = {ToCSharpLiteral(c.ControlName)};");
                if (c.ColorSchemeId.HasValue && !isNativeWinFormsControl)
                    designer.AppendLine($"        {varName}.ColorScheme = ColorSchemes.Find({c.ColorSchemeId.Value});");
                if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    if (c.ButtonStyleValue == 3)
                    {
                        designer.AppendLine($"        {varName}.Style = XPARuntimeCore.Box.UI.ButtonStyle.HyperLink;");
                        if (c.ColorSchemeId.HasValue)
                            designer.AppendLine($"        {varName}.HyperLinkColorScheme = ColorSchemes.Find({c.ColorSchemeId.Value});");
                    }
                    if (c.HoveringColorSchemeId.HasValue)
                        designer.AppendLine($"        {varName}.HyperLinkMouseEnterColorScheme = ColorSchemes.Find({c.HoveringColorSchemeId.Value});");
                    if (c.VisitedColorSchemeId.HasValue)
                        designer.AppendLine($"        {varName}.HyperLinkPressedColorScheme = ColorSchemes.Find({c.VisitedColorSchemeId.Value});");
                }
                if (c.FontSchemeId.HasValue && !isNativeWinFormsControl)
                    designer.AppendLine($"        {varName}.FontScheme = FontSchemes.Find({c.FontSchemeId.Value});");
                var alignmentExpr = ResolveViewAlignmentExpression(c) ?? ResolveInferredViewAlignmentExpression(c, t, dataObjects, tasks);
                if (!isNativeWinFormsControl && !string.IsNullOrWhiteSpace(alignmentExpr) && !IsTableViewControl(c) && !IsTableColumnViewControl(c))
                    designer.AppendLine($"        {varName}.Alignment = {alignmentExpr};");
                if (!isNativeWinFormsControl && SupportsViewControlStyle(c))
                {
                    var styleExpr = IsStaticGroupBoxLike(c, staticContainerIds)
                        ? "XPARuntimeCore.Box.UI.ControlStyle.Flat"
                        : ResolveViewControlStyleExpression(c);
                    if (!string.IsNullOrWhiteSpace(styleExpr))
                    {
                        designer.AppendLine($"        {varName}.Style = {styleExpr};");
                    }
                    else if ((c.StyleValue ?? c.InternalStyleValue).HasValue && !IsStaticGroupBoxLike(c, staticContainerIds))
                    {
                        designer.AppendLine($"        // GAP: View style not resolved for control {varName} (ControlId={c.Id}, Style={(c.StyleValue ?? c.InternalStyleValue)!.Value}).");
                    }
                }
                if (c.VerticalScroll.HasValue &&
                    (c.Model == "CTRL_GUI0_EDIT" || c.Model == "CTRL_RICH_CLIENT_EDIT" || c.Model == "CTRL_BROWSER_EDIT" || c.Model == "CTRL_GUI0_RICH_EDIT"))
                {
                    designer.AppendLine($"        {varName}.VerticalScroll = {(c.VerticalScroll.Value ? "true" : "false")};");
                }
                if (!string.IsNullOrWhiteSpace(c.Orientation))
                    designer.AppendLine($"        // GAP: View orientation not resolved for control {varName} (ControlId={c.Id}, Orientation={c.Orientation}).");
                int? groupBoxId = null;
                if (c.ParentId.HasValue &&
                    controlById.TryGetValue(c.ParentId.Value, out var declaredParent) &&
                    (IsStaticGroupBoxLike(declaredParent, staticContainerIds) || IsStaticShapeLike(declaredParent, staticContainerIds)))
                {
                    groupBoxId = declaredParent.Id;
                }
                else if (groupBoxBindingByControlId.TryGetValue(c.Id, out var inferredGroupBoxId))
                {
                    groupBoxId = inferredGroupBoxId;
                }
                if (!isNativeWinFormsControl && groupBoxId.HasValue && controlById.TryGetValue(groupBoxId.Value, out var groupControl))
                {
                    var groupVar = Var(groupControl);
                    designer.AppendLine($"        {varName}.BoundTo = new XPARuntimeCore.Box.UI.ControlBinding({groupVar});");
                }
                if (IsTableViewControl(c))
                {
                    if (c.LineDivider == true)
                        designer.AppendLine($"        {varName}.RowSeparators = true;");
                    if (c.ColumnDivider == true)
                        designer.AppendLine($"        {varName}.ColumnSeparators = true;");
                    if (string.Equals(c.SetTableColorBy, "2", StringComparison.OrdinalIgnoreCase))
                        designer.AppendLine($"        {varName}.RowColorStyle = XPARuntimeCore.Box.UI.GridRowColorStyle.AlternatingRowBackColor;");
                    if (c.AlternatingBgColor.HasValue)
                        designer.AppendLine($"        {varName}.AlternatingColorScheme = ColorSchemes.Find({c.AlternatingBgColor.Value});");
                    if (c.RowHeight.HasValue)
                        designer.AppendLine($"        {varName}.RowHeight = {Math.Max(1, ScaleViewY(c.RowHeight.Value))};");
                }
                else if (IsTableColumnViewControl(c))
                {
                    var colText = !string.IsNullOrWhiteSpace(c.ColumnTitle) ? c.ColumnTitle : c.Text;
                    if (!string.IsNullOrWhiteSpace(colText))
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(colText!)};");
                    designer.AppendLine($"        {varName}.Width = {Math.Max(10, ScaleViewX(c.Width))};");
                    if (c.Sortable == true)
                        designer.AppendLine($"        {varName}.AllowSort = true;");
                }
                else if (!string.IsNullOrWhiteSpace(c.Text))
                {
                    if (IsViewEditControl(c))
                    {
                        designer.AppendLine($"        {varName}.Format = {ToCSharpLiteral(c.Text)};");
                    }
                    else
                    {
                        var canUseRtfProperties =
                            !isNativeWinFormsControl &&
                            !typeName.EndsWith(".GroupBox", StringComparison.Ordinal);
                        if (canUseRtfProperties &&
                            (c.EnableRtf == true || c.Text.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase)))
                        {
                            designer.AppendLine($"        {varName}.Rtf = {ToCSharpLiteral(c.Text)};");
                            designer.AppendLine($"        {varName}.UseRtf = true;");
                        }
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.Text)};");
                    }
                }
                else if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    var buttonDesignText = ResolvePushButtonDesignText(c, t);
                    if (!string.IsNullOrWhiteSpace(buttonDesignText))
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(buttonDesignText)};");
                }
                if (IsViewEditControl(c) && c.MultiLineEdit == true)
                {
                    designer.AppendLine($"        {varName}.Multiline = true;");
                    designer.AppendLine($"        {varName}.AcceptsReturn = true;");
                }
                if (IsViewEditControl(c) && c.Modifiable == false)
                {
                    designer.AppendLine($"        {varName}.ReadOnly = true;");
                }
                if (ShouldAllowChangeInBrowseForViewControl(t, c))
                {
                    designer.AppendLine($"        {varName}.AllowChangeInBrowse = true;");
                }
                if (IsViewEditControl(c) && (c.TabInto == false || c.AllowParking == false))
                {
                    designer.AppendLine($"        {varName}.AllowFocus = false;");
                    designer.AppendLine($"        {varName}.TabStop = false;");
                }
                if (!isNativeWinFormsControl && !string.IsNullOrWhiteSpace(c.ToolTipText))
                {
                    designer.AppendLine($"        {varName}.ToolTip = {ToCSharpLiteral(c.ToolTipText)};");
                }
                else if (!isNativeWinFormsControl && c.ToolTipExpressionId.HasValue)
                {
                    if (TryResolveExpressionAsStringLiteralCode(t, c.ToolTipExpressionId.Value, dataObjects, out var tooltipLiteralCode))
                        designer.AppendLine($"        {varName}.ToolTip = {tooltipLiteralCode};");
                    else
                        designer.AppendLine($"        // GAP: View tooltip expression not resolved for control {varName} (ControlId={c.Id}, ToolTipExp={c.ToolTipExpressionId.Value}).");
                }

                var dataExpr = ResolveControlDataExpression(c, t, tasks, dataObjects);
                var hasXmlDataBindingHint = c.DataExpressionId.HasValue || !string.IsNullOrWhiteSpace(c.DataColumn);
                var didBindData = false;
                if (!string.IsNullOrWhiteSpace(dataExpr) &&
                    string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    var buttonDataExpr = NormalizePushButtonDirectDataAssignmentExpression($"_controller.{dataExpr}", t, tasks);
                    designer.AppendLine($"        {varName}.RaiseChangeOnClick = true;");
                    AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"{varName}.Data = {buttonDataExpr};");
                    didBindData = true;
                }
                else if (!string.IsNullOrWhiteSpace(dataExpr) && IsViewCheckBoxControl(c))
                {
                    var checkBoxDataExpr = NormalizeCheckBoxDirectDataAssignmentExpression($"_controller.{dataExpr}", t, tasks);
                    AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"{varName}.Data = {checkBoxDataExpr};");
                    didBindData = true;
                }
                else if (!string.IsNullOrWhiteSpace(dataExpr) && SupportsDirectViewDataAssignment(c))
                {
                    AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"{varName}.Data = _controller.{dataExpr};");
                    didBindData = true;
                }
                else
                {
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
                        didBindData = true;
                    }
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
                    didBindData = true;
                }
                if (hasXmlDataBindingHint && !didBindData)
                    designer.AppendLine($"        // GAP: View data binding not resolved for control {varName} (ControlId={c.Id}, DataExpressionId={c.DataExpressionId?.ToString() ?? "?"}, DataColumn={c.DataColumn ?? "?"}).");

                var click = clickHandlers.FirstOrDefault(x => x.ControlId == c.Id);
                if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase) &&
                    (click is not null || !string.IsNullOrWhiteSpace(c.RaiseEventType)))
                {
                    designer.AppendLine($"        {varName}.RaiseClickBeforeFocusChange = true;");
                }
                if (click is not null)
                    designer.AppendLine($"        {varName}.Click += new XPARuntimeCore.Box.UI.Advanced.ButtonClickEventHandler({click.HandlerName});");
                else if (!string.IsNullOrWhiteSpace(c.RaiseEventType))
                    designer.AppendLine($"        // GAP: View event binding not resolved for control {varName} (ControlId={c.Id}, RaiseEventType={c.RaiseEventType}, RaiseEventObject={c.RaiseEventObject ?? "?"}).");
                if (c.VisibleExpressionId.HasValue)
                    designer.AppendLine($"        {varName}.BindVisible += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs>(Exp_{c.VisibleExpressionId.Value}_Bindings);");
                if (c.EnabledExpressionId.HasValue)
                    designer.AppendLine($"        {varName}.BindEnabled += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs>(Exp_{c.EnabledExpressionId.Value}_Bindings);");
                var bind = bindListHandlers.FirstOrDefault(x => x.ControlId == c.Id);
                if (bind is not null)
                    designer.AppendLine($"        {varName}.BindListSource += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<System.EventArgs>({bind.HandlerName});");
                foreach (var binding in controlHandlerBindings.Where(x => x.ControlId == c.Id))
                {
                    designer.AppendLine($"        {varName}.{binding.EventName} += new System.Action({binding.WrapperName});");
                }
            }

            foreach (var col in tableColumns)
            {
                var colVar = Var(col);
                if (!columnChildIdsByColumn.TryGetValue(col.Id, out var childIds))
                    continue;
                foreach (var childId in childIds)
                {
                    if (!controlById.TryGetValue(childId, out var child))
                        continue;
                    var childVar = Var(child);
                    designer.AppendLine($"        {colVar}.Controls.Add({childVar});");
                }
            }

            foreach (var table in tables)
            {
                var tableVar = Var(table);
                if (!tableChildIdsByTable.TryGetValue(table.Id, out var childIds))
                    continue;
                foreach (var childId in childIds)
                {
                    if (!controlById.TryGetValue(childId, out var child))
                        continue;
                    var childVar = Var(child);
                    designer.AppendLine($"        {tableVar}.Controls.Add({childVar});");
                }
            }

            foreach (var controlId in rootControlIds)
            {
                if (!controlById.TryGetValue(controlId, out var c))
                    continue;
                var varName = Var(c);
                designer.AppendLine($"        Controls.Add({varName});");
            }

            if (!string.IsNullOrWhiteSpace(resolvedFormTitleLiteralCode))
            {
                designer.AppendLine($"        Text = {resolvedFormTitleLiteralCode};");
            }
            else
            {
                var formTextLiteral = !string.IsNullOrWhiteSpace(t.View.SelectedFormText)
                    ? t.View.SelectedFormText!
                    : t.Description;
                designer.AppendLine($"        Text = {ToCSharpLiteral(formTextLiteral)};");
            }
            designer.AppendLine("        AutoScaleDimensions = new SizeF(5F, 13F);");
            designer.AppendLine("        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;");
            designer.AppendLine($"        ClientSize = new Size({Math.Max(10, ScaleViewX(t.View.SelectedFormWidth))}, {Math.Max(10, ScaleViewY(t.View.SelectedFormHeight))});");
            if (selectedViewForm is not null)
                designer.AppendLine($"        Location = new Point({ScaleViewX(selectedViewForm.X)}, {ScaleViewY(selectedViewForm.Y)});");
            designer.AppendLine("        HorizontalExpressionFactor = 4D;");
            designer.AppendLine("        HorizontalScale = 5D;");
            if (t.View.SelectedFormColorSchemeId is int formColorId)
                designer.AppendLine($"        ColorScheme = ColorSchemes.Find({formColorId});");
            if (t.View.SelectedFormFontSchemeId is int formFontId)
                designer.AppendLine($"        FontScheme = FontSchemes.Find({formFontId});");
            if (bindFormTitle)
                designer.AppendLine("        BindText += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.StringBindingEventArgs>(this.this_BindText);");
            designer.AppendLine("        VerticalExpressionFactor = 8D;");
            designer.AppendLine("        VerticalScale = 13D;");
            if (bindFormLeft)
                designer.AppendLine("        BindLeft += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.IntBindingEventArgs>(this.this_BindLeft);");
            if (bindFormTop)
                designer.AppendLine("        BindTop += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.IntBindingEventArgs>(this.this_BindTop);");
            designer.AppendLine($"        Name = \"{viewClass}\";");
            designer.AppendLine("        ResumeLayout(false);");
            designer.AppendLine("    }");
            designer.AppendLine("}");
            File.WriteAllText(Path.Combine(taskViewsDir, $"{viewClass}.Designer.cs"), designer.ToString());
            LogProgress($"View designer done: {viewClass}");

            AppendViewControllerBindingsMethod(code, controllerBindingStatements);
            code.AppendLine("}");
            File.WriteAllText(Path.Combine(taskViewsDir, $"{viewClass}.cs"), code.ToString());
            LogProgress($"View code done: {viewClass}");

            WriteAdditionalMultiFormViews(t, tasks, dataObjects, buttonModels, taskViewsDir, appNamespace);
            LogProgress($"View done: {viewClass}");
            ConversionTelemetry.LogDuration("VIEW", viewClass, viewStopwatch.Elapsed, $"description={QuoteTelemetry(t.Description)}");
        }

        if (includeApplicationView && mainTask is not null)
        {
            var appViewClass = ResolveApplicationViewClassName(mainTask, appNamespace);
            var appViewStopwatch = Stopwatch.StartNew();
            LogProgress($"View start: {appViewClass} [Application]");
            var appViewText = !string.IsNullOrWhiteSpace(mainTask.FormName) ? mainTask.FormName! : mainTask.Description;
            var appView = new StringBuilder();
            appView.AppendLine($"using {appNamespace}.Shared.Theme;");
            appView.AppendLine();
            appView.AppendLine($"namespace {appNamespace}.Views;");
            appView.AppendLine();
            appView.AppendLine($"public partial class {appViewClass} : Shared.Theme.Controls.CompatibleForm");
            appView.AppendLine("{");
            appView.AppendLine("    readonly Application _controller;");
            appView.AppendLine($"    public {appViewClass}()");
            appView.AppendLine("    {");
            appView.AppendLine("        InitializeComponent();");
            appView.AppendLine("    }");
            appView.AppendLine();
            appView.AppendLine($"    internal {appViewClass}(Application controller)");
            appView.AppendLine("    {");
            appView.AppendLine("        _controller = controller;");
            appView.AppendLine("        InitializeComponent();");
            appView.AppendLine("    }");
            appView.AppendLine("}");
            File.WriteAllText(Path.Combine(viewsDir, $"{appViewClass}.cs"), appView.ToString());
            LogProgress($"View code done: {appViewClass}");

            var appDesigner = new StringBuilder();
            appDesigner.AppendLine("using System.Drawing;");
            appDesigner.AppendLine();
            appDesigner.AppendLine($"namespace {appNamespace}.Views;");
            appDesigner.AppendLine();
            appDesigner.AppendLine($"partial class {appViewClass}");
            appDesigner.AppendLine("{");
            appDesigner.AppendLine("    void InitializeComponent()");
            appDesigner.AppendLine("    {");
            appDesigner.AppendLine("        SuspendLayout();");
            appDesigner.AppendLine($"        Text = \"{Escape(appViewText)}\";");
            appDesigner.AppendLine("        ClientSize = new Size(900, 600);");
            appDesigner.AppendLine($"        Name = \"{appViewClass}\";");
            appDesigner.AppendLine("        ResumeLayout(false);");
            appDesigner.AppendLine("    }");
            appDesigner.AppendLine("}");
            File.WriteAllText(Path.Combine(viewsDir, $"{appViewClass}.Designer.cs"), appDesigner.ToString());
            LogProgress($"View designer done: {appViewClass}");
            LogProgress($"View done: {appViewClass}");
            ConversionTelemetry.LogDuration("VIEW", appViewClass, appViewStopwatch.Elapsed, "description=\"Application\"");
        }

        if (!includeApplicationView)
            WriteMissingScopedViewPlaceholders(tasks, viewsDir, appNamespace);
    }

    private static void WriteMissingScopedViewPlaceholders(
        IReadOnlyList<TaskSemantic> tasks,
        string viewsDir,
        string appNamespace)
    {
        var outputRoot = Directory.GetParent(viewsDir)?.FullName ?? viewsDir;
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => !t.MainProgram && !IsStructuralTask(t)))
        {
            foreach (var viewClass in EnumerateExpectedScopedViewClasses(task, tasks))
            {
                if (string.IsNullOrWhiteSpace(viewClass) ||
                    DoesGeneratedViewClassFileExist(outputRoot, viewClass))
                {
                    continue;
                }

                placeholders.TryAdd(viewClass, ResolveTaskTypeReference(task, tasks));
            }
        }

        var placeholderPath = Path.Combine(viewsDir, "RangeStubViews.cs");
        if (placeholders.Count == 0)
        {
            if (File.Exists(placeholderPath))
                File.Delete(placeholderPath);
            return;
        }

        var code = new StringBuilder();
        code.AppendLine($"using {appNamespace}.Shared.Theme;");
        code.AppendLine();
        code.AppendLine($"namespace {appNamespace}.Views;");
        code.AppendLine();
        foreach (var placeholder in placeholders.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            code.AppendLine($"public class {placeholder.Key} : Shared.Theme.Controls.CompatibleForm");
            code.AppendLine("{");
            code.AppendLine($"    public {placeholder.Key}()");
            code.AppendLine("    {");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine($"    internal {placeholder.Key}({placeholder.Value} controller)");
            code.AppendLine("    {");
            code.AppendLine("    }");
            code.AppendLine("}");
            code.AppendLine();
        }

        File.WriteAllText(placeholderPath, code.ToString());
        LogProgress($"Stage: scoped view placeholders -> {placeholders.Count}");
    }

    private static IEnumerable<string> EnumerateExpectedScopedViewClasses(TaskSemantic task, IReadOnlyList<TaskSemantic> tasks)
    {
        if (task.View.ShouldGenerate && !string.IsNullOrWhiteSpace(task.View.ClassName))
            yield return task.View.ClassName;

        foreach (var formEntry in task.FormEntries
                     .Where(fe => string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) && fe.Form is not null))
        {
            var viewClass = ResolveMultiFormViewClassName(task, formEntry, tasks);
            if (!string.IsNullOrWhiteSpace(viewClass))
                yield return viewClass;
        }
    }

    private static bool DoesGeneratedViewClassFileExist(string outputRoot, string viewClass)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(viewClass))
            return false;

        return Directory.EnumerateFiles(outputRoot, viewClass + ".cs", SearchOption.AllDirectories)
            .Any(path => !string.Equals(Path.GetFileName(path), "RangeStubViews.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendRuntimeControllerBindingStatement(ICollection<string> statements, string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return;

        statements.Add(statement.Trim());
    }

    private static void AppendViewControllerBindingsMethod(StringBuilder code, IReadOnlyCollection<string> statements)
    {
        code.AppendLine();
        code.AppendLine("    void InitializeControllerBindings()");
        code.AppendLine("    {");
        code.AppendLine("        if (_controller is null)");
        code.AppendLine("            return;");
        foreach (var statement in statements)
            code.AppendLine($"        {statement}");
        code.AppendLine("    }");
    }

    private static string BuildUniqueViewControlEventWrapperName(
        string controlVar,
        string eventName,
        string methodName,
        HashSet<string> usedNames)
    {
        var baseName = "On" + ToPascalIdentifier(controlVar) + ToPascalIdentifier(eventName);
        if (string.IsNullOrWhiteSpace(baseName) || string.Equals(baseName, "On", StringComparison.Ordinal))
            baseName = "On" + ToPascalIdentifier(methodName);

        var name = baseName;
        if (!usedNames.Add(name))
        {
            var methodBasedName = "On" + ToPascalIdentifier(controlVar) + ToPascalIdentifier(methodName);
            name = methodBasedName;
            if (!usedNames.Add(name))
            {
                var suffix = 2;
                do
                {
                    name = methodBasedName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    suffix++;
                }
                while (!usedNames.Add(name));
            }
        }

        return name;
    }
}
