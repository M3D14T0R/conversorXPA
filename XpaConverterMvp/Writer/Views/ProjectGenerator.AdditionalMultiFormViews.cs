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
            int ScaleViewX(int value) => ScaleViewXForForm(value, selectedViewForm);
            int ScaleViewY(int value) => ScaleViewYForForm(value, selectedViewForm);
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
            var tables = controls.Where(IsTableViewControl).ToList();
            var tableColumns = controls.Where(IsTableColumnViewControl).ToList();
            var leaves = controls.Where(IsLeafViewControl).ToList();
            var tableAttachmentByLeaf = new Dictionary<int, int>();
            var columnAttachmentByLeaf = new Dictionary<int, int>();
            var columnChildIdsByColumn = new Dictionary<int, IReadOnlyList<int>>();
            var tableChildIdsByTable = new Dictionary<int, IReadOnlyList<int>>();
            var tableColumnStartXByTableId = new Dictionary<int, IReadOnlyDictionary<int, int>>();

            foreach (var table in tables)
            {
                var columns = tableColumns
                    .Where(column => column.ParentId == table.Id)
                    .OrderBy(column => column.ControlLayer ?? int.MaxValue)
                    .ThenBy(column => column.Id)
                    .ToList();
                var startsByColumn = new Dictionary<int, int>();
                var cursorX = table.X;
                foreach (var column in columns)
                {
                    startsByColumn[column.Id] = cursorX;
                    cursorX += Math.Max(10, column.Width);
                }
                tableColumnStartXByTableId[table.Id] = startsByColumn;

                var columnByLayer = columns
                    .Where(column => column.ControlLayer.HasValue)
                    .GroupBy(column => column.ControlLayer!.Value)
                    .ToDictionary(group => group.Key, group => group.First());
                foreach (var leaf in leaves.Where(leaf => leaf.ParentId == table.Id))
                {
                    if (leaf.ControlLayer.HasValue &&
                        columnByLayer.TryGetValue(leaf.ControlLayer.Value, out var column))
                    {
                        columnAttachmentByLeaf[leaf.Id] = column.Id;
                    }
                    else
                    {
                        tableAttachmentByLeaf[leaf.Id] = table.Id;
                    }
                }
            }

            var tableColumnIds = tableColumns.Select(column => column.Id).ToHashSet();
            foreach (var leaf in leaves.Where(leaf =>
                         leaf.ParentId.HasValue &&
                         tableColumnIds.Contains(leaf.ParentId.Value)))
            {
                columnAttachmentByLeaf[leaf.Id] = leaf.ParentId!.Value;
            }

            foreach (var column in tableColumns)
            {
                columnChildIdsByColumn[column.Id] = leaves
                    .Where(leaf =>
                        columnAttachmentByLeaf.TryGetValue(leaf.Id, out var columnId) &&
                        columnId == column.Id)
                    .OrderBy(leaf => leaf.Id)
                    .Select(leaf => leaf.Id)
                    .ToList();
            }

            foreach (var table in tables)
            {
                var childIds = new List<int>();
                childIds.AddRange(tableColumns
                    .Where(column => column.ParentId == table.Id)
                    .OrderBy(column => column.ControlLayer ?? int.MaxValue)
                    .ThenBy(column => column.Id)
                    .Select(column => column.Id));
                childIds.AddRange(leaves
                    .Where(leaf =>
                        tableAttachmentByLeaf.TryGetValue(leaf.Id, out var tableId) &&
                        tableId == table.Id)
                    .OrderBy(leaf => leaf.Id)
                    .Select(leaf => leaf.Id));
                tableChildIdsByTable[table.Id] = childIds;
            }

            var containers = controls
                .Where(control =>
                    IsStaticGroupBoxLike(control, staticContainerIds) ||
                    IsStaticShapeLike(control, staticContainerIds))
                .ToList();
            var groupBoxBindingByControlId = new Dictionary<int, int>();
            foreach (var control in controls)
            {
                if (containers.Contains(control) ||
                    tableAttachmentByLeaf.ContainsKey(control.Id) ||
                    columnAttachmentByLeaf.ContainsKey(control.Id))
                {
                    continue;
                }

                if (control.ParentId.HasValue &&
                    controlById.TryGetValue(control.ParentId.Value, out var declaredParent) &&
                    containers.Contains(declaredParent))
                {
                    groupBoxBindingByControlId[control.Id] = declaredParent.Id;
                    continue;
                }

                if (!string.Equals(control.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
                    continue;

                TaskFormControlDef? best = null;
                var controlRight = control.X + Math.Max(1, control.Width);
                var controlBottom = control.Y + Math.Max(1, control.Height);
                foreach (var container in containers)
                {
                    var containerRight = container.X + Math.Max(1, container.Width);
                    var containerBottom = container.Y + Math.Max(1, container.Height);
                    if (control.X < container.X || control.Y < container.Y ||
                        controlRight > containerRight || controlBottom > containerBottom)
                    {
                        continue;
                    }

                    if (best is null ||
                        Math.Max(1, container.Width) * Math.Max(1, container.Height) <
                        Math.Max(1, best.Width) * Math.Max(1, best.Height))
                    {
                        best = container;
                    }
                }
                if (best is not null)
                    groupBoxBindingByControlId[control.Id] = best.Id;
            }

            var tabBindingByControlId =
                new Dictionary<int, (int TabControlId, int TabIndex)>();
            var tabControls = controls
                .Where(control => string.Equals(control.Model, "CTRL_GUI0_TAB", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var control in controls)
            {
                if (!control.ControlLayer.HasValue ||
                    control.ControlLayer.Value <= 0 ||
                    string.Equals(control.Model, "CTRL_GUI0_TAB", StringComparison.OrdinalIgnoreCase) ||
                    IsTableColumnViewControl(control) ||
                    tableAttachmentByLeaf.ContainsKey(control.Id) ||
                    columnAttachmentByLeaf.ContainsKey(control.Id) ||
                    groupBoxBindingByControlId.ContainsKey(control.Id))
                {
                    continue;
                }

                TaskFormControlDef? containingTab = null;
                var centerX = control.X + Math.Max(1, control.Width) / 2;
                var centerY = control.Y + Math.Max(1, control.Height) / 2;
                foreach (var tab in tabControls)
                {
                    if (centerX < tab.X || centerY < tab.Y ||
                        centerX > tab.X + Math.Max(1, tab.Width) ||
                        centerY > tab.Y + Math.Max(1, tab.Height))
                    {
                        continue;
                    }

                    if (containingTab is null ||
                        Math.Max(1, tab.Width) * Math.Max(1, tab.Height) <
                        Math.Max(1, containingTab.Width) * Math.Max(1, containingTab.Height))
                    {
                        containingTab = tab;
                    }
                }
                if (containingTab is not null)
                {
                    tabBindingByControlId[control.Id] =
                        (containingTab.Id, control.ControlLayer.Value - 1);
                }
            }

            var rootControlIds = controls
                .Where(control =>
                    !IsTableColumnViewControl(control) &&
                    !tableAttachmentByLeaf.ContainsKey(control.Id) &&
                    !columnAttachmentByLeaf.ContainsKey(control.Id))
                .OrderBy(control => control.TabOrder ?? control.TabbingOrder ?? int.MaxValue)
                .ThenBy(control => control.Id)
                .Select(control => control.Id)
                .ToList();
            var controllerBindingStatements = new List<string>();
            var selectMap = BuildSelectNameToExpressionMap(t, dataObjects);
            var clickHandlers = controls
                .Where(control => string.Equals(
                    control.Model,
                    "CTRL_GUI0_PUSH_BUTTON",
                    StringComparison.OrdinalIgnoreCase))
                .Select(control => new ViewClickHandler(
                    control.Id,
                    $"On{Var(control)}Click",
                    ResolveViewControlRaiseExpression(control, t, buttonModels),
                    control.SubformArguments,
                    control.SubformArgumentDefs))
                .Where(handler => !string.IsNullOrWhiteSpace(handler.RaiseExpression))
                .ToList();
            var booleanExpressionIds = controls
                .SelectMany(control => new[]
                {
                    control.VisibleExpressionId,
                    control.EnabledExpressionId
                })
                .Where(expressionId => expressionId.HasValue)
                .Select(expressionId => expressionId!.Value)
                .Distinct()
                .OrderBy(expressionId => expressionId)
                .ToList();
            var modifiableExpressionIds = controls
                .Where(IsViewEditControl)
                .Where(control => control.ModifiableExpressionId.HasValue)
                .Select(control => control.ModifiableExpressionId!.Value)
                .Distinct()
                .OrderBy(expressionId => expressionId)
                .ToList();
            var usedViewHandlerNames = new HashSet<string>(
                clickHandlers.Select(handler => handler.HandlerName),
                StringComparer.Ordinal);
            foreach (var expressionId in booleanExpressionIds)
                usedViewHandlerNames.Add($"Exp_{expressionId}_Bindings");
            foreach (var expressionId in modifiableExpressionIds)
                usedViewHandlerNames.Add($"Exp_{expressionId}_ModifiableBindings");
            var controlHandlerBindings =
                new List<(int ControlId, string EventName, string MethodName, string WrapperName)>();
            foreach (var control in controls)
            {
                foreach (var controlHandler in t.HandlersSemantic.Items.Where(handler =>
                             string.Equals(handler.Level, "C", StringComparison.OrdinalIgnoreCase) &&
                             handler.Type is "P" or "S" or "V" &&
                             DoesControlMatchHandlerReference(
                                 handler,
                                 control,
                                 t,
                                 tasks,
                                 dataObjects)))
                {
                    var eventName = ResolveControlHandlerEventName(controlHandler);
                    if (string.IsNullOrWhiteSpace(eventName))
                        continue;
                    var methodName =
                        ResolveControlHandlerMethodName(controlHandler, t, tasks, dataObjects);
                    var wrapperName = BuildUniqueViewControlEventWrapperName(
                        Var(control),
                        eventName,
                        methodName,
                        usedViewHandlerNames);
                    controlHandlerBindings.Add(
                        (control.Id, eventName, methodName, wrapperName));
                }
            }

            var code = new StringBuilder();
            code.AppendLine($"using {appNamespace}.Shared.Theme;");
            code.AppendLine("using XPARuntimeCore.Box;");
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
            foreach (var handler in clickHandlers)
            {
                var clickArguments = ResolveArgumentExpressions(
                        handler.ArgumentDefs,
                        handler.Arguments,
                        t,
                        dataObjects,
                        selectMap,
                        tasks)
                    .Select(argument =>
                        PrefixControllerReferencesForView(argument, t, dataObjects))
                    .ToList();
                var raiseCall = clickArguments.Count == 0
                    ? handler.RaiseExpression
                    : $"{handler.RaiseExpression}, {string.Join(", ", clickArguments)}";
                code.AppendLine();
                code.AppendLine(
                    $"    void {handler.HandlerName}(object sender, ButtonClickEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine($"        e.Raise({raiseCall});");
                code.AppendLine("    }");
            }
            foreach (var expressionId in booleanExpressionIds)
            {
                code.AppendLine();
                code.AppendLine(
                    $"    void Exp_{expressionId}_Bindings(object sender, BooleanBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine(
                    $"        e.Value = {ResolveBooleanBindingExpression(t, expressionId)};");
                code.AppendLine("    }");
            }
            foreach (var expressionId in modifiableExpressionIds)
            {
                code.AppendLine();
                code.AppendLine(
                    $"    void Exp_{expressionId}_ModifiableBindings(object sender, BooleanBindingEventArgs e)");
                code.AppendLine("    {");
                code.AppendLine("        if (_controller is null)");
                code.AppendLine("            return;");
                code.AppendLine(
                    $"        e.Value = !({ResolveBooleanBindingExpression(t, expressionId)});");
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
                var isPushButton = string.Equals(
                    c.Model,
                    "CTRL_GUI0_PUSH_BUTTON",
                    StringComparison.OrdinalIgnoreCase);
                var pushButtonDesignText = isPushButton
                    ? (!string.IsNullOrWhiteSpace(c.Text) ? c.Text : ResolvePushButtonDesignText(c, t, tasks))
                    : "";
                var pushButtonUsesStaticNullDisplay =
                    isPushButton &&
                    TryResolvePushButtonControlResource(c, t, tasks, out var pushButtonResource) &&
                    !string.IsNullOrWhiteSpace(pushButtonResource?.NullDisplayText);
                var locationX = c.X;
                var locationY = c.Y;
                if (columnAttachmentByLeaf.TryGetValue(c.Id, out var parentColumnId) &&
                    controlById.TryGetValue(parentColumnId, out var parentColumn) &&
                    parentColumn.ParentId.HasValue &&
                    controlById.TryGetValue(parentColumn.ParentId.Value, out var parentTable) &&
                    tableColumnStartXByTableId.TryGetValue(parentTable.Id, out var starts) &&
                    starts.TryGetValue(parentColumnId, out var startX))
                {
                    locationX = Math.Max(0, c.X - startX);
                    locationY = Math.Max(1, c.Y - parentTable.Y - (parentTable.TitleHeight ?? 0));
                }
                else if (tableAttachmentByLeaf.TryGetValue(c.Id, out var parentTableId) &&
                         controlById.TryGetValue(parentTableId, out var directlyAttachedTable))
                {
                    locationX = Math.Max(0, c.X - directlyAttachedTable.X);
                    locationY = Math.Max(0, c.Y - directlyAttachedTable.Y);
                }

                designer.AppendLine($"        {varName}.Location = new Point({ScaleViewX(locationX)}, {ScaleViewY(locationY)});");
                designer.AppendLine($"        {varName}.Size = new Size({Math.Max(10, ScaleViewX(c.Width))}, {Math.Max(10, ScaleViewY(c.Height))});");
                designer.AppendLine($"        {varName}.Name = \"{varName}\";");
                // Grid and GridColumn perform their own row/cell layout.
                if (!columnAttachmentByLeaf.ContainsKey(c.Id) &&
                    !tableAttachmentByLeaf.ContainsKey(c.Id))
                {
                    EmitViewControlPlacement(designer, c, varName);
                }
                var tabIndex = c.TabOrder ?? c.TabbingOrder;
                if (tabIndex.HasValue)
                    designer.AppendLine($"        {varName}.TabIndex = {tabIndex.Value};");
                if (c.VisibleValue == false)
                    designer.AppendLine($"        {varName}.Visible = false;");
                if (c.EnabledValue == false)
                    designer.AppendLine($"        {varName}.Enabled = false;");
                else if (isPushButton && c.EnabledExpressionId.HasValue)
                    designer.AppendLine($"        {varName}.Enabled = false;");
                if (!string.IsNullOrWhiteSpace(c.ControlName) && !IsTableColumnViewControl(c))
                    designer.AppendLine($"        {varName}.Tag = {ToCSharpLiteral(c.ControlName)};");
                if (c.ColorSchemeId.HasValue && !isNativeWinFormsControl)
                    AppendRuntimeControllerBindingStatement(
                        controllerBindingStatements,
                        $"{varName}.ColorScheme = ColorSchemes.Find({c.ColorSchemeId.Value});");
                if (isPushButton)
                {
                    if (c.ButtonStyleValue == 3)
                    {
                        designer.AppendLine(
                            $"        {varName}.Style = XPARuntimeCore.Box.UI.ButtonStyle.HyperLink;");
                        if (c.ColorSchemeId.HasValue)
                            AppendRuntimeControllerBindingStatement(
                                controllerBindingStatements,
                                $"{varName}.HyperLinkColorScheme = ColorSchemes.Find({c.ColorSchemeId.Value});");
                    }
                    if (c.HoveringColorSchemeId.HasValue)
                        AppendRuntimeControllerBindingStatement(
                            controllerBindingStatements,
                            $"{varName}.HyperLinkMouseEnterColorScheme = ColorSchemes.Find({c.HoveringColorSchemeId.Value});");
                    if (c.VisitedColorSchemeId.HasValue)
                        AppendRuntimeControllerBindingStatement(
                            controllerBindingStatements,
                            $"{varName}.HyperLinkPressedColorScheme = ColorSchemes.Find({c.VisitedColorSchemeId.Value});");
                }
                if (c.FontSchemeId.HasValue && !isNativeWinFormsControl)
                    AppendRuntimeControllerBindingStatement(
                        controllerBindingStatements,
                        $"{varName}.FontScheme = FontSchemes.Find({c.FontSchemeId.Value});");
                var alignmentExpr =
                    ResolveViewAlignmentExpression(c) ??
                    ResolveInferredViewAlignmentExpression(c, t, dataObjects, tasks);
                if (!isNativeWinFormsControl &&
                    !string.IsNullOrWhiteSpace(alignmentExpr) &&
                    !IsTableViewControl(c) &&
                    !IsTableColumnViewControl(c))
                    designer.AppendLine($"        {varName}.Alignment = {alignmentExpr};");
                if (!isNativeWinFormsControl && SupportsViewControlStyle(c))
                {
                    var styleExpr = ResolveViewControlStyleExpression(c);
                    if (!string.IsNullOrWhiteSpace(styleExpr))
                        designer.AppendLine($"        {varName}.Style = {styleExpr};");
                }
                if (!isNativeWinFormsControl &&
                    groupBoxBindingByControlId.TryGetValue(c.Id, out var groupBoxId) &&
                    controlById.TryGetValue(groupBoxId, out var groupBox))
                {
                    designer.AppendLine(
                        $"        {varName}.BoundTo = new XPARuntimeCore.Box.UI.ControlBinding({Var(groupBox)});");
                }
                else if (!isNativeWinFormsControl &&
                         tabBindingByControlId.TryGetValue(c.Id, out var tabBinding) &&
                         controlById.TryGetValue(tabBinding.TabControlId, out var tabControl))
                {
                    designer.AppendLine(
                        $"        {varName}.BoundTo = new XPARuntimeCore.Box.UI.ControlBinding({Var(tabControl)}, {tabBinding.TabIndex});");
                }
                EmitViewChoiceItems(designer, varName, c, t, tasks, dataObjects);
                if (IsTableViewControl(c))
                {
                    if (c.LineDivider == true)
                        designer.AppendLine($"        {varName}.RowSeparators = true;");
                    if (c.ColumnDivider == true)
                        designer.AppendLine($"        {varName}.ColumnSeparators = true;");
                    if (string.Equals(c.SetTableColorBy, "2", StringComparison.OrdinalIgnoreCase))
                        designer.AppendLine($"        {varName}.RowColorStyle = XPARuntimeCore.Box.UI.GridRowColorStyle.AlternatingRowBackColor;");
                    if (c.AlternatingBgColor.HasValue)
                        AppendRuntimeControllerBindingStatement(controllerBindingStatements, $"{varName}.AlternatingColorScheme = ColorSchemes.Find({c.AlternatingBgColor.Value});");
                    if (c.RowHeight.HasValue)
                        designer.AppendLine($"        {varName}.RowHeight = {Math.Max(1, ScaleViewY(c.RowHeight.Value))};");
                }
                else if (IsTableColumnViewControl(c))
                {
                    var colText = !string.IsNullOrWhiteSpace(c.ColumnTitle)
                        ? c.ColumnTitle
                        : !string.IsNullOrWhiteSpace(c.Text)
                            ? c.Text
                            : ResolveFallbackGridColumnTitle(c);
                    if (!string.IsNullOrWhiteSpace(colText))
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(colText!)};");
                    designer.AppendLine($"        {varName}.Width = {Math.Max(10, ScaleViewX(c.Width))};");
                    if (c.Sortable == true)
                        designer.AppendLine($"        {varName}.AllowSort = true;");
                }
                else if (!string.IsNullOrWhiteSpace(c.Text))
                {
                    if (IsViewEditControl(c))
                        designer.AppendLine($"        {varName}.Format = {ToCSharpLiteral(c.Text)};");
                    else
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(c.Text)};");
                }
                else if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(pushButtonDesignText))
                        designer.AppendLine($"        {varName}.Text = {ToCSharpLiteral(pushButtonDesignText)};");
                }
                if (string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase) &&
                    c.Text is not null &&
                    (c.Text.Contains('\r') || c.Text.Contains('\n')))
                    designer.AppendLine($"        {varName}.Multiline = true;");
                if (IsViewEditControl(c) && c.MultiLineEdit == true)
                {
                    designer.AppendLine($"        {varName}.Multiline = true;");
                    designer.AppendLine($"        {varName}.AcceptsReturn = true;");
                }
                if (IsViewEditControl(c) &&
                    c.Modifiable == false &&
                    !c.ModifiableExpressionId.HasValue)
                    designer.AppendLine($"        {varName}.ReadOnly = true;");
                if (IsViewEditControl(c) && c.ModifiableExpressionId.HasValue)
                    designer.AppendLine(
                        $"        {varName}.BindReadOnly += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs>(Exp_{c.ModifiableExpressionId.Value}_ModifiableBindings);");
                if (ShouldAllowChangeInBrowseForViewControl(t, c))
                    designer.AppendLine($"        {varName}.AllowChangeInBrowse = true;");
                if (IsViewEditControl(c) && (c.TabInto == false || c.AllowParking == false))
                {
                    designer.AppendLine($"        {varName}.AllowFocus = false;");
                    designer.AppendLine($"        {varName}.TabStop = false;");
                }
                if (!isNativeWinFormsControl && !string.IsNullOrWhiteSpace(c.ToolTipText))
                    designer.AppendLine(
                        $"        {varName}.ToolTip = {ToCSharpLiteral(c.ToolTipText)};");
                else if (!isNativeWinFormsControl && c.ToolTipExpressionId.HasValue)
                {
                    if (TryResolveExpressionAsStringLiteralCode(
                            t,
                            c.ToolTipExpressionId.Value,
                            dataObjects,
                            out var tooltipLiteralCode))
                        designer.AppendLine($"        {varName}.ToolTip = {tooltipLiteralCode};");
                    else
                        designer.AppendLine(
                            $"        // GAP: View tooltip expression not resolved for control {varName} (ControlId={c.Id}, ToolTipExp={c.ToolTipExpressionId.Value}).");
                }

                // ButtonData may raise BindText immediately from its Data setter.
                // Subscribe before the assignment so a static NullDisplay caption
                // is available on the first render as well as later refreshes.
                if (isPushButton && !string.IsNullOrWhiteSpace(pushButtonDesignText))
                {
                    var fallbackText = ToCSharpLiteral(pushButtonDesignText);
                    AppendRuntimeControllerBindingStatement(
                        controllerBindingStatements,
                        $"{varName}.BindText += (_, e) => {{ if (global::System.String.IsNullOrWhiteSpace(e.Value)) e.Value = {fallbackText}; }};");
                }

                var directDataExpression = ResolveControlDataExpression(c, t, tasks, dataObjects);
                var directDotNetResource = ResolveViewDotNetDataResource(
                    t,
                    directDataExpression,
                    c.DataColumn,
                    tasks);
                var dataBindingExpr = "";
                if (!string.IsNullOrWhiteSpace(directDataExpression) &&
                    SupportsDirectViewDataAssignment(c))
                {
                    var directControllerExpression = ScopeViewDataExpressionToController(
                        directDataExpression,
                        t,
                        dataObjects);
                    if (isPushButton)
                    {
                        dataBindingExpr = BuildPushButtonDirectDataAssignmentExpression(
                            directControllerExpression,
                            t,
                            tasks,
                            pushButtonDesignText);
                    }
                    else if (IsViewCheckBoxControl(c))
                    {
                        dataBindingExpr = BuildCheckBoxDirectDataAssignmentExpression(
                            directControllerExpression,
                            t,
                            tasks);
                    }
                    else if (IsViewImageControl(c))
                    {
                        dataBindingExpr = BuildImageDirectDataAssignmentExpression(
                            directControllerExpression,
                            t,
                            tasks);
                    }
                    else if (directDotNetResource is not null)
                    {
                        var objectType = NormalizeDotNetObjectType(directDotNetResource.ObjectType ?? "");
                        var attr = MapReturnTypeToSourceExpressionAttribute(NormalizeReturnTypeToken(objectType));
                        dataBindingExpr = BuildViewDataBindingExpression(
                            c,
                            attr,
                            $"() => {directControllerExpression}");
                    }
                    else
                    {
                        dataBindingExpr = directControllerExpression;
                    }
                }
                if (string.IsNullOrWhiteSpace(dataBindingExpr))
                    dataBindingExpr = ResolveControlDataExpressionBindingForView(c, t, dataObjects, tasks);
                if ((!pushButtonUsesStaticNullDisplay || !string.IsNullOrWhiteSpace(directDataExpression)) &&
                    !string.IsNullOrWhiteSpace(dataBindingExpr) &&
                    SupportsDirectViewDataAssignment(c))
                {
                    if (isPushButton)
                        designer.AppendLine($"        {varName}.RaiseChangeOnClick = true;");
                    dataBindingExpr = PrefixControllerReferencesForView(dataBindingExpr, t, dataObjects);
                    dataBindingExpr = EnsureControllerScopedViewBinding(dataBindingExpr);
                    dataBindingExpr = BuildResolvedViewDataAssignmentExpression(c, dataBindingExpr, t, tasks);
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
                var clickHandler =
                    clickHandlers.FirstOrDefault(handler => handler.ControlId == c.Id);
                if (isPushButton &&
                    (clickHandler is not null || !string.IsNullOrWhiteSpace(c.RaiseEventType)))
                    designer.AppendLine($"        {varName}.RaiseClickBeforeFocusChange = true;");
                if (clickHandler is not null)
                    designer.AppendLine(
                        $"        {varName}.Click += new XPARuntimeCore.Box.UI.Advanced.ButtonClickEventHandler({clickHandler.HandlerName});");
                else if (!string.IsNullOrWhiteSpace(c.RaiseEventType))
                    designer.AppendLine(
                        $"        // GAP: View event binding not resolved for control {varName} (ControlId={c.Id}, RaiseEventType={c.RaiseEventType}, RaiseEventObject={c.RaiseEventObject ?? "?"}).");
                if (c.VisibleExpressionId.HasValue)
                {
                    if (isNativeWinFormsControl)
                        AppendRuntimeControllerBindingStatement(
                            controllerBindingStatements,
                            $"{varName}.Visible = {ResolveBooleanBindingExpression(t, c.VisibleExpressionId.Value)};");
                    else
                        designer.AppendLine(
                            $"        {varName}.BindVisible += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs>(Exp_{c.VisibleExpressionId.Value}_Bindings);");
                }
                if (c.EnabledExpressionId.HasValue)
                {
                    if (isNativeWinFormsControl)
                        AppendRuntimeControllerBindingStatement(
                            controllerBindingStatements,
                            $"{varName}.Enabled = {ResolveBooleanBindingExpression(t, c.EnabledExpressionId.Value)};");
                    else
                        designer.AppendLine(
                            $"        {varName}.BindEnabled += new XPARuntimeCore.Box.UI.Advanced.BindingEventHandler<XPARuntimeCore.Box.UI.Advanced.BooleanBindingEventArgs>(Exp_{c.EnabledExpressionId.Value}_Bindings);");
                }
                foreach (var binding in controlHandlerBindings.Where(
                             binding => binding.ControlId == c.Id))
                {
                    if (string.Equals(
                            c.Model,
                            "CTRL_GUI0_STATIC",
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            binding.EventName,
                            "InputValidation",
                            StringComparison.Ordinal))
                    {
                        designer.AppendLine(
                            $"        // GAP: Static label does not expose InputValidation; handler {binding.WrapperName} retained on the controller.");
                    }
                    else if (isNativeWinFormsControl)
                    {
                        designer.AppendLine(
                            $"        {varName}.{binding.EventName} += (_, _) => {binding.WrapperName}();");
                    }
                    else
                    {
                        designer.AppendLine(
                            $"        {varName}.{binding.EventName} += new System.Action({binding.WrapperName});");
                    }
                }
            }

            foreach (var column in tableColumns)
            {
                if (!columnChildIdsByColumn.TryGetValue(column.Id, out var childIds))
                    continue;
                foreach (var childId in childIds)
                {
                    if (controlById.TryGetValue(childId, out var child))
                        designer.AppendLine($"        {Var(column)}.Controls.Add({Var(child)});");
                }
            }

            foreach (var table in tables)
            {
                if (!tableChildIdsByTable.TryGetValue(table.Id, out var childIds))
                    continue;
                foreach (var childId in childIds)
                {
                    if (controlById.TryGetValue(childId, out var child))
                        designer.AppendLine($"        {Var(table)}.Controls.Add({Var(child)});");
                }
            }

            foreach (var controlId in rootControlIds)
            {
                if (controlById.TryGetValue(controlId, out var control))
                    designer.AppendLine($"        Controls.Add({Var(control)});");
            }
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

