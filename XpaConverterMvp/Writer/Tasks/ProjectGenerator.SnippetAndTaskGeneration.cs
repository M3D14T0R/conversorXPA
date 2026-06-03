using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string BuildSnippetSource(string rawSnippetCode, string generatedClassName)
    {
        return BuildSnippetSource(rawSnippetCode, generatedClassName, BuildSnippetNamespace(generatedClassName));
    }

    private static string BuildSnippetSource(string rawSnippetCode, string generatedClassName, string generatedNamespace)
    {
        var code = rawSnippetCode.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
        code = Regex.Replace(
            code,
            @"(\b(?:public\s+|internal\s+)?(?:static\s+)?class\s+)Snippet\b",
            "$1" + generatedClassName,
            RegexOptions.IgnoreCase);
        if (Regex.IsMatch(code, @"(?m)^\s*namespace\b"))
            return code;

        var lines = code.Split('\n');
        var usingBlock = new List<string>();
        var bodyBlock = new List<string>();
        var inBody = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var isUsing = trimmed.StartsWith("using ", StringComparison.Ordinal);
            if (!inBody && (isUsing || string.IsNullOrWhiteSpace(line)))
            {
                usingBlock.Add(line.TrimEnd());
                continue;
            }

            inBody = true;
            bodyBlock.Add(line.TrimEnd());
        }

        var sb = new StringBuilder();
        foreach (var line in usingBlock)
            sb.AppendLine(line);
        if (usingBlock.Count > 0)
            sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace}");
        sb.AppendLine("{");
        foreach (var line in bodyBlock)
            sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSnippetNamespace(string generatedClassName)
        => $"GeneratedSnippets.{generatedClassName}Impl";

    private static bool RequiresFlowUsing(TaskSemantic t)
    {
        return t.DataView.TabCalls.Count > 0
            || t.FlowValidations.Count > 0
            || t.Logic.RowLogics.Any(r => r.Actions.Any(a => a.Call?.OperationType == "T"))
            || HasExpandBeforeFlow(t);
    }

    private static bool HasMergeLayoutInTree(TaskSemantic t, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (HasMergeLayout(t))
            return true;
        return allTasks.Any(x => x.ParentOrdinal == t.Ordinal && HasMergeLayoutInTree(x, allTasks));
    }
}

