using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using XpaConverterMvp.TypeSystem;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveControlDataExpressionBinding(TaskFormControlDef c, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!c.DataExpressionId.HasValue)
            return "";
        if (!task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(c.DataExpressionId.Value, out var exp) || exp is null)
            return "";
        var code = ResolveTypedExpressionEntryCode(exp, task, dataObjects, CreateViewBindingEmissionContext(exp.Attribute)).Code;
        if (string.IsNullOrWhiteSpace(code))
            return "";
        return BuildViewDataBindingExpression(c, exp.Attribute, $"() => {code}");
    }

    private static string ResolveControlDataExpressionBindingForView(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (c.DataExpressionId.HasValue)
        {
            if (task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(c.DataExpressionId.Value, out var exp) && exp is not null)
            {
                if (CanInlineStringLiteralExpression(task, exp.Ordinal, dataObjects))
                {
                    TryResolveExpressionAsStringLiteralCode(task, exp.Ordinal, dataObjects, out var literalCode);
                    literalCode = EmitExpressionForContext(literalCode, task, CreateViewBindingEmissionContext(exp.Attribute));
                    return BuildViewDataBindingExpression(c, exp.Attribute, $"() => {literalCode}");
                }
                return BuildViewDataBindingExpression(c, exp.Attribute, $"_controller.Exp_{exp.Ordinal}");
            }
        }

        if (!string.IsNullOrWhiteSpace(c.DataColumn) &&
            TryResolvePushButtonControlResourceBinding(c, task, out var buttonResourceBinding))
        {
            return PrefixControllerReferencesForView(buttonResourceBinding, task, dataObjects);
        }

        var allowParentHintBinding = AllowsViewControlParentHintBinding(c);
        var hintedBinding = allowParentHintBinding
            ? ResolveParentModelBindingByControlHint(c, task, allTasks, dataObjects)
            : "";
        if (!string.IsNullOrWhiteSpace(hintedBinding))
        {
            var prefixedHint = PrefixControllerReferencesForView(hintedBinding, task, dataObjects);
            return prefixedHint;
        }

        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            var expr = "";
            expr = ResolveDataColumnSelectBinding(c.DataColumn, task, allTasks, dataObjects);
            if (string.IsNullOrWhiteSpace(expr))
                expr = ResolveSelectExpressionByName(c.DataColumn!, task, dataObjects);
            if (string.IsNullOrWhiteSpace(expr))
                expr = allowParentHintBinding ? ResolveParentModelBindingByControlHint(c, task, allTasks, dataObjects) : "";
            if (string.IsNullOrWhiteSpace(expr))
                expr = ResolveDataColumnOrdinalBinding(c.DataColumn, task, allTasks);
            if (string.IsNullOrWhiteSpace(expr))
            {
                task.ResourcesSemantic.ByName.TryGetValue(c.DataColumn, out var rc);
                if (rc is not null)
                    expr = ResolveTaskResourceMemberName(task, rc);
            }
            if (!string.IsNullOrWhiteSpace(expr))
            {
                var prefixed = PrefixControllerReferencesForView(expr, task, dataObjects);
                return prefixed;
            }
        }

        return "";
    }

    private static string BuildViewDataBindingExpression(TaskFormControlDef c, string? attr, string valueExpression)
    {
        if (string.IsNullOrWhiteSpace(valueExpression))
            return "";

        var trimmedValueExpression = valueExpression.Trim();
        if (string.Equals(c.Model, "CTRL_GUI0_PUSH_BUTTON", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmedValueExpression.StartsWith("() =>", StringComparison.Ordinal))
                return $"new XPARuntimeCore.Box.UI.Advanced.ButtonData({trimmedValueExpression})";

            return $"new XPARuntimeCore.Box.UI.Advanced.ButtonData(() => {trimmedValueExpression}())";
        }

        if (IsViewCheckBoxControl(c))
        {
            if (trimmedValueExpression.StartsWith("() =>", StringComparison.Ordinal))
                return $"new XPARuntimeCore.Box.UI.Advanced.CheckBoxData({trimmedValueExpression})";

            var booleanValue = XpaExpressionTypeMap.Convert(
                trimmedValueExpression,
                XpaType.Object,
                XpaType.Bool);
            return $"new XPARuntimeCore.Box.UI.Advanced.CheckBoxData(() => {booleanValue})";
        }

        var fromMethod = ResolveViewControlDataFactoryMethod(c, attr);
        if (string.IsNullOrWhiteSpace(fromMethod))
            return "";

        return $"{fromMethod}({trimmedValueExpression})";
    }

    private static string BuildViewDataAssignmentExpression(
        TaskFormControlDef c,
        string valueExpression,
        TaskSemantic? task = null,
        IReadOnlyList<TaskSemantic>? allTasks = null)
    {
        if (!IsViewCheckBoxControl(c) || string.IsNullOrWhiteSpace(valueExpression))
            return valueExpression;

        var trimmed = valueExpression.Trim();
        if (trimmed.StartsWith("_controller.", StringComparison.Ordinal) ||
            trimmed.StartsWith("_parent.", StringComparison.Ordinal) ||
            IsSimpleMemberAccess(trimmed))
        {
            if (task is not null && allTasks is not null)
                return BuildCheckBoxDirectDataAssignmentExpression(trimmed, task, allTasks);

            return trimmed;
        }

        if (trimmed.StartsWith("new XPARuntimeCore.Box.UI.Advanced.CheckBoxData(", StringComparison.Ordinal))
            return trimmed;

        if (trimmed.StartsWith("() =>", StringComparison.Ordinal))
            return $"new XPARuntimeCore.Box.UI.Advanced.CheckBoxData({trimmed})";

        var booleanValue = XpaExpressionTypeMap.Convert(
            trimmed,
            XpaType.Object,
            XpaType.Bool);
        return $"new XPARuntimeCore.Box.UI.Advanced.CheckBoxData(() => {booleanValue})";
    }

    private static string BuildCheckBoxDirectDataAssignmentExpression(
        string valueExpression,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(valueExpression))
            return valueExpression;

        var trimmed = valueExpression.Trim();
        var unscoped = trimmed;
        if (unscoped.StartsWith("_controller.", StringComparison.Ordinal))
            unscoped = unscoped["_controller.".Length..];

        if (IsLogicalViewDataExpression(unscoped, task, allTasks))
            return trimmed;

        return $"(XPARuntimeCore.Box.UI.Advanced.CheckBoxData){trimmed}";
    }

    private static string BuildPushButtonDirectDataAssignmentExpression(
        string valueExpression,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(valueExpression))
            return valueExpression;

        var trimmed = valueExpression.Trim();
        var unscoped = trimmed;
        if (unscoped.StartsWith("_controller.", StringComparison.Ordinal))
            unscoped = unscoped["_controller.".Length..];

        if (trimmed.StartsWith("new XPARuntimeCore.Box.UI.Advanced.ButtonData(", StringComparison.Ordinal) ||
            trimmed.StartsWith("(XPARuntimeCore.Box.UI.Advanced.ButtonData)", StringComparison.Ordinal))
            return trimmed;

        if (IsTextViewDataExpression(unscoped, task, allTasks))
            return trimmed;

        if (IsLogicalViewDataExpression(unscoped, task, allTasks))
            return $"(XPARuntimeCore.Box.UI.Advanced.ButtonData){trimmed}";

        return $"(XPARuntimeCore.Box.UI.Advanced.ButtonData){trimmed}";
    }

    private static string BuildImageDirectDataAssignmentExpression(
        string valueExpression,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(valueExpression))
            return valueExpression;

        var trimmed = valueExpression.Trim();
        if (trimmed.StartsWith("XPARuntimeCore.Box.UI.Advanced.ImageData.", StringComparison.Ordinal) ||
            trimmed.StartsWith("new XPARuntimeCore.Box.UI.Advanced.ImageData(", StringComparison.Ordinal) ||
            trimmed.StartsWith("(XPARuntimeCore.Box.UI.Advanced.ImageData)", StringComparison.Ordinal))
            return trimmed;

        var unscoped = trimmed;
        if (unscoped.StartsWith("_controller.", StringComparison.Ordinal))
            unscoped = unscoped["_controller.".Length..];

        if (IsBlobViewDataExpression(unscoped, task, allTasks))
            return $"XPARuntimeCore.Box.UI.Advanced.ImageData.FromByteArray(() => {trimmed})";

        var textValue = XpaExpressionTypeMap.Convert(
            trimmed,
            XpaType.Object,
            XpaType.Text);
        return $"XPARuntimeCore.Box.UI.Advanced.ImageData.FromText(() => {textValue})";
    }

    private static bool IsLogicalViewDataExpression(string expression, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
        => IsViewDataExpressionWithAttr(expression, task, allTasks, attr =>
            string.Equals(attr, "FIELD_BOOLEAN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attr, "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase));

    private static bool IsTextViewDataExpression(string expression, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
        => IsViewDataExpressionWithAttr(expression, task, allTasks, attr =>
            string.Equals(attr, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attr, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase));

    private static bool IsBlobViewDataExpression(string expression, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
        => IsViewDataExpressionWithAttr(expression, task, allTasks, attr =>
            string.Equals(attr, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attr, "FIELD_OBJECT", StringComparison.OrdinalIgnoreCase));

    private static bool IsViewDataExpressionWithAttr(
        string expression,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        Func<string, bool> attrPredicate)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var currentTask = task;
        var remaining = expression.Trim();
        while (remaining.StartsWith("_parent.", StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue)
                return false;
            var parent = GetTaskByOrdinal(currentTask.ParentOrdinal, allTasks);
            if (parent is null)
                return false;
            currentTask = parent;
            remaining = remaining["_parent.".Length..];
        }

        var member = remaining.Split('.')[0];
        foreach (var resource in currentTask.ResourcesSemantic.Ordered)
        {
            if (!string.Equals(ResolveTaskResourceMemberName(currentTask, resource), member, StringComparison.Ordinal))
                continue;

            var attr = NormalizeAttrObjKind(resource.AttrObj);
            return attrPredicate(attr);
        }

        var modelAttr = NormalizeAttrObjKind(ResolveModelColumnAttrObj(currentTask, remaining));
        return attrPredicate(modelAttr);
    }

    private static string ResolveViewControlDataFactoryMethod(TaskFormControlDef c, string? attr)
    {
        if (string.Equals(c.Model, "CTRL_GUI0_IMAGE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Model, "CTRL_GUI1_IMAGE", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(attr, "O", StringComparison.OrdinalIgnoreCase)
                ? "XPARuntimeCore.Box.UI.Advanced.ImageData.FromByteArray"
                : "XPARuntimeCore.Box.UI.Advanced.ImageData.FromText";
        }

        var fromMethod = attr switch
        {
            "N" => "FromNumber",
            "D" => "FromDate",
            "T" => "FromTime",
            "B" => "FromBool",
            _ => "FromText"
        };
        return $"XPARuntimeCore.Box.UI.Advanced.ControlData.{fromMethod}";
    }

    private static string? BuildBrowserInitializationStatement(
        string varName,
        string typeName,
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!string.Equals(c.Model, "CTRL_GUI0_BROWSER", StringComparison.OrdinalIgnoreCase) || !c.DataExpressionId.HasValue)
            return null;

        if (string.Equals(typeName, "Shared.Theme.Controls.WebBrowser", StringComparison.Ordinal))
        {
            var expCall = $"_controller.Exp_{c.DataExpressionId.Value}()";
            return $"        {varName}.LoadHtml(System.IO.File.Exists({expCall}) ? System.IO.File.ReadAllText({expCall}) : {expCall});";
        }

        var browserSourceExpr = ResolveControlDataExpressionBindingForView(c, task, dataObjects, allTasks);
        if (string.IsNullOrWhiteSpace(browserSourceExpr))
            return null;

        return $"        {varName}.Navigate({browserSourceExpr});";
    }

    private static string PrefixControllerReferencesForView(string code, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = code;
        result = Regex.Replace(result, @"^\s*_parent\b", "_controller._parent");
        var symbolBindings = GetViewPrefixSymbolBindings(task, dataObjects);

        result = Regex.Replace(
            result,
            @"(?<![A-Za-z0-9_\.])\b[A-Za-z_][A-Za-z0-9_]*\b",
            match => symbolBindings.TryGetValue(match.Value, out var replacement)
                ? replacement
                : match.Value,
            RegexOptions.CultureInvariant);

        // Some view bindings already arrive partially prefixed; keep the binding
        // stable instead of generating invalid chains like _controller._controller.X.
        result = result.Replace("_controller._controller.", "_controller.", StringComparison.Ordinal);
        result = result.Replace("_controller._controller._parent.", "_controller._parent.", StringComparison.Ordinal);

        if (!result.StartsWith("_controller.", StringComparison.Ordinal) &&
            IsSimpleMemberAccess(result))
        {
            var firstSegment = result.Split('.')[0];
            if (symbolBindings.TryGetValue(firstSegment, out var boundFirstSegment) &&
                boundFirstSegment.StartsWith("_controller.", StringComparison.Ordinal))
            {
                result = "_controller." + result;
            }
        }

        result = PrefixBareModelLikeControllerReferences(result);
        return result;
    }

    private static Dictionary<string, string> GetViewPrefixSymbolBindings(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_viewPrefixSymbolBindingMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var symbolBindings = new Dictionary<string, string>(StringComparer.Ordinal);

        void AddBindings(TaskSemantic sourceTask, string prefix)
        {
            foreach (var rc in sourceTask.ResourcesSemantic.Ordered)
            {
                var member = ResolveTaskResourceMemberName(sourceTask, rc);
                if (!symbolBindings.ContainsKey(member))
                    symbolBindings[member] = prefix + member;
            }

            foreach (var mm in BuildModelMembers(sourceTask, dataObjects))
            {
                if (!symbolBindings.ContainsKey(mm.MemberName))
                    symbolBindings[mm.MemberName] = prefix + mm.MemberName;
            }
        }

        AddBindings(task, "_controller.");

        var parentOrdinal = task.ParentOrdinal;
        var parentPrefix = "_controller._parent.";
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, _allTasks ?? Array.Empty<TaskSemantic>());
            if (parentTask is null)
                break;

            AddBindings(parentTask, parentPrefix);
            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "_parent.";
        }

        _viewPrefixSymbolBindingMapCache[task.Ordinal] = symbolBindings;
        return symbolBindings;
    }

    private static string PrefixBareModelLikeControllerReferences(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        return Regex.Replace(
            code,
            @"(?<![A-Za-z0-9_\.])(?<symbol>[A-Z][A-Z0-9_]*\d*)\.",
            match =>
            {
                var symbol = match.Groups["symbol"].Value;
                if (symbol is "ENV" or "SQL")
                    return match.Value;

                return $"_controller.{symbol}.";
            },
            RegexOptions.CultureInvariant);
    }

    private static string EnsureControllerScopedViewBinding(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var trimmed = code.Trim();
        if (trimmed.StartsWith("_controller.", StringComparison.Ordinal))
            return trimmed;
        if (trimmed.StartsWith("_parent.", StringComparison.Ordinal))
            return "_controller." + trimmed;
        if (IsSimpleMemberAccess(trimmed))
            return "_controller." + trimmed;
        return trimmed;
    }

    private static bool IsSupportedViewControl(TaskFormControlDef c)
    {
        return c.Model is
            "CTRL_GUI0_TABLE" or
            "CTRL_GUI0_COLUMN" or
            "CTRL_GUI0_STATIC" or
            "CTRL_GUI0_EDIT" or
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_GUI0_PUSH_BUTTON" or
            "CTRL_GUI0_SUBFORM" or
            "CTRL_GUI0_CHECKBOX" or
            "CTRL_GUI0_TAB" or
            "CTRL_GUI0_TREE" or
            "CTRL_GUI0_RADIO" or
            "CTRL_GUI0_IMAGE" or
            "CTRL_GUI1_IMAGE" or
            "CTRL_GUI0_LINE" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RICH_EDIT" or
            "CTRL_GUI0_DOTNET" or
            "CTRL_GUI0_BROWSER" or
            "CTRL_RICH_CLIENT_EDIT" or
            "CTRL_BROWSER_EDIT" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_RICH_CLIENT_CHECKBOX";
    }

    private static bool IsTableViewControl(TaskFormControlDef c)
    {
        return c.Model == "CTRL_GUI0_TABLE";
    }

    private static bool IsTableColumnViewControl(TaskFormControlDef c)
    {
        return c.Model == "CTRL_GUI0_COLUMN";
    }

    private static bool IsLeafViewControl(TaskFormControlDef c)
    {
        return c.Model is
            "CTRL_GUI0_STATIC" or
            "CTRL_GUI0_EDIT" or
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_GUI0_PUSH_BUTTON" or
            "CTRL_GUI0_SUBFORM" or
            "CTRL_GUI0_CHECKBOX" or
            "CTRL_GUI0_TAB" or
            "CTRL_GUI0_TREE" or
            "CTRL_GUI0_RADIO" or
            "CTRL_GUI0_IMAGE" or
            "CTRL_GUI1_IMAGE" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RICH_EDIT" or
            "CTRL_GUI0_DOTNET" or
            "CTRL_GUI0_BROWSER" or
            "CTRL_RICH_CLIENT_EDIT" or
            "CTRL_BROWSER_EDIT" or
            "CTRL_RICH_CLIENT_CHECKBOX";
    }

    private static string ResolveEffectiveViewControlTypeName(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyDictionary<int, string> precomputedTypeNameById,
        IReadOnlyList<ControlButtonModelDef> buttonModels,
        IReadOnlySet<int>? staticContainerIds = null)
    {
        if (string.Equals(c.Model, "CTRL_GUI0_DOTNET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Model, "CTRL_GUI0_BROWSER", StringComparison.OrdinalIgnoreCase))
            return ResolveViewControlTypeName(c, task, buttonModels, staticContainerIds);

        return precomputedTypeNameById.TryGetValue(c.Id, out var mappedTypeName)
            ? mappedTypeName
            : ResolveViewControlTypeName(c, task, buttonModels, staticContainerIds);
    }

    private static bool IsStaticGroupBoxLike(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (!string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (c.EnableRtf == true ||
            (!string.IsNullOrWhiteSpace(c.Text) && c.Text.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase)))
            return false;
        var style = c.StyleValue ?? c.InternalStyleValue;
        if (style == 5)
            return true;

        // Some converted projects use a "standard" static, empty caption control as
        // a visual container (group/panel) that owns inner controls (e.g., RTF remark labels).
        // Treat it as GroupBox to preserve BoundTo semantics from official conversion.
        if (style == 7 && string.IsNullOrWhiteSpace(c.Text))
            return true;

        // In several XML exports, a generic static box container is emitted with
        // StaticType=128 and empty caption; children bind to it via ISN_FATHER.
        return string.Equals(c.StaticType, "128", StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrWhiteSpace(c.Text);
    }

    private static bool IsStaticShapeLike(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (!string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsStaticGroupBoxLike(c, staticContainerIds))
            return false;
        if (!string.IsNullOrWhiteSpace(c.Text))
            return false;
        var style = c.StyleValue ?? c.InternalStyleValue;
        return style == 1 || !style.HasValue;
    }

    private static bool SupportsViewControlStyle(TaskFormControlDef c)
    {
        return c.Model is
            "CTRL_GUI0_STATIC" or
            "CTRL_GUI0_EDIT" or
            "CTRL_RICH_CLIENT_EDIT" or
            "CTRL_BROWSER_EDIT" or
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_GUI0_PUSH_BUTTON" or
            "CTRL_GUI0_COLUMN" or
            "CTRL_GUI0_TABLE" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RICH_EDIT";
    }

    private static bool IsViewEditControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI0_EDIT" or "CTRL_RICH_CLIENT_EDIT" or "CTRL_BROWSER_EDIT" or "CTRL_GUI0_RICH_EDIT";
    }

    private static bool IsViewCheckBoxControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI0_CHECKBOX" or "CTRL_RICH_CLIENT_CHECKBOX";
    }

    private static bool IsViewImageControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI0_IMAGE" or "CTRL_GUI1_IMAGE";
    }

    private static bool SupportsDirectViewDataAssignment(TaskFormControlDef c)
    {
        return c.Model is not "CTRL_GUI0_STATIC"
            and not "CTRL_GUI0_COLUMN"
            and not "CTRL_GUI0_SUBFORM"
            and not "CTRL_GUI0_TREE"
            and not "CTRL_GUI0_DOTNET"
            and not "CTRL_GUI0_BROWSER";
    }

    private static bool ShouldAllowChangeInBrowseForViewControl(TaskSemantic task, TaskFormControlDef c)
    {
        if (!string.Equals(task.Execution.Activity, "Activities.Browse", StringComparison.Ordinal))
            return false;

        if (c.Modifiable == false)
            return false;

        if (string.IsNullOrWhiteSpace(c.DataColumn))
            return false;

        return IsViewEditControl(c) || IsViewCheckBoxControl(c);
    }

    private static string ResolveViewControlRaiseExpression(TaskFormControlDef c, TaskSemantic task, IReadOnlyList<ControlButtonModelDef> buttonModels)
    {
        var raiseType = c.RaiseEventType?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(raiseType))
            return "";

        if (c.ModelRefObj.HasValue)
        {
            var model = buttonModels.FirstOrDefault(m => m.Obj == c.ModelRefObj.Value);
            if (model?.InternalEventId is int iid)
            {
                var mapped = ResolveCommandByInternalEventId(iid);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped!;
            }
        }

        if (raiseType == "U")
        {
            var referenceCandidates = new[]
            {
                c.ControlName?.Trim(),
                c.Text?.Trim()
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            var commandMap = BuildTaskCommandMemberMap(task);
            foreach (var reference in referenceCandidates)
            {
                if (commandMap.TryGetValue(reference, out var localCommand))
                    return $"_controller.{localCommand}";

                var normalizedReference = NormalizeCommandLookupKey(reference);
                if (string.IsNullOrWhiteSpace(normalizedReference))
                    continue;

                var matches = commandMap
                    .Where(kv => string.Equals(NormalizeCommandLookupKey(kv.Key), normalizedReference, StringComparison.Ordinal))
                    .Select(kv => kv.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (matches.Count == 1)
                    return $"_controller.{matches[0]}";
            }

            var matchedByReference = task.HandlersSemantic.Items
                .Where(h => h.EventType == "U" && !string.IsNullOrWhiteSpace(h.Reference))
                .FirstOrDefault(h => referenceCandidates.Any(rc =>
                    string.Equals(h.Reference, rc, StringComparison.OrdinalIgnoreCase)));
            if (matchedByReference is not null)
            {
                var matchedCommand = ResolveHandlerCommandName(matchedByReference, task);
                if (IsApplicationEventReference(matchedByReference.EventParent, matchedByReference.EventPublicComponentId))
                    return $"Application.{matchedCommand}";
                if (matchedByReference.EventParent.HasValue && task.ParentOrdinal.HasValue)
                    return $"_controller._parent.{matchedCommand}";
                return $"_controller.{matchedCommand}";
            }

            if (!string.IsNullOrWhiteSpace(c.RaiseEventObject))
            {
                if (IsApplicationEventReference(c.RaiseEventParent, c.RaiseEventPublicComponentId))
                {
                    var applicationCommandName = ResolveCommandNameByEventObject(
                        c.RaiseEventObject!,
                        c.RaiseEventPublicComponentId,
                        c.RaiseEventParent,
                        task);
                    if (!string.IsNullOrWhiteSpace(applicationCommandName))
                        return applicationCommandName;
                }

                var parentCommandName = ResolveParentCommandNameByEventObject(c.RaiseEventObject!, task);
                if (!string.IsNullOrWhiteSpace(parentCommandName))
                    return $"_controller._parent.{parentCommandName}";

                var commandName = ResolveCommandNameByEventObject(
                    c.RaiseEventObject!,
                    c.RaiseEventPublicComponentId,
                    c.RaiseEventParent,
                    task);
                if (!string.IsNullOrWhiteSpace(commandName))
                    return commandName.StartsWith("Application.", StringComparison.Ordinal)
                        ? commandName
                        : $"_controller.{commandName}";

                var handlerByObj = task.HandlersSemantic.Items.FirstOrDefault(h =>
                    h.EventType == "U" &&
                    string.Equals(h.EventPublicObject, c.RaiseEventObject, StringComparison.OrdinalIgnoreCase));
                if (handlerByObj is not null)
                {
                    var handlerCommand = ResolveHandlerCommandName(handlerByObj, task);
                    if (IsApplicationEventReference(handlerByObj.EventParent, handlerByObj.EventPublicComponentId))
                        return $"Application.{handlerCommand}";
                    if (handlerByObj.EventParent.HasValue && task.ParentOrdinal.HasValue)
                        return $"_controller._parent.{handlerCommand}";
                    return $"_controller.{handlerCommand}";
                }
            }

            var uHandlers = task.HandlersSemantic.Items.Where(h => h.EventType == "U").ToList();
            if (uHandlers.Count == 1)
            {
                var handlerCommand = ResolveHandlerCommandName(uHandlers[0], task);
                if (IsApplicationEventReference(uHandlers[0].EventParent, uHandlers[0].EventPublicComponentId))
                    return $"Application.{handlerCommand}";
                if (uHandlers[0].EventParent.HasValue && task.ParentOrdinal.HasValue)
                    return $"_controller._parent.{handlerCommand}";
                return $"_controller.{handlerCommand}";
            }
        }

        if (raiseType == "I" && c.RaiseEventInternalEventId is int raiseInternalId)
        {
            var mapped = ResolveCommandByInternalEventId(raiseInternalId);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped!;
        }

        return "";
    }

    private static string ResolveParentCommandNameByEventObject(string eventPublicObject, TaskSemantic task)
    {
        if (!task.ParentOrdinal.HasValue || !int.TryParse(eventPublicObject, out var eventObj))
            return "";

        var relativePrefix = "";
        var currentTask = GetTaskByOrdinal(task.ParentOrdinal.Value, _allTasks);
        while (currentTask is not null)
        {
            if (!string.IsNullOrWhiteSpace(task.Description) &&
                (task.Description.IndexOf("Add Child", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 task.Description.IndexOf("Add Sibling", StringComparison.OrdinalIgnoreCase) >= 0) &&
                currentTask.ParentOrdinal.HasValue &&
                currentTask.EventsSemantic.ItemsByOrdinal.TryGetValue(eventObj, out var addNodeParentEvt) &&
                string.Equals(addNodeParentEvt.Description, "OK", StringComparison.OrdinalIgnoreCase))
                return relativePrefix + "Start_";

            if (currentTask.EventsSemantic.DescriptionByOrdinal.TryGetValue(eventObj, out var description))
                return relativePrefix + ResolveTaskCommandIdentifier(currentTask, description, preserveCase: true);

            relativePrefix += "_parent.";
            if (!currentTask.ParentOrdinal.HasValue)
                break;
            currentTask = GetTaskByOrdinal(currentTask.ParentOrdinal.Value, _allTasks);
        }
        return "";
    }

    private static string? ResolveViewAlignmentExpression(TaskFormControlDef c)
    {
        if (!c.HorizontalAlignment.HasValue)
            return null;
        return c.HorizontalAlignment.Value switch
        {
            1 => "System.Drawing.ContentAlignment.TopLeft",
            2 => "System.Drawing.ContentAlignment.MiddleCenter",
            3 => "System.Drawing.ContentAlignment.MiddleRight",
            _ => null
        };
    }

    private static string? ResolveInferredViewAlignmentExpression(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!IsViewEditControl(c))
            return null;

        TaskResourceColumnDef? resource = null;
        if (!string.IsNullOrWhiteSpace(c.ControlName))
        {
            task.ResourcesSemantic.ByName.TryGetValue(c.ControlName, out resource);
            if (resource is null)
                task.ResourcesSemantic.ByLegacyName.TryGetValue(c.ControlName, out resource);
        }

        if (resource is null && !string.IsNullOrWhiteSpace(c.DataColumn))
        {
            task.ResourcesSemantic.ByName.TryGetValue(c.DataColumn, out resource);
            if (resource is null)
                task.ResourcesSemantic.ByLegacyName.TryGetValue(c.DataColumn, out resource);
        }

        if (string.Equals(NormalizeAttrObjKind(resource?.AttrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return "System.Drawing.ContentAlignment.MiddleRight";

        var dataExpr = ResolveControlDataExpression(c, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(dataExpr))
        {
            var normalizedDataExpr = dataExpr.Trim();
            if (normalizedDataExpr.StartsWith("_controller.", StringComparison.Ordinal))
                normalizedDataExpr = normalizedDataExpr["_controller.".Length..];

            if (string.Equals(NormalizeAttrObjKind(ResolveModelColumnAttrObj(task, normalizedDataExpr)), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
                return "System.Drawing.ContentAlignment.MiddleRight";
        }

        return null;
    }

    private static string ResolveViewControlStyleExpression(TaskFormControlDef c)
    {
        var style = c.StyleValue ?? c.InternalStyleValue;
        if (string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase) && style == 7)
            return "XPARuntimeCore.Box.UI.ControlStyle.Flat";
        return style switch
        {
            1 => "XPARuntimeCore.Box.UI.ControlStyle.Flat",
            7 => "XPARuntimeCore.Box.UI.ControlStyle.Standard",
            _ => ""
        };
    }

    private static string ResolveViewControlTypeName(TaskFormControlDef c, TaskSemantic task, IReadOnlyList<ControlButtonModelDef> buttonModels, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase) &&
            (c.EnableRtf == true ||
             (!string.IsNullOrWhiteSpace(c.Text) && c.Text.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))))
            return "Shared.Theme.Controls.CompatibleLabel";

        if (c.Model == "CTRL_GUI0_PUSH_BUTTON" && c.ModelRefObj.HasValue)
        {
            var model = buttonModels.FirstOrDefault(m => m.Obj == c.ModelRefObj.Value);
            if (model is not null)
                return $"Controls.{ToPascalIdentifier(model.Name)}";
        }
        if (string.Equals(c.Model, "CTRL_GUI0_BROWSER", StringComparison.OrdinalIgnoreCase))
            return "Shared.Theme.Controls.WebBrowser";
        if (string.Equals(c.Model, "CTRL_GUI0_DOTNET", StringComparison.OrdinalIgnoreCase))
            return ResolveViewDotNetControlTypeName(c, task);
        return c.Model switch
        {
            "CTRL_GUI0_TABLE" => "Controls.V9CompatibleDefaultTable",
            "CTRL_GUI0_COLUMN" => "Shared.Theme.Controls.CompatibleGridColumn",
            "CTRL_GUI0_STATIC" => IsStaticGroupBoxLike(c, staticContainerIds)
                ? "Shared.Theme.Controls.GroupBox"
                : IsStaticShapeLike(c, staticContainerIds)
                    ? "Shared.Theme.Controls.Shape"
                    : "Shared.Theme.Controls.CompatibleLabel",
            "CTRL_GUI0_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_RICH_CLIENT_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_BROWSER_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_GUI0_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_RICH_CLIENT_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_BROWSER_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_GUI0_PUSH_BUTTON" => "Shared.Theme.Controls.Button",
            "CTRL_GUI0_SUBFORM" => "Shared.Theme.Controls.SubForm",
            "CTRL_GUI0_CHECKBOX" => "ENV.UI.CheckBox",
            "CTRL_RICH_CLIENT_CHECKBOX" => "ENV.UI.CheckBox",
            "CTRL_GUI0_TAB" => "ENV.UI.TabControl",
            "CTRL_GUI0_TREE" => "ENV.UI.TreeView",
            "CTRL_GUI0_RADIO" => "ENV.UI.RadioButton",
            "CTRL_GUI0_IMAGE" => "ENV.UI.PictureBox",
            "CTRL_GUI1_IMAGE" => "ENV.UI.PictureBox",
            "CTRL_GUI0_LINE" => "Shared.Theme.Controls.Line",
            "CTRL_GUI0_LISTBOX" => "ENV.UI.ListBox",
            "CTRL_GUI0_RICH_EDIT" => "ENV.UI.RichTextBox",
            _ => "Shared.Theme.Controls.CompatibleTextBox"
        };
    }

    private static string ResolveViewDotNetControlTypeName(TaskFormControlDef c, TaskSemantic task)
    {
        var resource = ResolveViewHostResource(c, task);
        if (resource is not null && IsDotNetTaskResource(resource) && !string.IsNullOrWhiteSpace(resource.ObjectType))
            return NormalizeDotNetObjectType(resource.ObjectType);
        return "System.Windows.Forms.Panel";
    }

    private static TaskResourceColumnDef? ResolveViewHostResource(TaskFormControlDef c, TaskSemantic task)
    {
        if (!string.IsNullOrWhiteSpace(c.ControlName))
        {
            if (task.ResourcesSemantic.ByName.TryGetValue(c.ControlName, out var byName))
                return byName;
            if (task.ResourcesSemantic.ByLegacyName.TryGetValue(c.ControlName, out var byLegacy))
                return byLegacy;
        }

        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            if (task.ResourcesSemantic.ByName.TryGetValue(c.DataColumn, out var resourceByName))
                return resourceByName;
            if (task.ResourcesSemantic.ByLegacyName.TryGetValue(c.DataColumn, out var resourceByLegacy))
                return resourceByLegacy;
        }

        return null;
    }

    private static bool IsNativeWinFormsViewControl(TaskFormControlDef c, string typeName)
    {
        return string.Equals(c.Model, "CTRL_GUI0_DOTNET", StringComparison.OrdinalIgnoreCase) ||
               typeName.StartsWith("System.Windows.Forms.", StringComparison.Ordinal);
    }

}

