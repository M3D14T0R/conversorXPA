using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveViewControlVariableName(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        var baseName = ResolveViewControlVariableBaseName(c, staticContainerIds);
        // Legacy stateless fallback keeps deterministic uniqueness across arbitrary calls.
        return baseName + c.Id;
    }

    private static string ResolveViewControlVariableBaseName(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (IsStaticGroupBoxLike(c, staticContainerIds))
        {
            var grpSuffix = !string.IsNullOrWhiteSpace(c.ControlName)
                ? ToPascalIdentifier(c.ControlName)
                : !string.IsNullOrWhiteSpace(c.Text) && !c.Text.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase)
                    ? ToPascalIdentifier(c.Text)
                    : "";
            return "grp" + grpSuffix;
        }

        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : !string.IsNullOrWhiteSpace(c.ColumnTitle)
                ? ToPascalIdentifier(c.ColumnTitle)
                : c.Id.ToString();
        if (string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase) &&
            IsStaticShapeLike(c, staticContainerIds) &&
            string.IsNullOrWhiteSpace(c.ControlName))
            return "shp";
        return c.Model switch
        {
            "CTRL_GUI0_TABLE" => c.Id == 1 ? "grd" : "grd" + suffix,
            "CTRL_GUI0_COLUMN" => "gcl" + suffix,
            "CTRL_GUI0_STATIC" => IsStaticShapeLike(c, staticContainerIds) ? "shp" + suffix : "lbl" + suffix,
            "CTRL_GUI0_EDIT" => "txt" + suffix,
            "CTRL_RICH_CLIENT_EDIT" => "txt" + suffix,
            "CTRL_BROWSER_EDIT" => "txt" + suffix,
            "CTRL_GUI0_DOTNET" => "dn" + suffix,
            "CTRL_GUI0_BROWSER" => "web" + suffix,
            "CTRL_GUI0_COMBOBOX" => "cbo" + suffix,
            "CTRL_RICH_CLIENT_COMBOBOX" => "cbo" + suffix,
            "CTRL_BROWSER_COMBOBOX" => "cbo" + suffix,
            "CTRL_GUI0_PUSH_BUTTON" => "btn" + suffix,
            "CTRL_GUI0_SUBFORM" => "SubForm" + suffix,
            "CTRL_GUI0_CHECKBOX" => "chk" + suffix,
            "CTRL_RICH_CLIENT_CHECKBOX" => "chk" + suffix,
            "CTRL_GUI0_TAB" => "tab" + suffix,
            "CTRL_GUI0_TREE" => "tre" + suffix,
            "CTRL_GUI0_RADIO" => "rad" + suffix,
            "CTRL_GUI0_IMAGE" => "pic" + suffix,
            "CTRL_GUI1_IMAGE" => "pic" + suffix,
            "CTRL_GUI0_LISTBOX" => "lst" + suffix,
            "CTRL_GUI0_RICH_EDIT" => "rtx" + suffix,
            _ => "txt" + suffix
        };
    }

    private static Dictionary<int, string> BuildUniqueViewVariableNames(
        IReadOnlyList<TaskFormControlDef> controls,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlySet<int>? staticContainerIds = null)
    {
        var map = new Dictionary<int, string>();
        var baseGroups = controls
            .GroupBy(c => ResolvePreferredViewControlVariableBaseName(c, task, allTasks, dataObjects, staticContainerIds), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sequenceByBase = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var c in controls)
        {
            var baseName = ResolvePreferredViewControlVariableBaseName(c, task, allTasks, dataObjects, staticContainerIds);
            var hasCollision = baseGroups.TryGetValue(baseName, out var same) && same.Count > 1;
            var candidate = baseName;
            if (hasCollision)
            {
                var seq = sequenceByBase.TryGetValue(baseName, out var current) ? current + 1 : 1;
                sequenceByBase[baseName] = seq;
                candidate = seq switch
                {
                    1 => baseName,
                    2 => baseName + "_",
                    _ => baseName + "_" + (seq - 1).ToString()
                };
            }
            while (!used.Add(candidate))
                candidate += "_";
            map[c.Id] = candidate;
        }

        return map;
    }

    private static string ResolvePreferredViewControlVariableBaseName(
        TaskFormControlDef c,
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlySet<int>? staticContainerIds = null)
    {
        var fallback = ResolveViewControlVariableBaseName(c, staticContainerIds);
        if (IsStaticGroupBoxLike(c, staticContainerIds) || IsStaticShapeLike(c, staticContainerIds))
            return fallback;

        string PrefixFor(TaskFormControlDef ctrl) => ctrl.Model switch
        {
            "CTRL_GUI0_TABLE" => "grd",
            "CTRL_GUI0_COLUMN" => "gcl",
            "CTRL_GUI0_STATIC" => "lbl",
            "CTRL_GUI0_EDIT" => "txt",
            "CTRL_RICH_CLIENT_EDIT" => "txt",
            "CTRL_BROWSER_EDIT" => "txt",
            "CTRL_GUI0_COMBOBOX" => "cbo",
            "CTRL_RICH_CLIENT_COMBOBOX" => "cbo",
            "CTRL_BROWSER_COMBOBOX" => "cbo",
            "CTRL_GUI0_PUSH_BUTTON" => "btn",
            "CTRL_GUI0_SUBFORM" => "SubForm",
            "CTRL_GUI0_DOTNET" => "dn",
            "CTRL_GUI0_BROWSER" => "web",
            "CTRL_GUI0_CHECKBOX" => "chk",
            "CTRL_RICH_CLIENT_CHECKBOX" => "chk",
            "CTRL_GUI0_TAB" => "tab",
            "CTRL_GUI0_TREE" => "tre",
            "CTRL_GUI0_RADIO" => "rad",
            "CTRL_GUI0_IMAGE" => "pic",
            "CTRL_GUI1_IMAGE" => "pic",
            "CTRL_GUI0_LISTBOX" => "lst",
            "CTRL_GUI0_RICH_EDIT" => "rtx",
            _ => "txt"
        };

        if (string.IsNullOrWhiteSpace(c.ControlName) && c.Model == "CTRL_GUI0_STATIC" && !string.IsNullOrWhiteSpace(c.Text))
        {
            var txt = c.Text.Trim();
            var labelName = ToLabelIdentifier(txt, c.Id);
            if (!string.IsNullOrWhiteSpace(labelName) && !labelName.Equals("Unnamed", StringComparison.OrdinalIgnoreCase))
                return "lbl" + labelName;
        }

        var generatedControlName = IsLikelyGeneratedControlName(c.ControlName);
        if (string.IsNullOrWhiteSpace(c.ControlName) || generatedControlName)
        {
            if (c.DataExpressionId.HasValue && IsViewEditControl(c))
                return PrefixFor(c) + "Exp_" + c.DataExpressionId.Value;

            var dataExpr = ResolveControlDataExpression(c, task, allTasks, dataObjects);
            if (string.IsNullOrWhiteSpace(dataExpr))
                dataExpr = ResolveDataColumnOrdinalBinding(c.DataColumn, task, allTasks);
            if (!string.IsNullOrWhiteSpace(dataExpr) && IsSimpleMemberAccess(dataExpr))
            {
                var member = dataExpr.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(member))
                    return PrefixFor(c) + ToPascalIdentifier(member);
            }
        }

        return fallback;
    }

    private static bool IsLikelyGeneratedControlName(string? controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName))
            return false;
        var n = controlName.Trim();
        if (Regex.IsMatch(n, @"^.+_\d{3,}$"))
            return true;
        if (Regex.IsMatch(n, @"^\w+\s+\w+_\d{3,}$"))
            return true;
        return false;
    }

    private static string ToLabelIdentifier(string raw, int controlId)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Static" + controlId;
        var source = raw.Trim();
        var isRtf = source.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);
        if (isRtf)
            return "Rtf" + controlId;
        var normalized = Regex.Replace(source, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
            return "Static" + controlId;
        var tokens = normalized
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Take(4)
            .ToArray();
        if (tokens.Length == 0)
            return "Static" + controlId;
        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (i == 0)
            {
                sb.Append(char.ToUpperInvariant(t[0]) + t[1..]);
                continue;
            }

            if (Regex.IsMatch(t, @"^\d+$"))
            {
                sb.Append('_').Append(t);
                continue;
            }

            if (t.Length == 1)
            {
                sb.Append('_').Append(t.ToLowerInvariant());
                continue;
            }

            sb.Append(char.ToUpperInvariant(t[0]) + t[1..]);
        }
        var id = sb.ToString();
        if (id.Length > 40)
            id = id.Substring(0, 40).TrimEnd('_');
        if (string.IsNullOrWhiteSpace(id))
            id = "Static" + controlId;
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }
}

