using System;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string StripRedundantOuterParentheses(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();
        while (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            var depth = 0;
            var wrapsAll = true;
            char quote = '\0';
            for (var i = 0; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        if (quote == '"' && i > 0 && trimmed[i - 1] == '\\')
                            continue;
                        quote = '\0';
                    }
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '(')
                    depth++;
                else if (ch == ')')
                    depth--;

                if (depth == 0 && i < trimmed.Length - 1)
                {
                    wrapsAll = false;
                    break;
                }
            }

            if (!wrapsAll)
                break;

            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static string RewriteAncestorResourceMemberReferences(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            !expression.Contains("_parent.", StringComparison.Ordinal) ||
            _allTasks is null)
            return expression;

        var result = new StringBuilder(expression.Length);
        for (var i = 0; i < expression.Length; i++)
        {
            if (TryReadQuotedSegmentEnd(expression, i, out var quotedEnd))
            {
                result.Append(expression, i, quotedEnd - i + 1);
                i = quotedEnd;
                continue;
            }

            if (!expression.AsSpan(i).StartsWith("_parent.".AsSpan(), StringComparison.Ordinal))
            {
                result.Append(expression[i]);
                continue;
            }

            var rewritten = TryRewriteAncestorResourcePath(expression, i, task, out var consumedLength);
            if (rewritten is null)
            {
                result.Append(expression[i]);
                continue;
            }

            result.Append(rewritten);
            i += consumedLength - 1;
        }

        return result.ToString();
    }

    private static string? TryRewriteAncestorResourcePath(string expression, int startIndex, TaskSemantic task, out int consumedLength)
    {
        consumedLength = 0;
        if (_allTasks is null || !expression.AsSpan(startIndex).StartsWith("_parent.".AsSpan(), StringComparison.Ordinal))
            return null;

        var currentTask = task;
        var index = startIndex;
        var prefix = new StringBuilder();
        while (expression.AsSpan(index).StartsWith("_parent.".AsSpan(), StringComparison.Ordinal))
        {
            if (!currentTask.ParentOrdinal.HasValue)
                return null;

            currentTask = GetTaskByOrdinal(currentTask.ParentOrdinal.Value, _allTasks);
            if (currentTask is null)
                return null;

            prefix.Append("_parent.");
            index += "_parent.".Length;
        }

        if (index >= expression.Length || !(char.IsLetter(expression[index]) || expression[index] == '_'))
            return null;

        var identStart = index;
        index++;
        while (index < expression.Length && (char.IsLetterOrDigit(expression[index]) || expression[index] == '_'))
            index++;

        var identifier = expression[identStart..index];
        if (!currentTask.ResourcesSemantic.ByName.TryGetValue(identifier, out var resource) &&
            !currentTask.ResourcesSemantic.ByLegacyName.TryGetValue(identifier, out resource))
            return null;

        var memberName = ResolveTaskResourceMemberName(currentTask, resource);
        consumedLength = index - startIndex;
        if (string.Equals(memberName, identifier, StringComparison.Ordinal))
            return expression.Substring(startIndex, consumedLength);

        return prefix.ToString() + memberName;
    }
}

