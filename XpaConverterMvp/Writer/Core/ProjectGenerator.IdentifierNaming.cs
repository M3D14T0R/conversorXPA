using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string EnsureUniqueIdentifier(string baseName, HashSet<string> used, string fallback)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = fallback;
        if (used.Add(baseName))
            return baseName;

        var suffix = 2;
        while (true)
        {
            var candidate = baseName + suffix;
            if (used.Add(candidate))
                return candidate;
            suffix++;
        }
    }

    private static string ResolveMultiFormViewClassName(TaskSemantic t, TaskFormEntryDef formEntry, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (t.View.SelectedFormEntry?.Index == formEntry.Index &&
            !string.IsNullOrWhiteSpace(t.View.ClassName))
        {
            return t.View.ClassName;
        }

        if (formEntry.Form is not null && !string.IsNullOrWhiteSpace(formEntry.Form.FormName))
        {
            var ownerTaskName = ToTaskClassName(ResolveViewOwnerTask(t, allTasks).Description);
            var formName = ToPascalIdentifier(formEntry.Form.FormName);
            var candidate = ownerTaskName + formName;
            if (HasMultiFormViewClassNameCollision(t, formEntry, candidate, allTasks))
                candidate = ownerTaskName + ResolveTaskClassName(t, allTasks) + formName;
            return EnsureUniqueMultiFormViewClassName(t, candidate, allTasks);
        }
        return ResolveViewClassName(t, allTasks);
    }

    private static bool HasMultiFormViewClassNameCollision(
        TaskSemantic ownerTask,
        TaskFormEntryDef ownerFormEntry,
        string candidate,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        foreach (var task in allTasks)
        {
            foreach (var formEntry in task.FormEntries)
            {
                if (ReferenceEquals(task, ownerTask) && formEntry.Index == ownerFormEntry.Index)
                    continue;
                if (!string.Equals(formEntry.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) ||
                    formEntry.Form is null ||
                    string.IsNullOrWhiteSpace(formEntry.Form.FormName))
                    continue;
                if (task.View.SelectedFormEntry?.Index == formEntry.Index &&
                    !string.IsNullOrWhiteSpace(task.View.ClassName))
                    continue;

                var ownerTaskName = ToTaskClassName(ResolveViewOwnerTask(task, allTasks).Description);
                var otherCandidate = ownerTaskName + ToPascalIdentifier(formEntry.Form.FormName);
                if (string.Equals(candidate, otherCandidate, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static string EnsureUniqueMultiFormViewClassName(TaskSemantic ownerTask, string candidate, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return candidate;

        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in allTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.View.ClassName))
                reserved.Add(task.View.ClassName);
        }

        var unique = candidate;
        while (reserved.Contains(unique) &&
               !string.Equals(ownerTask.View.ClassName, unique, StringComparison.Ordinal))
            unique += "_";

        if (string.Equals(ownerTask.View.ClassName, unique, StringComparison.Ordinal))
            unique += "_";

        return unique;
    }

    private static string ResolveViewClassBaseName(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        return t.View.BaseClassName;
    }

    private static TaskSemantic ResolveViewOwnerTask(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        var root = t;
        while (root.ParentOrdinal.HasValue)
        {
            var parent = allTasks.FirstOrDefault(x => x.Ordinal == root.ParentOrdinal.Value);
            if (parent is null)
                break;
            root = parent;
        }
        return root;
    }

    private static bool NormalizedIdentifierStartsWith(string id, string prefix)
    {
        var a = Regex.Replace(id ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        var b = Regex.Replace(prefix ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && a.StartsWith(b, StringComparison.Ordinal);
    }

    private static string EnsureViewSuffix(string baseName)
    {
        if (baseName.EndsWith("View", StringComparison.Ordinal))
            return baseName;
        return baseName + "View";
    }

    private static bool IsGenericViewName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return true;

        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Title",
            "Display",
            "View",
            "List",
            "Query",
            "Edit",
            "Data",
            "Details"
        };
        if (generic.Contains(baseName))
            return true;

        // Names ending with "Title" are usually per-task variants in official conversion
        // (for example ST01_AStrTitle), so they should be task-scoped.
        if (baseName.EndsWith("Title", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string ResolveApplicationViewClassName(TaskSemantic mainTask, string appNamespace)
    {
        if (!string.IsNullOrWhiteSpace(_targetComponent))
            return "Application" + ToPascalIdentifier(_targetComponent);

        if (mainTask.View.ClassName.StartsWith("Application", StringComparison.Ordinal))
            return mainTask.View.ClassName;

        return "Application" + ToPascalIdentifier(appNamespace);
    }

    private static string ToPascalIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed";
        var pieces = Regex.Split(raw, "[^A-Za-z0-9]+")
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        var id = string.Concat(pieces);
        if (string.IsNullOrWhiteSpace(id))
            id = "Unnamed";
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    private static string ResolvePrinterIdentifier(string raw)
    {
        var normalized = (raw ?? "").Trim();
        var printerMatch = Regex.Match(normalized, @"^PRINTER(?<number>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (printerMatch.Success)
            return "Printer" + printerMatch.Groups["number"].Value;

        return ToPascalIdentifier(normalized);
    }

    private static string ToCamelIdentifier(string raw)
    {
        var p = ToPascalIdentifier(raw);
        if (string.IsNullOrEmpty(p))
            return p;
        return char.ToLowerInvariant(p[0]) + p[1..];
    }

    private static string ToLegacyVariableName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "vUnnamed";
        var trimmed = raw.Trim();
        var id = new StringBuilder();
        var capitalizeNext = false;
        var pendingUnderscore = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingUnderscore)
                {
                    if (id.Length > 0 && id[^1] != '_')
                        id.Append('_');
                    pendingUnderscore = false;
                }

                if (capitalizeNext && char.IsLetter(ch))
                    id.Append(char.ToUpperInvariant(ch));
                else
                    id.Append(ch);

                capitalizeNext = false;
                continue;
            }

            if (ch == '.')
            {
                pendingUnderscore = id.Length > 0;
                capitalizeNext = false;
                continue;
            }

            if (ch == '_')
            {
                if (id.Length > 0 && id[^1] != '_')
                    id.Append('_');
                pendingUnderscore = false;
                capitalizeNext = false;
                continue;
            }

            capitalizeNext = id.Length > 0;
        }

        var result = id.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "vUnnamed";
        if (trimmed.Length > 0 && !char.IsLetterOrDigit(trimmed[0]))
            result = "_" + result;
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }
}

