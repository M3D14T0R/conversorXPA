using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveTaskResourceMemberName(TaskSemantic task, TaskResourceColumnDef resource)
    {
        if (_resourceMemberNameByTaskOrdinal.TryGetValue(task.Ordinal, out var byResourceId) &&
            byResourceId.TryGetValue(resource.Id, out var cachedById))
        {
            return cachedById;
        }

        var cacheKey = BuildTaskResourceMemberNameCacheKey(task, resource);
        if (_resourceMemberNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (!_resourceMemberNameCacheBuiltTaskOrdinals.Contains(task.Ordinal))
        {
            BuildTaskResourceMemberNameCache(task);
            if (_resourceMemberNameCache.TryGetValue(cacheKey, out cached))
                return cached;
        }

        var baseName = ToLegacyVariableName(resource.Name);
        if (IsReservedTaskMemberName(task, baseName))
            baseName += "_";
        var ordered = task.ResourcesSemantic.Ordered
            .Where(r =>
            {
                var candidate = ToLegacyVariableName(r.Name);
                if (IsReservedTaskMemberName(task, candidate))
                    candidate += "_";
                return string.Equals(candidate, baseName, StringComparison.Ordinal);
            })
            .ToList();
        string resolved;
        if (ordered.Count <= 1)
        {
            resolved = baseName;
            _resourceMemberNameCache[cacheKey] = resolved;
            return resolved;
        }

        var index = ordered.FindIndex(r => r.Id == resource.Id);
        if (index <= 0)
            resolved = baseName;
        else if (index == 1)
            resolved = baseName + "_";
        else
            resolved = $"{baseName}_{index}";

        _resourceMemberNameCache[cacheKey] = resolved;
        return resolved;
    }

    private static string BuildTaskResourceMemberNameCacheKey(TaskSemantic task, TaskResourceColumnDef resource)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{task.Ordinal}|{resource.Id}|{resource.Name}");

    private static void BuildTaskResourceMemberNameCache(TaskSemantic task)
    {
        var seenByBaseName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byResourceId = new Dictionary<int, string>();
        var byMemberName = new Dictionary<string, TaskResourceColumnDef>(StringComparer.Ordinal);
        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var baseName = ToLegacyVariableName(resource.Name);
            if (IsReservedTaskMemberName(task, baseName))
                baseName += "_";

            seenByBaseName.TryGetValue(baseName, out var index);
            var resolved = index switch
            {
                0 => baseName,
                1 => baseName + "_",
                _ => $"{baseName}_{index}"
            };
            seenByBaseName[baseName] = index + 1;
            _resourceMemberNameCache[BuildTaskResourceMemberNameCacheKey(task, resource)] = resolved;
            byResourceId[resource.Id] = resolved;
            byMemberName.TryAdd(resolved, resource);
        }

        _resourceMemberNameByTaskOrdinal[task.Ordinal] = byResourceId;
        _taskResourceByMemberNameCache[task.Ordinal] = byMemberName;
        _resourceMemberNameCacheBuiltTaskOrdinals.Add(task.Ordinal);
    }

    private static IReadOnlyDictionary<string, TaskResourceColumnDef> GetTaskResourcesByMemberName(TaskSemantic task)
    {
        if (!_taskResourceByMemberNameCache.TryGetValue(task.Ordinal, out var cached))
        {
            BuildTaskResourceMemberNameCache(task);
            cached = _taskResourceByMemberNameCache.TryGetValue(task.Ordinal, out var built)
                ? built
                : new Dictionary<string, TaskResourceColumnDef>(StringComparer.Ordinal);
        }

        return cached;
    }

    private static bool IsReservedTaskMemberName(TaskSemantic task, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        return GetReservedTaskMemberNames(task).Contains(candidate);
    }

    private static HashSet<string> GetReservedTaskMemberNames(TaskSemantic task)
    {
        if (_reservedTaskMemberNameCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var reserved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in ResolveFrameworkReservedTaskMemberNames(task))
            reserved.Add(name);
        reserved.Add(VariableCurrentByNameHelper);

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        reserved.Add(ResolveTaskClassName(task, allTasks));
        if (_childTasksByParentOrdinal.TryGetValue(task.Ordinal, out var children))
            foreach (var child in children)
                reserved.Add(ResolveTaskClassName(child, allTasks));

        // A resource field and a function cannot share the same member name in
        // C#. Reserve generated function names before resource names are
        // assigned so collisions such as `ultimoID` become `ultimoID_` while
        // calls to `ultimoID()` continue to target the function.
        foreach (var function in task.FunctionOverridesSemantic)
        {
            if (!string.IsNullOrWhiteSpace(function.MethodName))
                reserved.Add(function.MethodName);
        }

        _reservedTaskMemberNameCache[task.Ordinal] = reserved;
        return reserved;
    }

    private static bool IsFrameworkReservedTaskMemberName(TaskSemantic task, string candidate)
        => ResolveFrameworkReservedTaskMemberNames(task).Contains(candidate);

    private static IReadOnlySet<string> ResolveFrameworkReservedTaskMemberNames(TaskSemantic task)
    {
        var reserved = ResolveBaseClass(task) switch
        {
            "UIControllerBase" => UiControllerReservedTaskMembers,
            "BusinessProcessBase" => BusinessProcessReservedTaskMembers,
            _ => null
        };
        return reserved ?? EmptyReservedTaskMembers;
    }

    private static readonly HashSet<string> EmptyReservedTaskMembers = new(StringComparer.Ordinal);

    private static readonly HashSet<string> UiControllerReservedTaskMembers = new(StringComparer.Ordinal)
    {
        "TaskID",
        "Title",
        "From",
        "View",
        "OrderBy",
        "Activity",
        "Counter"
    };

    private static readonly HashSet<string> BusinessProcessReservedTaskMembers = new(StringComparer.Ordinal)
    {
        "TaskID",
        "Title",
        "From",
        "View",
        "OrderBy",
        "Activity",
        "Counter"
    };

    private static string ToTaskClassName(string rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription))
            return "UnnamedTask";
        var leadingMarker = Regex.Match(rawDescription, @"^\s*[-=<>\.]+\s*(.+)$");
        if (leadingMarker.Success)
        {
            var markerRaw = leadingMarker.Groups[1].Value;
            string markerName;
            if (markerRaw.Contains("=", StringComparison.Ordinal))
            {
                var parts = markerRaw.Split('=', 2, StringSplitOptions.TrimEntries);
                var left = ToPascalIdentifier(parts[0]);
                var right = parts.Length > 1 ? ToPascalIdentifier(parts[1]) : "";
                markerName = string.IsNullOrWhiteSpace(right) ? left : left + "_" + right;
            }
            else
            {
                markerName = ToPascalIdentifier(markerRaw);
            }
            if (string.IsNullOrWhiteSpace(markerName))
                return "_Unnamed";
            return "_" + markerName;
        }
        if (rawDescription.StartsWith(".", StringComparison.Ordinal))
        {
            var baseName = ToCodeIdentifierPreservingCase(rawDescription[1..]);
            return "_" + baseName;
        }
        var prefixed = Regex.Match(rawDescription.Trim(), @"^([A-Za-z]+\d+)\s*[_\-\s]+\s*(.+)$");
        if (prefixed.Success)
        {
            var prefix = ToCodeIdentifierPreservingCase(prefixed.Groups[1].Value);
            var tail = ToTaskTailIdentifier(prefixed.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(tail))
                return prefix;
            return prefix + "_" + tail;
        }
        if (Regex.IsMatch(rawDescription, @"[-_()/\[\]]"))
        {
            var id = ToTaskTailIdentifier(rawDescription);
            if (string.IsNullOrWhiteSpace(id))
                return "UnnamedTask";
            return id;
        }
        return ToPascalIdentifier(rawDescription);
    }

    private static string ToTaskTailIdentifier(string rawTail)
    {
        if (string.IsNullOrWhiteSpace(rawTail))
            return "";

        var normalized = rawTail.Trim();
        var segments = new List<(string Separator, string Value)>();
        var current = new StringBuilder();
        var pendingSeparator = "";

        void FlushCurrent()
        {
            var value = current.ToString().Trim();
            if (value.Length == 0)
                return;
            segments.Add((pendingSeparator, value));
            current.Clear();
            pendingSeparator = "";
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                current.Append(ch);
                continue;
            }

            FlushCurrent();

            if (ch is '-' or '_' or '(' or ')' or '[' or ']' or '/')
                pendingSeparator = "_";
        }

        FlushCurrent();
        if (segments.Count == 0)
            return "";

        var formattedSegments = new List<string>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var useCamel = i > 0 &&
                           segments[i].Separator == "_" &&
                           SegmentLooksLikeShortPrefix(segments[i - 1].Value) &&
                           SegmentStartsLowercase(segments[i].Value);
            var formatted = BuildSoftTaskSegmentIdentifier(segments[i].Value, useCamel);
            if (!string.IsNullOrWhiteSpace(formatted))
                formattedSegments.Add(formatted);
        }

        if (formattedSegments.Count == 0)
            return "";

        var id = string.Join("_", formattedSegments);
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    private static string BuildSoftTaskSegmentIdentifier(string rawSegment, bool camelCaseFirstToken = false)
    {
        if (string.IsNullOrWhiteSpace(rawSegment))
            return "";

        var tokens = Regex.Matches(rawSegment, "[A-Za-z0-9]+")
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0)
            return "";

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var piece = char.ToUpperInvariant(token[0]) + token[1..];
            if (i == 0)
            {
                if (camelCaseFirstToken && token.Length > 0)
                    piece = char.ToLowerInvariant(piece[0]) + piece[1..];
                sb.Append(piece);
                continue;
            }

            var previous = tokens[i - 1];
            var hasSingleLetterBridge = previous.Length == 1 && token.Length > 1;
            if (hasSingleLetterBridge)
                sb.Append('_');
            sb.Append(piece);
        }

        var result = sb.ToString();
        if (result.Length > 40)
            result = result.Substring(0, 40).TrimEnd('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "";
        return result;
    }

    private static bool SegmentLooksLikeShortPrefix(string value)
    {
        var token = Regex.Match(value ?? "", "[A-Za-z0-9]+").Value;
        return token.Length is > 0 and <= 2;
    }

    private static bool SegmentStartsLowercase(string value)
    {
        var token = Regex.Match(value ?? "", "[A-Za-z0-9]+").Value;
        return token.Length > 0 && char.IsLower(token[0]);
    }

    private static string ToEntityTypeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed";
        var trimmed = raw.Trim();
        var isUpperUnderscoreStyle = trimmed.ToUpperInvariant() == trimmed && Regex.IsMatch(trimmed, "[A-Z]");
        if (!isUpperUnderscoreStyle)
            return ToPascalIdentifier(trimmed);
        var id = Regex.Replace(trimmed, "[^A-Za-z0-9_]+", "_");
        if (string.IsNullOrWhiteSpace(id))
            id = "Unnamed";
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }


    private static string ToCodeIdentifierPreservingCase(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed";
        var trimmed = raw.Trim();
        var id = Regex.Replace(trimmed, "[^A-Za-z0-9_]+", "_");
        id = Regex.Replace(id, "_{2,}", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(id))
            id = "Unnamed";
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

}

