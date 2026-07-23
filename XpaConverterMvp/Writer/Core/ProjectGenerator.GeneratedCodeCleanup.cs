using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string NormalizeGeneratedTaskCode(string code, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeGeneratedTaskCode));
        var taskDetail = CreateNewLegacyGeneratedTaskDetail(task);
        var before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(NormalizeGeneratedCode), before, NormalizeGeneratedCode(before, taskDetail), taskDetail);

        before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(RewriteGeneratedColumnIndexOfCalls), before, RewriteGeneratedColumnIndexOfCalls(before, task), taskDetail);

        before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(RewriteGeneratedColumnLevelCalls), before, RewriteGeneratedColumnLevelCalls(before, task), taskDetail);

        before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(NormalizeGeneratedTaskCounterReferences), before, NormalizeGeneratedTaskCounterReferences(before), taskDetail);

        before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(NormalizeGeneratedTaskLocalModelReferences), before, NormalizeGeneratedTaskLocalModelReferences(before, task, dataObjects), taskDetail);

        before = code;
        code = TrackNewLegacyCleanupTreatmentIfChanged("GeneratedCleanup", nameof(NormalizeGeneratedTaskParentIoReferences), before, NormalizeGeneratedTaskParentIoReferences(before, task), taskDetail);
        return code;
    }

    private static string CreateNewLegacyGeneratedTaskDetail(TaskSemantic task)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"task={task.Ordinal}:{task.Description}");

    private static string TrackGeneratedCleanupTypingChange(string method, string original, string rewritten)
    {
        if (!string.Equals(original, rewritten, StringComparison.Ordinal))
            TrackNewLegacyCleanupTreatment("GeneratedCleanup", method);

        return rewritten;
    }

    private static string NormalizeGeneratedCode(string code, string? taskDetail = null)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeGeneratedCode));
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var before = code;
        code = code
            .Replace("\u00E2\u20AC\u2122", "\u2019", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u02DC", "\u2018", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u0153", "\u201C", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u009D", "\u201D", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u201C", "\u2013", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u201D", "\u2014", StringComparison.Ordinal)
            .Replace("\u00E2\u20AC\u00A6", "\u2026", StringComparison.Ordinal)
            .Replace("\u00C3\u00A1", "\u00E1", StringComparison.Ordinal)
            .Replace("\u00C3\u00A2", "\u00E2", StringComparison.Ordinal)
            .Replace("\u00C3\u00A3", "\u00E3", StringComparison.Ordinal)
            .Replace("\u00C3\u00A0", "\u00E0", StringComparison.Ordinal)
            .Replace("\u00C3\u00A4", "\u00E4", StringComparison.Ordinal)
            .Replace("\u00C3\u00A9", "\u00E9", StringComparison.Ordinal)
            .Replace("\u00C3\u00AA", "\u00EA", StringComparison.Ordinal)
            .Replace("\u00C3\u00AD", "\u00ED", StringComparison.Ordinal)
            .Replace("\u00C3\u00B3", "\u00F3", StringComparison.Ordinal)
            .Replace("\u00C3\u00B4", "\u00F4", StringComparison.Ordinal)
            .Replace("\u00C3\u00B5", "\u00F5", StringComparison.Ordinal)
            .Replace("\u00C3\u00B6", "\u00F6", StringComparison.Ordinal)
            .Replace("\u00C3\u00BA", "\u00FA", StringComparison.Ordinal)
            .Replace("\u00C3\u00BC", "\u00FC", StringComparison.Ordinal)
            .Replace("\u00C3\u00A7", "\u00E7", StringComparison.Ordinal)
            .Replace("\u00C3\u0081", "\u00C1", StringComparison.Ordinal)
            .Replace("\u00C3\u0082", "\u00C2", StringComparison.Ordinal)
            .Replace("\u00C3\u0083", "\u00C3", StringComparison.Ordinal)
            .Replace("\u00C3\u0080", "\u00C0", StringComparison.Ordinal)
            .Replace("\u00C3\u0089", "\u00C9", StringComparison.Ordinal)
            .Replace("\u00C3\u008A", "\u00CA", StringComparison.Ordinal)
            .Replace("\u00C3\u008D", "\u00CD", StringComparison.Ordinal)
            .Replace("\u00C3\u0093", "\u00D3", StringComparison.Ordinal)
            .Replace("\u00C3\u0094", "\u00D4", StringComparison.Ordinal)
            .Replace("\u00C3\u0095", "\u00D5", StringComparison.Ordinal)
            .Replace("\u00C3\u009A", "\u00DA", StringComparison.Ordinal)
            .Replace("\u00C3\u0087", "\u00C7", StringComparison.Ordinal)
            .Replace("\u00C2\u00B0", "\u00B0", StringComparison.Ordinal)
            .Replace("\u00C2\u00BA", "\u00BA", StringComparison.Ordinal)
            .Replace("\u00C2\u00AA", "\u00AA", StringComparison.Ordinal);
        LogGeneratedCleanupStepIfChanged(nameof(NormalizeGeneratedCode) + ".RepairMojibake", before, code, taskDetail);

        before = code;
        code = RewriteSilentSetBlobAssignments(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteSilentSetBlobAssignments), before, code, taskDetail);

        before = code;
        code = RewriteMalformedApplicationParameterIndexCalls(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteMalformedApplicationParameterIndexCalls), before, code, taskDetail);

        before = code;
        code = RewriteMalformedApplicationLevelCalls(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteMalformedApplicationLevelCalls), before, code, taskDetail);

        before = code;
        code = RewriteApplicationTextTruthiness(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteApplicationTextTruthiness), before, code, taskDetail);

        before = code;
        code = RewriteArrayColumnNullChecks(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteArrayColumnNullChecks), before, code, taskDetail);

        before = code;
        code = RewriteArrayColumnIndexOfCalls(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteArrayColumnIndexOfCalls), before, code, taskDetail);

        before = code;
        code = RewriteGeneratedColumnIndexOfCalls(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteGeneratedColumnIndexOfCalls), before, code, taskDetail);

        before = code;
        code = RewriteKnownBooleanFunctionNumericComparisons(code);
        LogGeneratedCleanupStepIfChanged(nameof(RewriteKnownBooleanFunctionNumericComparisons), before, code, taskDetail);

        before = code;
        code = NormalizeStandaloneParenthesizedInvocationStatements(code);
        LogGeneratedCleanupStepIfChanged(nameof(NormalizeStandaloneParenthesizedInvocationStatements), before, code, taskDetail);

        before = code;
        code = RepairMalformedBooleanStatementHeaders(code);
        LogGeneratedCleanupStepIfChanged(nameof(RepairMalformedBooleanStatementHeaders), before, code, taskDetail);

        before = code;
        code = CollapseDuplicateIfBlocks(code);
        LogGeneratedCleanupStepIfChanged(nameof(CollapseDuplicateIfBlocks), before, code, taskDetail);

        before = code;
        code = code.Replace("Application.AllPrograms.", "Application.Instance.AllPrograms.", StringComparison.Ordinal);
        LogGeneratedCleanupStepIfChanged("RewriteApplicationAllPrograms", before, code, taskDetail);
        return code;
    }

    private static void LogGeneratedCleanupStepIfChanged(string method, string before, string after, string? taskDetail)
    {
        if (string.Equals(before, after, StringComparison.Ordinal) ||
            AreTelemetryEquivalentExpressionForms(before, after))
            return;

        ConversionTelemetry.Log(
            "GENERATED_CLEANUP_STEP",
            string.IsNullOrWhiteSpace(taskDetail)
                ? method
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{method} {taskDetail}"));
    }

    private static string RewriteKnownBooleanFunctionNumericComparisons(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("u.VarMod(", StringComparison.Ordinal) < 0)
            return code;

        code = Regex.Replace(
            code,
            @"(?<expr>u\.VarMod\((?:[^()]|\([^()]*\))*\))\s*!=\s*0",
            "${expr}",
            RegexOptions.CultureInvariant);

        code = Regex.Replace(
            code,
            @"(?<expr>u\.VarMod\((?:[^()]|\([^()]*\))*\))\s*==\s*0",
            "u.Not(${expr})",
            RegexOptions.CultureInvariant);

        return code;
    }

    private static string NormalizeStandaloneParenthesizedInvocationStatements(string code)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeStandaloneParenthesizedInvocationStatements));
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf(");", StringComparison.Ordinal) < 0)
            return code;

        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("(", StringComparison.Ordinal) ||
                !trimmed.EndsWith(");", StringComparison.Ordinal))
                continue;

            var inner = trimmed[1..^2].Trim();
            if (!TryGetStandaloneInvocationStatementExpression(inner, out var invocation))
                continue;

            var indent = line[..(line.Length - trimmed.Length)];
            lines[i] = $"{indent}{invocation};";
            changed = true;
        }

        return changed ? string.Join(Environment.NewLine, lines) : code;
    }

    private static bool TryGetStandaloneInvocationStatementExpression(string expression, out string invocation)
    {
        invocation = "";
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (IsStatementInvocationExpression(expression))
        {
            invocation = expression.Trim();
            return true;
        }

        var comparison = SplitTopLevelComparisonExpression(expression);
        if (comparison is null ||
            !IsStatementInvocationExpression(comparison.Value.Left))
            return false;

        var right = comparison.Value.Right.Trim();
        if (right is not "\"\"" and not "0" and not "false" and not "False")
            return false;

        invocation = comparison.Value.Left.Trim();
        return true;
    }

    private static bool IsStatementInvocationExpression(string expression)
    {
        if (!TryParseFunctionCall(expression.Trim(), out var functionName, out _))
            return false;

        return functionName.StartsWith("u.", StringComparison.Ordinal) ||
               functionName.StartsWith("UserMethods.", StringComparison.Ordinal) ||
               functionName.EndsWith("Compat", StringComparison.Ordinal);
    }

    private static string RewriteArrayColumnIndexOfCalls(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("ArrayColumn<", StringComparison.Ordinal) < 0 ||
            code.IndexOf(".IndexOf(", StringComparison.Ordinal) < 0)
            return code;

        var arrayColumns = GetDeclaredArrayColumnNames(code);
        if (arrayColumns.Length == 0)
            return code;

        foreach (var name in arrayColumns)
        {
            code = Regex.Replace(
                code,
                $@"(?<![A-Za-z0-9_\.])(?<owner>[A-Za-z_][A-Za-z0-9_\.]*)\.IndexOf\(\s*(?<arg>(?:[A-Za-z_][A-Za-z0-9_]*\.)*{Regex.Escape(name)})\s*\)",
                match =>
                {
                    var owner = match.Groups["owner"].Value;
                    return string.Equals(owner, "u", StringComparison.Ordinal)
                        ? match.Value
                        : $"u.IndexOf({match.Groups["arg"].Value})";
                },
                RegexOptions.CultureInvariant);
        }

        return code;
    }

    private static string RewriteArrayColumnNullChecks(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("ArrayColumn<", StringComparison.Ordinal) < 0 ||
            code.IndexOf("u.IsNull(", StringComparison.Ordinal) < 0)
            return code;

        var arrayColumns = GetDeclaredArrayColumnNames(code);

        if (arrayColumns.Length == 0)
            return code;

        foreach (var name in arrayColumns)
        {
            code = Regex.Replace(
                code,
                $@"u\.IsNull\(\s*{Regex.Escape(name)}\s*\)",
                match =>
                {
                    var replacement = $"({name}.Value == null)";
                    ConversionTelemetry.Log(
                        "GENERATED_CLEANUP_DETAIL",
                        $"RewriteArrayColumnNullChecks from={QuoteTelemetry(match.Value)} to={QuoteTelemetry(replacement)}");
                    return replacement;
                },
                RegexOptions.CultureInvariant);
        }

        return code;
    }

    private static string RewriteGeneratedColumnIndexOfCalls(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf(".IndexOf(", StringComparison.Ordinal) < 0)
            return code;

        var columnNames = GetDeclaredGeneratedColumnNames(code);
        if (columnNames.Count == 0)
            return code;

        return Regex.Replace(
            code,
            @"(?<![A-Za-z0-9_\.])(?<owner>(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*)\.IndexOf\(\s*(?<arg>(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*)\s*\)",
            match =>
            {
                var owner = match.Groups["owner"].Value;
                if (string.Equals(owner, "u", StringComparison.Ordinal))
                    return match.Value;

                var ownerMember = owner.Split('.').LastOrDefault() ?? "";
                var argMember = match.Groups["arg"].Value.Split('.').LastOrDefault() ?? "";
                var ownerLooksLikeGeneratedColumn =
                    columnNames.Contains(ownerMember) ||
                    LooksLikeGeneratedReferenceSyntax(owner);
                if (!ownerLooksLikeGeneratedColumn || !columnNames.Contains(argMember))
                    return match.Value;

                var replacement = $"u.IndexOf({match.Groups["arg"].Value})";
                ConversionTelemetry.Log(
                    "GENERATED_CLEANUP_DETAIL",
                    $"RewriteGeneratedColumnIndexOfCalls from={QuoteTelemetry(match.Value)} to={QuoteTelemetry(replacement)}");
                return replacement;
            },
            RegexOptions.CultureInvariant);
    }

    private static string RewriteGeneratedColumnIndexOfCalls(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf(".IndexOf(", StringComparison.Ordinal) < 0)
            return code;

        var columnNames = GetDeclaredGeneratedColumnNames(code);
        return Regex.Replace(
            code,
            @"(?<![A-Za-z0-9_\.])(?<owner>(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*)\.IndexOf\(\s*(?<arg>(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*)\s*\)",
            match =>
            {
                var owner = match.Groups["owner"].Value;
                var arg = match.Groups["arg"].Value;
                if (string.Equals(owner, "u", StringComparison.Ordinal) ||
                    IsKnownNonGeneratedIndexOfOwner(owner))
                    return match.Value;

                if (!IsGeneratedIndexReference(task, owner, columnNames) ||
                    !IsGeneratedIndexReference(task, arg, columnNames))
                    return match.Value;

                var replacement = $"u.IndexOf({arg})";
                ConversionTelemetry.Log(
                    "GENERATED_CLEANUP_DETAIL",
                    $"RewriteGeneratedColumnIndexOfCalls task={task.Ordinal} from={QuoteTelemetry(match.Value)} to={QuoteTelemetry(replacement)}");
                return replacement;
            },
            RegexOptions.CultureInvariant);
    }

    private static string RewriteGeneratedColumnLevelCalls(string code, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf(".Level(", StringComparison.Ordinal) < 0)
            return code;

        var columnNames = GetDeclaredGeneratedColumnNames(code);
        return Regex.Replace(
            code,
            @"(?<![A-Za-z0-9_\.])(?<owner>(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*)\.Level\(\s*(?<level>\d+)\s*\)",
            match =>
            {
                var owner = match.Groups["owner"].Value;
                if (string.Equals(owner, "u", StringComparison.Ordinal) ||
                    IsKnownNonGeneratedIndexOfOwner(owner) ||
                    !IsGeneratedIndexReference(task, owner, columnNames))
                    return match.Value;

                return $"u.Level({match.Groups["level"].Value})";
            },
            RegexOptions.CultureInvariant);
    }

    private static bool IsGeneratedIndexReference(TaskSemantic task, string reference, HashSet<string> declaredColumnNames)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var trimmed = reference.Trim();
        var member = trimmed.Split('.').LastOrDefault() ?? trimmed;
        if (declaredColumnNames.Contains(member))
            return true;

        if (ResolveTaskResourceForAssignment(task, member, trimmed) is not null)
            return true;

        if (!string.IsNullOrWhiteSpace(ResolveModelColumnAttrObj(task, trimmed)))
            return true;

        return LooksLikeGeneratedReferenceSyntax(trimmed);
    }

    private static bool IsKnownNonGeneratedIndexOfOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return true;

        var root = owner.Split('.')[0];
        return root is "string" or "System" or "Regex" or "Path" or "File" or "Directory" or "Math" or "DateTime" or "TimeSpan";
    }

    private static bool LooksLikeGeneratedReferenceSyntax(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var segments = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = 0;
        while (index < segments.Length && string.Equals(segments[index], "_parent", StringComparison.Ordinal))
            index++;

        if (index >= segments.Length)
            return false;

        var first = segments[index];
        if (first.StartsWith("p_", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("pp_", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("pr_", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("v_", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("r_", StringComparison.OrdinalIgnoreCase))
            return true;

        if (segments.Length - index >= 2 &&
            (first.Contains('_', StringComparison.Ordinal) ||
             first.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))))
            return true;

        return false;
    }

    private static string[] GetDeclaredArrayColumnNames(string code)
    {
        return Regex.Matches(
                code,
                @"\bArrayColumn\s*<[^>]+>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(name => name.Length)
            .ToArray();
    }

    private static HashSet<string> GetDeclaredGeneratedColumnNames(string code)
    {
        return Regex.Matches(
                code,
                @"\b(?:[A-Za-z_][A-Za-z0-9_\.]*\.)?(?:ByteArray|Text|Number|Date|Time|Bool|Array)Column(?:\s*<[^>]+>)?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeGeneratedTaskCounterReferences(string code)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeGeneratedTaskCounterReferences));
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("Application.Instance.Counter_", StringComparison.Ordinal) < 0)
            return code;

        var appTask = _applicationTask;
        var hasApplicationCounterResource = appTask?.ResourcesSemantic.Ordered.Any(resource =>
            string.Equals(ResolveTaskResourceMemberName(appTask, resource), "Counter_", StringComparison.Ordinal)) == true;
        if (hasApplicationCounterResource)
            return code;

        return code.Replace("Application.Instance.Counter_", "Counter", StringComparison.Ordinal);
    }

    private static string NormalizeGeneratedTaskLocalModelReferences(string code, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeGeneratedTaskLocalModelReferences));
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("Application.Instance.", StringComparison.Ordinal) < 0)
            return code;

        code = code.Replace("Application.Instance.Application.Instance.", "Application.Instance.", StringComparison.Ordinal);

        foreach (var member in BuildModelMembers(task, dataObjects)
                     .Select(m => m.MemberName)
                     .Where(m => !string.IsNullOrWhiteSpace(m))
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(m => m.Length))
        {
            code = Regex.Replace(
                code,
                $@"(?<![A-Za-z0-9_\.])Application\.Instance\.{Regex.Escape(member)}(?=\.)",
                member,
                RegexOptions.CultureInvariant);
        }

        foreach (var modelMember in BuildModelMembers(task, dataObjects))
        {
            var dataObject = ResolveDataObjectByOrdinal(dataObjects, modelMember.DbObj);
            var keys = new[]
            {
                dataObject?.Name,
                dataObject?.PublicName,
                dataObject is null ? null : ResolveDataObjectTypeName(dataObject)
            }
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (string.Equals(key, modelMember.MemberName, StringComparison.Ordinal))
                    continue;

                code = Regex.Replace(
                    code,
                    $@"(?<![A-Za-z0-9_\.])Application\.Instance\.{Regex.Escape(key!)}(?=\.)",
                    modelMember.MemberName,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            }
        }

        return code;
    }

    private static string NormalizeGeneratedTaskParentIoReferences(string code, TaskSemantic task)
    {
        TrackLegacyExpressionTreatment("GeneratedCleanup", nameof(NormalizeGeneratedTaskParentIoReferences));
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("_parent._parent._ioReport", StringComparison.Ordinal) < 0 ||
            !task.ParentOrdinal.HasValue ||
            !_tasksByOrdinal.TryGetValue(task.ParentOrdinal.Value, out var parentTask) ||
            parentTask is null)
            return code;

        var parentStreamName = ResolveMergeStreamVariableName(parentTask);
        if (!string.Equals(parentStreamName, "_ioReport", StringComparison.Ordinal))
            return code;

        return code.Replace("_parent._parent._ioReport", "_parent._ioReport", StringComparison.Ordinal);
    }

    private static bool IsAlreadyWrappedByFunction(string code, int expressionIndex, string functionPrefix)
    {
        var prefixStart = expressionIndex - functionPrefix.Length;
        return prefixStart >= 0 &&
               string.Compare(code, prefixStart, functionPrefix, 0, functionPrefix.Length, StringComparison.Ordinal) == 0;
    }

    private static string ResolveGeneratedBindTargetAttrObj(TaskSemantic task, string target, bool allowNameInference)
    {
        var attrObj = ResolveGeneratedTaskResourceAttrObj(task, target);
        if (string.IsNullOrWhiteSpace(attrObj))
            attrObj = NormalizeAttrObjKind(ResolveModelColumnAttrObj(task, target));
        if (string.IsNullOrWhiteSpace(attrObj) && allowNameInference)
            attrObj = InferDateTimeAttrObjFromMemberName(target);
        return attrObj;
    }

    private static string ResolveGeneratedTaskResourceAttrObj(TaskSemantic task, string target)
    {
        var resource = ResolveTaskResourceForAssignment(task, null, target);
        if (resource is null)
            return "";

        var ownerTask = task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
            ? task
            : ResolveOwningTaskForResource(resource);
        if (ownerTask is null)
            return NormalizeAttrObjKind(resource.AttrObj);

        var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
        var resolvedAttrObj = NormalizeAttrObjKind(ResolveAttrObjForColumnType(resolvedType, _allFieldModels, ownerTask));
        if (!string.IsNullOrWhiteSpace(resolvedAttrObj))
            return resolvedAttrObj;

        return NormalizeAttrObjKind(ResolveEffectiveTaskResourceAttrObj(resource, ownerTask));
    }

    private static string InferDateTimeAttrObjFromMemberName(string target)
    {
        var member = target.Split('.').LastOrDefault() ?? "";
        if (member.IndexOf("Data", StringComparison.OrdinalIgnoreCase) >= 0 ||
            member.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0)
            return "FIELD_DATE";
        if (member.IndexOf("Hora", StringComparison.OrdinalIgnoreCase) >= 0 ||
            member.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0)
            return "FIELD_TIME";
        return "";
    }

    private static string RewriteSilentSetBlobAssignments(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (!trimmed.EndsWith(");", StringComparison.Ordinal) ||
                trimmed.IndexOf(".SilentSet(", StringComparison.Ordinal) < 0 ||
                trimmed.IndexOf("u.Blb2File(", StringComparison.Ordinal) < 0)
                continue;

            var callIndex = line.IndexOf(".SilentSet(", StringComparison.Ordinal);
            if (callIndex <= 0)
                continue;

            var lhs = line[..callIndex].Trim();
            if (!IsSimpleIdentifierPath(lhs))
                continue;

            var argStart = callIndex + ".SilentSet(".Length;
            var argEnd = FindMatchingParen(line, callIndex + ".SilentSet".Length);
            if (argEnd < 0)
                continue;

            var rhs = line[argStart..argEnd].Trim();
            if (!TryParseFunctionCall(rhs, out var functionName, out _) ||
                !string.Equals(functionName, "u.Blb2File", StringComparison.OrdinalIgnoreCase))
                continue;

            ConversionTelemetry.Log(
                "GENERATED_CLEANUP_DETAIL",
                $"RewriteSilentSetBlobAssignments lhs={QuoteTelemetry(lhs)} rhs={QuoteTelemetry(TruncateTelemetryValue(rhs))}");

            var indentLength = line.TakeWhile(char.IsWhiteSpace).Count();
            var indent = line[..indentLength];
            lines[i] = $"{indent}{lhs}.Value = {rhs};";
            changed = true;
        }

        return changed ? string.Join("\r\n", lines) : code;
    }

    private static string RewriteMalformedApplicationParameterIndexCalls(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("Application.Instance.", StringComparison.Ordinal) < 0)
            return code;

        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string instanceMarker = "Application.Instance.";
        var changed = false;
        var index = normalized.IndexOf(instanceMarker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var memberStart = index + instanceMarker.Length;
            var memberEnd = memberStart;
            while (memberEnd < normalized.Length &&
                   (char.IsLetterOrDigit(normalized[memberEnd]) || normalized[memberEnd] == '_'))
            {
                memberEnd++;
            }

            if (memberEnd == memberStart)
            {
                index = normalized.IndexOf(instanceMarker, index + instanceMarker.Length, StringComparison.Ordinal);
                continue;
            }

            var memberName = normalized[memberStart..memberEnd];
            if (string.Equals(memberName, "AllPrograms", StringComparison.Ordinal) ||
                string.Equals(memberName, "AllEntities", StringComparison.Ordinal) ||
                string.Equals(memberName, "AllRoles", StringComparison.Ordinal))
            {
                index = normalized.IndexOf(instanceMarker, memberEnd, StringComparison.Ordinal);
                continue;
            }

            const string callSuffix = ".IndexOf(";
            if (!StartsWithAt(normalized, memberEnd, callSuffix))
            {
                index = normalized.IndexOf(instanceMarker, memberEnd, StringComparison.Ordinal);
                continue;
            }

            var openParen = memberEnd + callSuffix.Length - 1;
            var closeParen = FindMatchingParen(normalized, openParen);
            if (closeParen < 0)
                break;

            var arguments = normalized[(openParen + 1)..closeParen];
            var splitArgs = SplitTopLevelArguments(arguments);
            if (splitArgs.Count != 1 || !IsSimpleIdentifierPath(splitArgs[0].Trim()))
            {
                index = normalized.IndexOf(instanceMarker, closeParen + 1, StringComparison.Ordinal);
                continue;
            }

            var replacement = $"u.IndexOf({splitArgs[0].Trim()})";
            ConversionTelemetry.Log(
                "GENERATED_CLEANUP_DETAIL",
                $"RewriteMalformedApplicationParameterIndexCalls member={QuoteTelemetry(memberName)} replacement={QuoteTelemetry(replacement)}");
            normalized = normalized[..index] + replacement + normalized[(closeParen + 1)..];
            changed = true;
            index = normalized.IndexOf(instanceMarker, index + "u.IndexOf(".Length, StringComparison.Ordinal);
        }

        return changed ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : code;
    }

    private static string RewriteMalformedApplicationLevelCalls(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("Application.Instance.", StringComparison.Ordinal) < 0 ||
            code.IndexOf(".Level(", StringComparison.Ordinal) < 0)
            return code;

        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string instanceMarker = "Application.Instance.";
        var changed = false;
        var index = normalized.IndexOf(instanceMarker, StringComparison.Ordinal);
        while (index >= 0)
        {
            var memberStart = index + instanceMarker.Length;
            var memberEnd = memberStart;
            while (memberEnd < normalized.Length &&
                   (char.IsLetterOrDigit(normalized[memberEnd]) || normalized[memberEnd] == '_'))
            {
                memberEnd++;
            }

            const string callSuffix = ".Level(";
            if (!StartsWithAt(normalized, memberEnd, callSuffix))
            {
                index = normalized.IndexOf(instanceMarker, memberEnd, StringComparison.Ordinal);
                continue;
            }

            var openParen = memberEnd + callSuffix.Length - 1;
            var closeParen = FindMatchingParen(normalized, openParen);
            if (closeParen < 0)
                break;

            var arguments = normalized[(openParen + 1)..closeParen].Trim();
            if (!int.TryParse(arguments, out _))
            {
                index = normalized.IndexOf(instanceMarker, closeParen + 1, StringComparison.Ordinal);
                continue;
            }

            var replacement = $"u.Level({arguments})";
            ConversionTelemetry.Log(
                "GENERATED_CLEANUP_DETAIL",
                $"RewriteMalformedApplicationLevelCalls member={QuoteTelemetry(normalized[memberStart..memberEnd])} replacement={QuoteTelemetry(replacement)}");
            normalized = normalized[..index] + replacement + normalized[(closeParen + 1)..];
            changed = true;
            index = normalized.IndexOf(instanceMarker, index + "u.Level(".Length, StringComparison.Ordinal);
        }

        return changed ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : code;
    }

    private static string RewriteApplicationTextTruthiness(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.IndexOf("Application.Instance.", StringComparison.Ordinal) < 0)
            return code;

        foreach (var member in GetApplicationTextResourceMembers())
        {
            var expr = $"Application.Instance.{member}";
            code = code.Replace($"if ({expr})", $"if ({expr} != \"\")", StringComparison.Ordinal);
            code = code.Replace($"if (({expr}))", $"if (({expr} != \"\"))", StringComparison.Ordinal);
            code = code.Replace($"u.If({expr},", $"u.If({expr} != \"\",", StringComparison.Ordinal);
        }

        return code;
    }

    private static string[] GetApplicationTextResourceMembers()
    {
        var appTask = _applicationTask;
        if (appTask is null)
            return Array.Empty<string>();

        return appTask.ResourcesSemantic.Ordered
            .Where(resource =>
            {
                var attrObj = NormalizeAttrObjKind(resource.AttrObj);
                if (string.Equals(attrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
                    return true;

                var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, appTask);
                return resolvedType.EndsWith("TextColumn", StringComparison.Ordinal) ||
                       string.Equals(resolvedType, "TextColumn", StringComparison.Ordinal);
            })
            .Select(resource => ResolveTaskResourceMemberName(appTask, resource))
            .Where(member => !string.IsNullOrWhiteSpace(member))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepairMalformedBooleanStatementHeaders(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indentLength = line.Length - trimmed.Length;
            var indent = line[..indentLength];

            string? keyword = null;
            if (trimmed.StartsWith("if ", StringComparison.Ordinal))
                keyword = "if ";
            else if (trimmed.StartsWith("else if ", StringComparison.Ordinal))
                keyword = "else if ";
            else if (trimmed.StartsWith("while ", StringComparison.Ordinal))
                keyword = "while ";

            if (keyword is null)
                continue;

            var suffix = trimmed[keyword.Length..].TrimStart();
            if (!suffix.StartsWith("(", StringComparison.Ordinal))
                continue;

            var firstParenEnd = FindMatchingParen(suffix, 0);
            if (firstParenEnd < 0 || firstParenEnd >= suffix.Length - 1)
                continue;

            var trailing = suffix[(firstParenEnd + 1)..].TrimStart();
            if (!trailing.StartsWith("&&", StringComparison.Ordinal) &&
                !trailing.StartsWith("||", StringComparison.Ordinal))
                continue;

            // Some malformed headers leak a top-level boolean tail outside the statement envelope:
            // if ((a)) || ((b))
            // Re-wrap the full condition instead of trying to rewrite the expression itself.
            lines[i] = $"{indent}{keyword}({suffix.Trim()})";
            changed = true;
        }

        return changed ? string.Join("\r\n", lines) : code;
    }

    private static string CollapseDuplicateIfBlocks(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        var duplicatePattern = "if (";
        var changed = false;
        var index = normalized.IndexOf(duplicatePattern, StringComparison.Ordinal);
        while (index >= 0)
        {
            var outerParenStart = index + 3;
            var outerParenEnd = FindMatchingParen(normalized, outerParenStart);
            if (outerParenEnd < 0)
                break;

            var outerCond = normalized[(outerParenStart + 1)..outerParenEnd].Trim();
            var outerBraceStart = SkipWhitespace(normalized, outerParenEnd + 1);
            if (outerBraceStart < 0 || outerBraceStart >= normalized.Length || normalized[outerBraceStart] != '{')
            {
                index = normalized.IndexOf(duplicatePattern, outerParenEnd + 1, StringComparison.Ordinal);
                continue;
            }

            var innerIfStart = SkipWhitespace(normalized, outerBraceStart + 1);
            if (innerIfStart < 0 || !StartsWithAt(normalized, innerIfStart, duplicatePattern))
            {
                index = normalized.IndexOf(duplicatePattern, outerBraceStart + 1, StringComparison.Ordinal);
                continue;
            }

            var innerParenStart = innerIfStart + 3;
            var innerParenEnd = FindMatchingParen(normalized, innerParenStart);
            if (innerParenEnd < 0)
                break;

            var innerCond = normalized[(innerParenStart + 1)..innerParenEnd].Trim();
            if (!string.Equals(outerCond, innerCond, StringComparison.Ordinal))
            {
                index = normalized.IndexOf(duplicatePattern, innerParenEnd + 1, StringComparison.Ordinal);
                continue;
            }

            var innerBraceStart = SkipWhitespace(normalized, innerParenEnd + 1);
            if (innerBraceStart < 0 || innerBraceStart >= normalized.Length || normalized[innerBraceStart] != '{')
            {
                index = normalized.IndexOf(duplicatePattern, innerParenEnd + 1, StringComparison.Ordinal);
                continue;
            }

            var outerIndent = GetLineIndent(normalized, index);
            var replacement = $"if ({outerCond})\n{outerIndent}{{\n";
            normalized = normalized[..index] + replacement + normalized[(innerBraceStart + 1)..];
            changed = true;
            index = normalized.IndexOf(duplicatePattern, index + replacement.Length, StringComparison.Ordinal);
        }

        return changed ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : code;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string GetLineIndent(string text, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;

        var cursor = lineStart;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]) && text[cursor] != '\n' && text[cursor] != '\r')
            cursor++;

        return text[lineStart..cursor];
    }
}

