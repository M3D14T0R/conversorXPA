using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ApplyExpressionSelectReplacement(string expr, TaskSemantic task, Dictionary<string, string> selectMap, IReadOnlyList<DataObjectDef> dataObjects)
    {
        expr = RewriteVarLiteralPostfixTokens(expr, task, selectMap, dataObjects);
        expr = NormalizeQuotedVarPostfixSyntax(expr);
        expr = RewriteIdentifierBindingsOutsideQuotes(expr, task, selectMap, dataObjects);
        return expr;
    }

    private static string NormalizeQuotedVarPostfixSyntax(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        return Regex.Replace(
            expr,
            @"(['""])(?<name>[A-Za-z][A-Za-z0-9_]*)\1\s*VAR\b",
            "u.IndexOf(${name})",
            RegexOptions.IgnoreCase);
    }

    private static string NormalizeMalformedAdjacentArgumentSeparators(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        expr = Regex.Replace(
            expr,
            @"(?<=\))\s*(?=DNCast\s*\()",
            ", ",
            RegexOptions.IgnoreCase);

        expr = Regex.Replace(
            expr,
            @"(?<=\))\s*(?=(?:u\.)?LoopCounter\s*\()",
            ", ",
            RegexOptions.IgnoreCase);

        expr = Regex.Replace(
            expr,
            @"(?<=\))\s*(?=u\.CastTo(?:Text|Bool|Number)\s*\()",
            ", ",
            RegexOptions.IgnoreCase);

        expr = Regex.Replace(
            expr,
            "(?<=\\))\\s*(?=(?:@?\") )".Replace(" ", ""),
            ", ",
            RegexOptions.IgnoreCase);

        expr = RepairMalformedCastArgumentSeparators(expr);
        expr = RepairMalformedSingleCharComparisonLiterals(expr);
        return expr;
    }

    private static string RepairMalformedCastArgumentSeparators(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) ||
            expr.IndexOf("u.CastTo", StringComparison.Ordinal) < 0)
            return expr;

        return Regex.Replace(
            expr,
            @"\(\(\s*(?<type>(?:global::)?[A-Za-z_][A-Za-z0-9_\.:<>?]*)\s*\),\s*(?<value>u\.CastTo(?:Text|Bool|Number)\s*\([^()\r\n]*(?:\([^()\r\n]*\)[^()\r\n]*)*\))\)",
            m => $"(({m.Groups["type"].Value}){m.Groups["value"].Value})",
            RegexOptions.IgnoreCase);
    }

    private static string RepairMalformedSingleCharComparisonLiterals(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) ||
            (expr.IndexOf("== \"", StringComparison.Ordinal) < 0 &&
             expr.IndexOf("!= \"", StringComparison.Ordinal) < 0))
            return expr;

        return Regex.Replace(
            expr,
            @"(?<op>==|!=)\s*""(?<char>[()\[\]{},\\])\s*,\s*""",
            m => $"{m.Groups["op"].Value} \"{m.Groups["char"].Value}\"",
            RegexOptions.CultureInvariant);
    }

    private static string BalanceMalformedNumericExpressionParentheses(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) ||
            !expr.Contains("u.Val(", StringComparison.Ordinal))
            return expr;

        var balance = 0;
        var inString = false;
        var verbatim = false;

        for (var i = 0; i < expr.Length; i++)
        {
            var ch = expr[i];
            if (inString)
            {
                if (verbatim)
                {
                    if (ch == '"' && i + 1 < expr.Length && expr[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = false;
                        verbatim = false;
                    }

                    continue;
                }

                if (ch == '\\')
                {
                    i++;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '@' && i + 1 < expr.Length && expr[i + 1] == '"')
            {
                inString = true;
                verbatim = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '(')
                balance++;
            else if (ch == ')' && balance > 0)
                balance--;
        }

        if (balance <= 0 || balance > 2)
            return expr;

        return expr + new string(')', balance);
    }

    private static string RewriteVarLiteralPostfixTokens(
        string expr,
        TaskSemantic task,
        IReadOnlyDictionary<string, string> selectMap,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        var sb = new StringBuilder(expr.Length + 16);
        var i = 0;
        while (i < expr.Length)
        {
            if (expr[i] is not ('"' or '\''))
            {
                sb.Append(expr[i]);
                i++;
                continue;
            }

            if (!TryReadXpaLiteral(expr, i, out var endIndex, out var literalValue, out var literalCode))
            {
                sb.Append(expr[i]);
                i++;
                continue;
            }

            var cursor = endIndex + 1;
            while (cursor < expr.Length && char.IsWhiteSpace(expr[cursor]))
                cursor++;

            if (TryReadLiteralPostfixToken(expr, cursor, out var token, out var tokenEnd) &&
                (string.Equals(token, "VAR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(token, "MENU", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(token, "FORM", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(token, "VAR", StringComparison.OrdinalIgnoreCase) &&
                    IsBareIdentifier(literalValue))
                {
                    sb.Append($"u.IndexOf({literalValue})");
                }
                else if ((string.Equals(token, "MENU", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(token, "FORM", StringComparison.OrdinalIgnoreCase)) &&
                         int.TryParse(literalValue, out var numericLiteral))
                    sb.Append(numericLiteral.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(ToCSharpLiteral(literalValue));
                i = tokenEnd + 1;
                continue;
            }

            sb.Append(literalCode);
            i = endIndex + 1;
        }

        return sb.ToString();
    }

    private static string RewriteIdentifierBindingsOutsideQuotes(
        string input,
        TaskSemantic task,
        IReadOnlyDictionary<string, string> selectMap,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var allTasks = _allTasks ?? Array.Empty<TaskSemantic>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "NOT", "MOD", "TRUE", "FALSE", "NULL", "DATE", "TIME", "RIGHT", "DSOURCE", "EXP", "VAR",
            // Counter(0) is normalized by the semantic phase to the controller Counter property.
            // It must not be rebound to an application select with the same public name.
            "Counter"
        };
        var resourceMap = task.ResourcesSemantic.Ordered
            .SelectMany(resource =>
            {
                var memberName = ResolveTaskResourceMemberName(task, resource);
                var keys = new[]
                {
                    resource.Name,
                    ToLegacyVariableName(resource.Name ?? ""),
                    ToCodeIdentifierPreservingCase(resource.Name ?? ""),
                    ToPascalIdentifier(resource.Name ?? "")
                }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase);

                return keys.Select(key => new KeyValuePair<string, string>(key!, memberName));
            })
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var parentResourceMap = BuildAncestorResourceBindingMap(task, allTasks, dataObjects);
        var localMap = selectMap
            .Where(kv => IsReliableIdentifierBindingValue(task, kv.Value, dataObjects))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        AddTaskIdentifierBindings(localMap, task, "", dataObjects);
        var localResolvedMembers = BuildTaskResolvedMemberNameSet(task, dataObjects);
        var parentMap = _parentSelectMapByTaskOrdinal.TryGetValue(task.Ordinal, out var rawParentMap)
            ? rawParentMap
                .Where(kv => IsReliableIdentifierBindingValue(task, kv.Value, dataObjects))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in parentResourceMap)
        {
            if (!parentMap.ContainsKey(kv.Key))
                parentMap[kv.Key] = kv.Value;
        }
        var appMap = _applicationSelectMap
            .Where(kv => !localMap.ContainsKey(kv.Key))
            .Where(kv => !parentMap.ContainsKey(kv.Key))
            .Where(kv => string.IsNullOrWhiteSpace(ResolveExpressionOrdinalBinding(kv.Key, task, allTasks, dataObjects)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in BuildApplicationResourceBindingMap(allTasks))
        {
            if (!localMap.ContainsKey(kv.Key) && !parentMap.ContainsKey(kv.Key) && !appMap.ContainsKey(kv.Key))
                appMap[kv.Key] = kv.Value;
        }
        var localFunctionNames = task.FunctionOverridesSemantic
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => f.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accessibleParentFunctions = ResolveAccessibleParentFunctionTargets(task);
        var userMethodsMap = GetUserMethodsPublicNameMap();

        var sb = new StringBuilder(input.Length + 32);
        for (var i = 0; i < input.Length;)
        {
            if (IsQuotedSegmentStart(input, i))
            {
                var quoteStart = i;
                if (!TryReadQuotedSegmentEnd(input, i, out var quoteEnd))
                    break;
                sb.Append(input, quoteStart, (quoteEnd - quoteStart) + 1);
                i = quoteEnd + 1;
                continue;
            }

            if (!TryReadIdentifierToken(input, i, out var tokenEnd))
            {
                sb.Append(input[i]);
                i++;
                continue;
            }

            var token = input[i..tokenEnd];
            var previousChar = i > 0 ? input[i - 1] : '\0';
            var nextIndex = tokenEnd;
            while (nextIndex < input.Length && char.IsWhiteSpace(input[nextIndex]))
                nextIndex++;
            var preserveReservedToken = ShouldPreserveIdentifierReservedToken(input, i, tokenEnd, token, reserved);

            if (previousChar == '.' ||
                preserveReservedToken ||
                localResolvedMembers.Contains(token) ||
                (string.Equals(token, "Application", StringComparison.Ordinal) && nextIndex < input.Length && input[nextIndex] == '.') ||
                (string.Equals(token, "u", StringComparison.Ordinal) && nextIndex < input.Length && input[nextIndex] == '.'))
            {
                sb.Append(token);
                i = tokenEnd;
                continue;
            }

            var isFunctionInvocation = nextIndex < input.Length && input[nextIndex] == '(';
            if (isFunctionInvocation &&
                (localFunctionNames.Contains(token) ||
                 accessibleParentFunctions.ContainsKey(token) ||
                 _xpaFunctionMap.ContainsKey(token) ||
                 userMethodsMap.ContainsKey(token)))
            {
                sb.Append(token);
                i = tokenEnd;
                continue;
            }

            if (token.Length == 1 &&
                TryResolveIdentifierBinding(token, appMap) is string singleLetterAppBinding &&
                !string.IsNullOrWhiteSpace(singleLetterAppBinding))
            {
                sb.Append(singleLetterAppBinding);
                i = tokenEnd;
                continue;
            }

            var replacement =
                TryResolveIdentifierBinding(token, resourceMap) ??
                TryResolveIdentifierBinding(token, localMap) ??
                TryResolveIdentifierBinding(token, parentMap);

            if (string.IsNullOrWhiteSpace(replacement))
            {
                var ordinalBinding = ResolveExpressionOrdinalBinding(token, task, allTasks, dataObjects);
                if (IsReliableIdentifierBindingValue(task, ordinalBinding, dataObjects))
                    replacement = ordinalBinding;
            }

            replacement ??= TryResolveIdentifierBinding(token, appMap);

            if (string.IsNullOrWhiteSpace(replacement) &&
                TryResolveGluedLogicalIdentifierBinding(input, i, token, task, resourceMap, localMap, parentMap, appMap, allTasks, dataObjects, out var gluedOperator, out var gluedBinding))
            {
                sb.Append(gluedOperator);
                sb.Append(' ');
                sb.Append(gluedBinding);
            }
            else if (string.IsNullOrWhiteSpace(replacement))
            {
                var unresolvedSelectGap = TryBuildUnresolvedSelectGapToken(token, task, dataObjects);
                sb.Append(string.IsNullOrWhiteSpace(unresolvedSelectGap) ? token : unresolvedSelectGap);
            }
            else
            {
                sb.Append(replacement);
            }
            i = tokenEnd;
        }

        return sb.ToString();
    }

    private static bool ShouldPreserveIdentifierReservedToken(
        string input,
        int start,
        int end,
        string token,
        HashSet<string> reserved)
    {
        if (!reserved.Contains(token))
            return false;

        if (token.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("MOD", StringComparison.OrdinalIgnoreCase))
        {
            return HasLikelyOperandBefore(input, start) &&
                   HasLikelyOperandAfter(input, end);
        }

        if (token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            return HasLikelyOperandAfter(input, end);

        return true;
    }

    private static bool TryResolveGluedLogicalIdentifierBinding(
        string input,
        int tokenStart,
        string token,
        TaskSemantic task,
        IReadOnlyDictionary<string, string> resourceMap,
        IReadOnlyDictionary<string, string> localMap,
        IReadOnlyDictionary<string, string> parentMap,
        IReadOnlyDictionary<string, string> appMap,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string logicalOperator,
        out string binding)
    {
        logicalOperator = "";
        binding = "";

        if (TryResolveGluedLogicalIdentifierSuffix(token, "NOT", task, resourceMap, localMap, parentMap, appMap, allTasks, dataObjects, out binding))
        {
            logicalOperator = "NOT";
            return true;
        }

        if (!HasLikelyOperandBefore(input, tokenStart))
            return false;

        if (TryResolveGluedLogicalIdentifierSuffix(token, "AND", task, resourceMap, localMap, parentMap, appMap, allTasks, dataObjects, out binding))
        {
            logicalOperator = "AND";
            return true;
        }

        if (TryResolveGluedLogicalIdentifierSuffix(token, "OR", task, resourceMap, localMap, parentMap, appMap, allTasks, dataObjects, out binding))
        {
            logicalOperator = "OR";
            return true;
        }

        return false;
    }

    private static bool TryResolveGluedLogicalIdentifierSuffix(
        string token,
        string logicalOperator,
        TaskSemantic task,
        IReadOnlyDictionary<string, string> resourceMap,
        IReadOnlyDictionary<string, string> localMap,
        IReadOnlyDictionary<string, string> parentMap,
        IReadOnlyDictionary<string, string> appMap,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        out string binding)
    {
        binding = "";
        if (token.Length <= logicalOperator.Length ||
            !token.StartsWith(logicalOperator, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = token[logicalOperator.Length..];
        if (!IsAlphabeticBindingToken(suffix))
            return false;

        binding =
            TryResolveIdentifierBinding(suffix, resourceMap) ??
            TryResolveIdentifierBinding(suffix, localMap) ??
            TryResolveIdentifierBinding(suffix, parentMap) ??
            "";

        if (string.IsNullOrWhiteSpace(binding))
        {
            var ordinalBinding = ResolveExpressionOrdinalBinding(suffix, task, allTasks, dataObjects);
            if (IsReliableIdentifierBindingValue(task, ordinalBinding, dataObjects))
                binding = ordinalBinding;
        }

        binding = string.IsNullOrWhiteSpace(binding)
            ? TryResolveIdentifierBinding(suffix, appMap) ?? ""
            : binding;
        return !string.IsNullOrWhiteSpace(binding);
    }

    private static bool IsReliableIdentifierBindingValue(
        TaskSemantic task,
        string? binding,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(binding))
            return false;

        var trimmed = binding.Trim();
        if (!IsSimpleIdentifierPath(trimmed))
            return true;

        if (ResolveResourceByTargetPath(task, trimmed, _allTasks ?? Array.Empty<TaskSemantic>()) is not null)
            return true;

        return TryResolveDataViewMemberColumn(task, trimmed, out _, out _);
    }

    private static HashSet<string> BuildTaskResolvedMemberNameSet(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var memberName = ResolveTaskResourceMemberName(task, resource);
            if (!string.IsNullOrWhiteSpace(memberName))
                result.Add(memberName);
        }

        foreach (var modelMember in BuildModelMembers(task, dataObjects))
        {
            if (!string.IsNullOrWhiteSpace(modelMember.MemberName))
                result.Add(modelMember.MemberName);
        }

        return result;
    }

    private static string? TryResolveIdentifierBinding(string token, IReadOnlyDictionary<string, string> bindings)
    {
        return bindings.TryGetValue(token, out var replacement) ? replacement : null;
    }

    private static string? TryBuildUnresolvedSelectGapToken(
        string token,
        TaskSemantic task,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            !task.SelectsSemantic.ItemsByName.TryGetValue(token, out var select) ||
            select is null)
            return null;

        var resolved = ResolveSelectExpression(select, task, dataObjects, "");
        if (!string.IsNullOrWhiteSpace(resolved))
            return null;

        var dbObj = select.SourceDbObj ?? task.PrimaryDbObj ?? task.InformationDbObj;
        var dataObject = dbObj.HasValue
            ? dataObjects.FirstOrDefault(x => x.Ordinal == dbObj.Value)
            : null;
        var objectLabel = dataObject?.PublicName ??
                          dataObject?.Name ??
                          (dbObj.HasValue ? $"Obj{dbObj.Value}" : "objeto-desconhecido");
        var objectSuffix = dbObj.HasValue ? $" ({dbObj.Value})" : "";
        var attrObj = ResolveUnresolvedSelectFallbackAttrObj(select, task);
        var fallback = BuildCompilableGapFallbackExpression(attrObj);

        // Avoid embedding inline comments inside expressions. Later normalization passes
        // can rewrite around the fallback token and corrupt the comment text, yielding
        // invalid generated C# in large conditional expressions.
        return fallback;
    }

    private static string ResolveUnresolvedSelectFallbackAttrObj(TaskLogicSelectDef select, TaskSemantic task)
    {
        var resource = ResolveTaskResourceColumn(task, select.ColumnId);
        if (resource is not null)
        {
            var resourceAttr = NormalizeAttrObjKind(resource.AttrObj);
            if (!string.IsNullOrWhiteSpace(resourceAttr))
                return resourceAttr;

            var cellAttr = NormalizeAttrObjKind(resource.CellModelAttrObj);
            if (!string.IsNullOrWhiteSpace(cellAttr))
                return cellAttr;
        }

        return "";
    }

    private static string BuildCompilableGapFallbackExpression(string? attrObj)
        => BuildCompilableGapFallbackExpressionCentral(attrObj);

    private static Dictionary<string, string> BuildAncestorResourceBindingMap(
        TaskSemantic task,
        IReadOnlyList<TaskSemantic> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_ancestorResourceBindingMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parentOrdinal = task.ParentOrdinal;
        var parentPrefix = "_parent";
        while (parentOrdinal.HasValue)
        {
            var parentTask = allTasks.FirstOrDefault(t => t.Ordinal == parentOrdinal.Value);
            if (parentTask is null)
                break;

            AddTaskIdentifierBindings(map, parentTask, $"{parentPrefix}.", dataObjects);
            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "._parent";
        }

        _ancestorResourceBindingMapCache[task.Ordinal] = map;
        return map;
    }

    private static Dictionary<string, string> BuildApplicationResourceBindingMap(IReadOnlyList<TaskSemantic> allTasks)
    {
        if (_applicationResourceBindingMap is not null)
            return _applicationResourceBindingMap;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appTask = allTasks.FirstOrDefault(t => t.MainProgram) ?? allTasks.FirstOrDefault(t => t.ParentOrdinal is null);
        if (appTask is null)
        {
            _applicationResourceBindingMap = map;
            return map;
        }

        AddTaskIdentifierBindings(map, appTask, "Application.Instance.", Array.Empty<DataObjectDef>());
        _applicationResourceBindingMap = map;
        return map;
    }

    private static void AddTaskIdentifierBindings(
        Dictionary<string, string> map,
        TaskSemantic task,
        string prefix,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var memberName = ResolveTaskResourceMemberName(task, resource);
            var keys = new[]
            {
                resource.Name,
                ToLegacyVariableName(resource.Name ?? ""),
                ToCodeIdentifierPreservingCase(resource.Name ?? ""),
                ToPascalIdentifier(resource.Name ?? "")
            }
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (!map.ContainsKey(key!))
                    map[key!] = prefix + memberName;
            }
        }

        foreach (var modelMember in BuildModelMembers(task, dataObjects))
        {
            var keys = new List<string> { modelMember.MemberName };
            if (dataObjects.FirstOrDefault(d => d.Ordinal == modelMember.DbObj) is { } dataObject)
            {
                keys.Add(dataObject.Name);
                keys.Add(dataObject.PublicName ?? "");
                keys.Add(ResolveDataObjectTypeName(dataObject));
                keys.Add(ToCodeIdentifierPreservingCase(dataObject.Name ?? ""));
                keys.Add(ToPascalIdentifier(dataObject.Name ?? ""));
            }

            foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!map.ContainsKey(key!))
                    map[key!] = prefix + modelMember.MemberName;
            }
        }
    }
}

