using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveParentBindingExpression(
        DataColumnDef childColumn,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var cacheKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{childColumn.Id}|{childColumn.Name}|{childColumn.DbColumnName}");
        if (_parentBindingExpressionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!task.ParentOrdinal.HasValue)
        {
            _parentBindingExpressionCache[cacheKey] = "";
            return "";
        }
        _tasksByOrdinal.TryGetValue(task.ParentOrdinal.Value, out var parentTask);
        if (parentTask is null)
        {
            _parentBindingExpressionCache[cacheKey] = "";
            return "";
        }
        var parentMembers = BuildModelMembers(parentTask, dataObjects);
        foreach (var pm in parentMembers)
        {
            var pd = dataObjects.FirstOrDefault(x => x.Ordinal == pm.DbObj);
            if (pd is null)
                continue;
            var parentCol = pd.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, childColumn.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.DbColumnName, childColumn.DbColumnName, StringComparison.OrdinalIgnoreCase));
            if (parentCol is null)
                continue;
            var resolved = $"_parent.{pm.MemberName}.{ToPascalIdentifier(parentCol.Name)}";
            _parentBindingExpressionCache[cacheKey] = resolved;
            return resolved;
        }
        _parentBindingExpressionCache[cacheKey] = "";
        return "";
    }

    private static string CanonicalizeViewRaiseExpression(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        if (!string.IsNullOrWhiteSpace(task.Description) &&
            (task.Description.IndexOf("Add Child", StringComparison.OrdinalIgnoreCase) >= 0 ||
             task.Description.IndexOf("Add Sibling", StringComparison.OrdinalIgnoreCase) >= 0) &&
            string.Equals(expression.Trim(), "_controller._parent.Event1", StringComparison.Ordinal))
            return "_controller._parent._parent.Start_";

        expression = Regex.Replace(
            expression,
            @"(?<![\w\.])Keys\.",
            "System.Windows.Forms.Keys.",
            RegexOptions.CultureInvariant);
        expression = PreferLocalControllerCommandForViewRaise(expression, task);
        expression = CanonicalizeControllerRaiseMemberReference(expression, "_controller.", task);
        if (task.ParentOrdinal.HasValue)
        {
            var parentTask = (_allTasks ?? Array.Empty<TaskSemantic>()).FirstOrDefault(x => x.Ordinal == task.ParentOrdinal.Value);
            if (parentTask is not null)
                expression = CanonicalizeControllerRaiseMemberReference(expression, "_controller._parent.", parentTask);
        }
        return ClampViewRaiseParentChain(expression, task);
    }

    private static string PreferLocalControllerCommandForViewRaise(string expression, TaskSemantic task)
    {
        const string applicationPrefix = "Application.";
        if (string.IsNullOrWhiteSpace(expression) ||
            !expression.StartsWith(applicationPrefix, StringComparison.Ordinal))
            return expression;

        var member = expression[applicationPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(member) ||
            member.Contains('(') ||
            member.Contains('.') ||
            member.Contains('[') ||
            member.Contains(']'))
            return expression;

        var commandMap = BuildTaskCommandMemberMap(task);
        if (commandMap.TryGetValue(member, out var exact))
            return "_controller." + exact;

        var normalizedMember = NormalizeCommandLookupKey(member);
        if (string.IsNullOrWhiteSpace(normalizedMember))
            return expression;

        var matches = commandMap
            .Where(kv => string.Equals(NormalizeCommandLookupKey(kv.Key), normalizedMember, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return matches.Count == 1
            ? "_controller." + matches[0]
            : expression;
    }

    private static string ClampViewRaiseParentChain(string expression, TaskSemantic task)
    {
        const string prefix = "_controller.";
        const string parentSegment = "_parent.";
        if (string.IsNullOrWhiteSpace(expression) ||
            !expression.StartsWith(prefix + parentSegment, StringComparison.Ordinal))
            return expression;

        var allowedDepth = 0;
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            allowedDepth++;
            var parentTask = (_allTasks ?? Array.Empty<TaskSemantic>()).FirstOrDefault(x => x.Ordinal == parentOrdinal.Value);
            if (parentTask is null)
                break;
            parentOrdinal = parentTask.ParentOrdinal;
        }

        if (allowedDepth <= 0)
            return expression.Replace(prefix + parentSegment, prefix, StringComparison.Ordinal);

        var cursor = prefix.Length;
        var depth = 0;
        while (cursor + parentSegment.Length <= expression.Length &&
               string.Equals(expression.Substring(cursor, parentSegment.Length), parentSegment, StringComparison.Ordinal))
        {
            depth++;
            cursor += parentSegment.Length;
        }

        if (depth <= allowedDepth)
            return expression;

        return prefix + string.Concat(Enumerable.Repeat(parentSegment, allowedDepth)) + expression[cursor..];
    }

    private static string CanonicalizeControllerRaiseMemberReference(string expression, string prefix, TaskSemantic task)
    {
        if (!expression.StartsWith(prefix, StringComparison.Ordinal))
            return expression;

        var member = expression[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(member) ||
            member.Contains('(') ||
            member.Contains('.') ||
            member.Contains('[') ||
            member.Contains(']'))
            return expression;

        var commandMap = BuildTaskCommandMemberMap(task);
        if (commandMap.TryGetValue(member, out var exact))
            return prefix + exact;

        var normalizedMember = NormalizeCommandLookupKey(member);
        if (string.IsNullOrWhiteSpace(normalizedMember))
            return expression;

        var matches = commandMap
            .Where(kv => string.Equals(NormalizeCommandLookupKey(kv.Key), normalizedMember, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 1)
            return prefix + matches[0];

        if (string.Equals(prefix, "_controller.", StringComparison.Ordinal) &&
            TryResolveApplicationCommandMember(member, out var applicationCommandMember))
            return $"Application.{applicationCommandMember}";

        return expression;
    }

    private static bool TryResolveApplicationCommandMember(string member, out string commandMember)
    {
        commandMember = "";
        if (string.IsNullOrWhiteSpace(member))
            return false;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        if (allTasks.Count == 0)
            return false;

        var normalizedMember = NormalizeCommandLookupKey(member);
        if (string.IsNullOrWhiteSpace(normalizedMember))
            return false;

        var applicationTask = allTasks.FirstOrDefault(x => x.MainProgram)
                              ?? allTasks.FirstOrDefault(x => x.ParentOrdinal is null);
        if (applicationTask is null)
            return false;

        var candidates = new[] { applicationTask }
            .SelectMany(root =>
            {
                var commandMap = BuildTaskCommandMemberMap(root);
                return commandMap
                    .Where(kv =>
                        string.Equals(kv.Key, member, StringComparison.Ordinal) ||
                        string.Equals(NormalizeCommandLookupKey(kv.Key), normalizedMember, StringComparison.Ordinal))
                    .Select(kv => kv.Value);
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Count != 1)
            return false;

        commandMember = candidates[0];
        return true;
    }

    private static string NormalizeExpressionByAttribute(TaskSemantic task, string? attr, string translated)
    {
        return NormalizeExpressionByAttributeCentral(task, attr, translated);
    }

    private static string RewriteTextSinkCalls(string expression)
        => RewriteTextSinkCallsCentral(expression);
}

