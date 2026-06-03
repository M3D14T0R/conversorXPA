using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XpaTaskExplorer;

public static class XpaTaskTreeScanner
{
    private static readonly Regex ClassRegex = new(
        @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*:\s*(?<base>[^{]+))?",
        RegexOptions.Compiled);

    private static readonly Regex SummaryRegex = new(
        @"^\s*///\s*<summary>(?<text>.*?)</summary>\s*$",
        RegexOptions.Compiled);

    public static ObservableCollection<XpaTaskNode> ScanProjects(IEnumerable<string> projectDirectories, string? filter)
    {
        var roots = new ObservableCollection<XpaTaskNode>();
        var normalizedFilter = (filter ?? "").Trim();

        foreach (var file in EnumerateCandidateFiles(projectDirectories))
        {
            foreach (var node in ScanFile(file))
                AddIfMatches(roots, node, normalizedFilter);
        }

        return SortTree(roots);
    }

    public static IReadOnlyList<XpaTaskNode> ScanFile(string filePath)
    {
        var roots = new List<XpaTaskNode>();
        var stack = new Stack<(XpaTaskNode Node, int BodyDepth)>();
        var pending = new List<XpaTaskNode>();
        var recentSummaries = new Queue<string>();
        var depth = 0;
        var lineNumber = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            while (stack.Count > 0 && depth < stack.Peek().BodyDepth)
                stack.Pop();

            var summary = SummaryRegex.Match(line);
            if (summary.Success)
            {
                recentSummaries.Enqueue(summary.Groups["text"].Value.Trim());
                while (recentSummaries.Count > 4)
                    recentSummaries.Dequeue();
            }

            var classMatch = ClassRegex.Match(line);
            if (classMatch.Success)
            {
                var baseText = classMatch.Groups["base"].Value;
                var kind = ResolveTaskKind(baseText);
                if (!string.IsNullOrEmpty(kind))
                {
                    var className = classMatch.Groups["name"].Value;
                    var node = new XpaTaskNode
                    {
                        ClassName = className,
                        OriginalName = ResolveOriginalName(recentSummaries.LastOrDefault(), className),
                        Kind = kind,
                        FilePath = filePath,
                        Line = lineNumber
                    };

                    if (stack.Count > 0)
                        stack.Peek().Node.Children.Add(node);
                    else
                        roots.Add(node);

                    pending.Add(node);
                }

                recentSummaries.Clear();
            }

            var previousDepth = depth;
            var delta = CountBraceDelta(line);
            depth += delta;
            if (delta > 0 && pending.Count > 0)
            {
                foreach (var node in pending)
                    stack.Push((node, previousDepth + 1));
                pending.Clear();
            }
        }

        return roots;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(IEnumerable<string> projectDirectories)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in projectDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsIgnoredPath(file) || !visited.Add(file))
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsIgnoredPath(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p =>
            string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTaskKind(string baseText)
    {
        if (baseText.IndexOf("ApplicationControllerBase", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Application";
        if (baseText.IndexOf("UIControllerBase", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Online";
        if (baseText.IndexOf("BusinessProcessBase", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Batch";
        return "";
    }

    private static string ResolveOriginalName(string? summary, string className)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return className;

        var text = Regex.Replace(summary, "\\(MVP generated\\)", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(text) ? className : text;
    }

    private static void AddIfMatches(ObservableCollection<XpaTaskNode> roots, XpaTaskNode node, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || NodeMatches(node, filter))
        {
            roots.Add(node);
            return;
        }

        var clone = CloneMatching(node, filter);
        if (clone is not null)
            roots.Add(clone);
    }

    private static bool NodeMatches(XpaTaskNode node, string filter)
    {
        return node.OriginalName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
               node.ClassName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
               node.Kind.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
               node.Children.Any(child => NodeMatches(child, filter));
    }

    private static XpaTaskNode? CloneMatching(XpaTaskNode node, string filter)
    {
        var selfMatches = node.OriginalName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                          node.ClassName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                          node.Kind.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        var clone = new XpaTaskNode
        {
            ClassName = node.ClassName,
            OriginalName = node.OriginalName,
            Kind = node.Kind,
            FilePath = node.FilePath,
            Line = node.Line
        };

        foreach (var child in node.Children)
        {
            var childClone = CloneMatching(child, filter);
            if (childClone is not null)
                clone.Children.Add(childClone);
        }

        return selfMatches || clone.Children.Count > 0 ? clone : null;
    }

    private static ObservableCollection<XpaTaskNode> SortTree(IEnumerable<XpaTaskNode> nodes)
    {
        var sorted = new ObservableCollection<XpaTaskNode>();
        foreach (var node in nodes.OrderBy(n => n.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Line))
        {
            var clone = new XpaTaskNode
            {
                ClassName = node.ClassName,
                OriginalName = node.OriginalName,
                Kind = node.Kind,
                FilePath = node.FilePath,
                Line = node.Line
            };
            foreach (var child in SortTree(node.Children))
                clone.Children.Add(child);
            sorted.Add(clone);
        }

        return sorted;
    }

    private static int CountBraceDelta(string line)
    {
        var delta = 0;
        var inString = false;
        var inChar = false;
        var escape = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (!inString && !inChar && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;

            if (inString)
            {
                if (!escape && c == '"')
                    inString = false;
                escape = !escape && c == '\\';
                if (c != '\\')
                    escape = false;
                continue;
            }

            if (inChar)
            {
                if (!escape && c == '\'')
                    inChar = false;
                escape = !escape && c == '\\';
                if (c != '\\')
                    escape = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '{')
                delta++;
            else if (c == '}')
                delta--;
        }

        return delta;
    }
}
