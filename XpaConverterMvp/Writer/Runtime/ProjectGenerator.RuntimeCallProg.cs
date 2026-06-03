using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string RewriteCallProgExpressions(string expr)
{
    if (string.IsNullOrWhiteSpace(expr) || !expr.Contains("u.CallProg(", StringComparison.Ordinal))
        return expr;

    var marker = "u.CallProg(";
    var index = expr.IndexOf(marker, StringComparison.Ordinal);
    while (index >= 0)
    {
        var argsStart = index + marker.Length;
        var end = FindMatchingParen(expr, argsStart - 1);
        if (end < 0)
            break;

        var argsText = expr.Substring(argsStart, end - argsStart);
        var args = SplitTopLevelArguments(argsText);
        if (args.Count >= 1)
        {
            var replacement = ResolveCallProgReplacement(args);
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                expr = expr.Substring(0, index) + replacement + expr[(end + 1)..];
                index = expr.IndexOf(marker, index + replacement.Length, StringComparison.Ordinal);
                continue;
            }
        }

        index = expr.IndexOf(marker, end + 1, StringComparison.Ordinal);
    }

    return expr;
}

private static string? ResolveCallProgReplacement(IReadOnlyList<string> args)
{
    if (_allTasks is null || args.Count == 0)
        return null;

    TaskSemantic? targetTask = null;
    var firstArg = args[0].Trim();
    var progLiteralMatch = Regex.Match(firstArg, "^\"(?<id>\\d+),0\"PROG$", RegexOptions.IgnoreCase);
    if (progLiteralMatch.Success && int.TryParse(progLiteralMatch.Groups["id"].Value, out var xpaId))
        targetTask = ResolveTaskByXpaId(xpaId, _allTasks);
    else if (int.TryParse(firstArg, out var numericTaskId))
        targetTask = ResolveTaskByXpaId(numericTaskId, _allTasks);
    else
    {
        var progIdxMatch = Regex.Match(firstArg, "^u\\.ProgIdx\\(\"(?<name>[^\"]+)\",\\s*true\\)$", RegexOptions.IgnoreCase);
        if (progIdxMatch.Success)
        {
            var publicName = progIdxMatch.Groups["name"].Value;
            targetTask = _allTasks.FirstOrDefault(t => string.Equals(t.PublicName, publicName, StringComparison.OrdinalIgnoreCase));
        }
    }

    if (targetTask is null)
        return null;

    var targetClass = ResolveTaskClassName(targetTask, _allTasks ?? Array.Empty<TaskSemantic>());
    var callArgs = args.Skip(1).ToList();
    var runArgs = callArgs.Count == 0 ? "" : string.Join(", ", callArgs);
    var invocation = string.IsNullOrWhiteSpace(runArgs)
        ? $"new {targetClass}().Run()"
        : $"new {targetClass}().Run({runArgs})";

    var returnType = ResolveTaskReturnType(targetTask);
    if (string.Equals(returnType, "Text", StringComparison.Ordinal))
        return $"u.RemoveZeroChar({invocation})";
    return invocation;
}
}

