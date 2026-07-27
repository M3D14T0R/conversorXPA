using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private sealed record ViewChoiceItem(string Value, string Display);

    private static void EmitViewChoiceItems(
        StringBuilder designer,
        string controlVariable,
        TaskFormControlDef control,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!IsViewChoiceControl(control))
            return;

        var range = control.ItemsList;
        int? textValueLength = null;

        var dataExpression = ResolveControlDataExpression(control, task, allTasks, dataObjects);
        if (!string.IsNullOrWhiteSpace(dataExpression) &&
            TryResolveDataViewMemberColumn(task, dataExpression, out _, out var dataColumn))
        {
            if (string.IsNullOrWhiteSpace(range))
                range = ResolveDataColumnInputRange(dataColumn, _allFieldModels);
            textValueLength = ResolveTextChoiceValueLength(dataColumn);
        }
        else
        {
            var resource = ResolveResourceByTargetPath(task, dataExpression, allTasks)
                           ?? ResolveDataColumnSelectResource(control.DataColumn, task, allTasks)
                           ?? ResolveViewHostResource(control, task);
            if (resource is not null)
            {
                if (string.IsNullOrWhiteSpace(range))
                    range = ResolveTaskResourceInputRange(resource);
                textValueLength = ResolveTextChoiceValueLength(resource);
            }
        }

        if (string.IsNullOrWhiteSpace(range))
            return;

        var items = ParseViewChoiceItems(range!, textValueLength);
        if (items.Count == 0)
            return;

        // ENV uses the XPA range grammar. "\ " is the encoded empty value;
        // keeping it in Values preserves the default/blank option instead of
        // shifting the display list by one position.
        var values = string.Join(",", items.Select(item =>
            item.Value.Length == 0 ? @"\ " : item.Value));
        var displays = string.Join(",", items.Select(item => item.Display));
        designer.AppendLine($"        {controlVariable}.Values = {ToCSharpLiteral(values)};");
        designer.AppendLine($"        {controlVariable}.DisplayValues = {ToCSharpLiteral(displays)};");
    }

    private static bool IsViewChoiceControl(TaskFormControlDef control)
        => control.Model is
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RADIO" or
            "CTRL_GUI0_TAB";

    private static string ResolveTaskResourceInputRange(TaskResourceColumnDef resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.InputRange))
            return resource.InputRange!;

        if (!string.IsNullOrWhiteSpace(resource.ModelRefObj) &&
            int.TryParse(resource.ModelRefObj, out var modelOrdinal))
        {
            var fieldModel = _allFieldModels.FirstOrDefault(model => model.Ordinal == modelOrdinal);
            if (!string.IsNullOrWhiteSpace(fieldModel?.InputRange))
                return fieldModel!.InputRange!;
        }

        return "";
    }

    private static int? ResolveTextChoiceValueLength(DataColumnDef column)
    {
        var isText =
            string.Equals(column.Attribute, "A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column.AttrObj, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase);
        if (!isText)
            return null;

        return ResolveTextPictureLength(column.Picture)
               ?? ResolveTextPictureLength(column.FieldPhysicalPicture)
               ?? (column.FieldPhysicalSize is > 0 ? column.FieldPhysicalSize : null);
    }

    private static int? ResolveTextChoiceValueLength(TaskResourceColumnDef resource)
    {
        var attr = NormalizeAttrObjKind(resource.AttrObj);
        if (!string.Equals(attr, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(attr, "FIELD_UNICODE", StringComparison.OrdinalIgnoreCase))
            return null;

        var picture = resource.Picture;
        if (string.IsNullOrWhiteSpace(picture) &&
            !string.IsNullOrWhiteSpace(resource.ModelRefObj) &&
            int.TryParse(resource.ModelRefObj, out var modelOrdinal))
            picture = _allFieldModels.FirstOrDefault(model => model.Ordinal == modelOrdinal)?.Picture;

        return ResolveTextPictureLength(picture);
    }

    private static int? ResolveTextPictureLength(string? picture)
    {
        if (string.IsNullOrWhiteSpace(picture))
            return null;

        var normalized = picture.Trim().ToUpperInvariant();
        var digits = new string(normalized.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0 &&
            int.TryParse(digits, out var declaredLength) &&
            declaredLength > 0)
        {
            return declaredLength;
        }

        if (normalized == "A")
            return 1;

        var unicodeLength = normalized.TakeWhile(ch => ch == 'U').Count();
        return unicodeLength > 0 ? unicodeLength : null;
    }

    private static IReadOnlyList<ViewChoiceItem> ParseViewChoiceItems(
        string range,
        int? textValueLength)
    {
        var result = new List<ViewChoiceItem>();
        foreach (var rawItem in range.Split(','))
        {
            var original = rawItem ?? "";
            var leftTrimmed = original.TrimStart();
            if (leftTrimmed.StartsWith("\\", StringComparison.Ordinal))
            {
                result.Add(new ViewChoiceItem(
                    "",
                    leftTrimmed.Length == 1 ? "" : leftTrimmed[1..].Trim()));
                continue;
            }

            var text = original.Trim();
            if (text.Length == 0)
                continue;

            var firstSpace = text.IndexOf(' ');
            if (firstSpace > 0)
            {
                var prefix = text[..firstSpace].Trim();
                var display = text[(firstSpace + 1)..].TrimStart();
                if (IsViewChoiceValuePrefix(prefix))
                {
                    result.Add(new ViewChoiceItem(
                        prefix,
                        string.IsNullOrWhiteSpace(display) ? prefix : display));
                    continue;
                }
            }

            result.Add(new ViewChoiceItem(
                textValueLength is > 0 && text.Length > textValueLength.Value
                    ? text[..textValueLength.Value]
                    : text,
                text));
        }

        return result;
    }

    private static bool IsViewChoiceValuePrefix(string prefix)
        => !string.IsNullOrWhiteSpace(prefix) &&
           prefix.Length <= 3 &&
           prefix.All(char.IsLetterOrDigit);
}
