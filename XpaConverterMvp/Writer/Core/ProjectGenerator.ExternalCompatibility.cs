using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool UsesExternalTypeCompat(ProjectSemantic parsed)
        => parsed.Tasks.Any(UsesExternalTypeCompat);

    private static bool UsesExternalTypeCompat(TaskSemantic task)
    {
        if (task.ResourceColumns.Any(c => RequiresExternalTypeCompat(c.ObjectType)))
            return true;

        return task.Expressions.Any(expr => LooksLikeExternalTypeCompatSyntax(expr.Syntax));
    }

    private static bool LooksLikeExternalTypeCompatSyntax(string? syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        return syntax.IndexOf("Cigam.Utils.Upgrade.Mail.", StringComparison.OrdinalIgnoreCase) >= 0
            || syntax.IndexOf("Cigam.WebServices.Apis.Upgrade.", StringComparison.OrdinalIgnoreCase) >= 0
            || syntax.IndexOf("Cigam.Utils.BarCode.QRCode.", StringComparison.OrdinalIgnoreCase) >= 0
            || syntax.IndexOf("DNCAST", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool UsesComponentFunctionCompat(ProjectSemantic parsed)
        => BuildComponentFunctionCompatReturnTypeMap(parsed).Count > 0;

    private static bool UsesExternalProgramCompat(ProjectSemantic parsed)
        => BuildExternalProgramCompatCalls(parsed).Count > 0;

    private static List<TaskCallDef> BuildExternalProgramCompatCalls(ProjectSemantic parsed)
    {
        var result = new List<TaskCallDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in parsed.Tasks)
        {
            foreach (var call in EnumerateTaskCalls(task))
            {
                if (!ShouldEmitExternalProgramCompat(task, call, parsed.Tasks))
                    continue;

                var compatKey = BuildExternalProgramCompatKey(call);
                if (seen.Add(compatKey))
                    result.Add(call);
            }
        }
        foreach (var call in BuildExternalSubformProgramCompatCalls(parsed))
        {
            var compatKey = BuildExternalProgramCompatKey(call);
            if (seen.Add(compatKey))
                result.Add(call);
        }

        return result;
    }

    private static IEnumerable<TaskCallDef> BuildExternalSubformProgramCompatCalls(ProjectSemantic parsed)
    {
        foreach (var task in parsed.Tasks)
        {
            foreach (var binding in task.View.SubformBindings)
            {
                if (binding.Kind != ViewSubformBindingKind.ExternalProgram)
                    continue;
                if (string.IsNullOrWhiteSpace(binding.TargetComponentName) &&
                    string.IsNullOrWhiteSpace(binding.TargetPublicName) &&
                    !binding.TargetObjectId.HasValue)
                    continue;

                yield return new TaskCallDef(
                    TaskId: null,
                    TargetComponentId: binding.TargetComponentId,
                    TargetObjectId: binding.TargetObjectId,
                    TargetComponentName: binding.TargetComponentName,
                    TargetPublicName: binding.TargetPublicName,
                    OperationType: "P",
                    ArgumentVariables: Array.Empty<string>(),
                    ArgumentDefs: Array.Empty<TaskArgumentDef>(),
                    ReturnVariable: null,
                    ReturnValue: null,
                    ConditionExpressionId: null,
                    Direction: null,
                    Modifier: null,
                    Page: null,
                    IoDeviceIndex: null,
                    FormEntryIndex: null,
                    WaitForCompletion: true,
                    Lock: null,
                    SyncData: null,
                    RetainFocus: null,
                    EventType: null,
                    EventInternalEventId: null,
                    DestSubformName: null,
                    Disabled: false,
                    FunctionName: null,
                    SnippetCode: null,
                    CompiledCode: null,
                    IsRoute: null,
                    RoutePath: null,
                    XmlTrace: null);
            }
        }
    }

    private static bool ShouldEmitExternalProgramCompat(TaskSemantic task, TaskCallDef call, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (!string.IsNullOrWhiteSpace(call.ReturnVariable) || call.ReturnValue is not null)
            return false;

        if (ResolveTaskByCall(task, call, allTasks) is not null)
            return false;

        var externalTargetType = ResolveExternalTaskTypeReference(call);
        if (!string.IsNullOrWhiteSpace(externalTargetType) &&
            !ShouldPreferExternalProgramCompatForResolvedExternalCall(call))
            return false;

        return !string.IsNullOrWhiteSpace(call.TargetComponentName)
            || !string.IsNullOrWhiteSpace(call.TargetPublicName)
            || call.TargetObjectId.HasValue
            || call.TaskId.HasValue;
    }

    private static bool ShouldPreferExternalProgramCompatForResolvedExternalCall(TaskCallDef call)
    {
        if (!string.IsNullOrWhiteSpace(call.ReturnVariable) || call.ReturnValue is not null)
            return false;

        // During an incremental task conversion the reduced XML does not contain
        // the other top-level programs. The existing full-project manifest does,
        // so a same-project call resolved through it is a real call, not an
        // external compatibility fallback.
        if (ResolveCurrentProjectProgramDetail(call) is not null)
            return false;

        if (!string.IsNullOrWhiteSpace(call.TargetComponentName) &&
            string.IsNullOrWhiteSpace(call.TargetPublicName) &&
            call.TargetObjectId.HasValue)
            return true;

        var detail = ResolveExternalProgramDetail(call);
        if (detail is null)
            return false;

        if (!detail.IsPublic)
            return true;

        return false;
    }

    private static int CountCallArguments(TaskCallDef call)
    {
        var argDefsCount = call.ArgumentDefs?.Count ?? 0;
        var argVarsCount = call.ArgumentVariables?.Count ?? 0;
        return Math.Max(argDefsCount, argVarsCount);
    }

    private static ProjectManifestProgram? ResolveExternalProgramDetail(TaskCallDef call)
    {
        var currentProjectDetail = ResolveCurrentProjectProgramDetail(call);
        if (currentProjectDetail is not null)
            return currentProjectDetail;

        if (string.IsNullOrWhiteSpace(call.TargetComponentName) ||
            !_projectReferenceManifests.TryGetValue(call.TargetComponentName, out var manifest))
        {
            return null;
        }

        ProjectManifestProgram? detail = null;
        if (!string.IsNullOrWhiteSpace(call.TargetPublicName))
        {
            detail = manifest.ProgramDetails.FirstOrDefault(p =>
                string.Equals(p.PublicName, call.TargetPublicName, StringComparison.OrdinalIgnoreCase));
        }

        if (detail is null && call.TargetObjectId.HasValue)
        {
            detail = manifest.ProgramDetails.FirstOrDefault(p =>
                p.ObjectIndex.HasValue && p.ObjectIndex.Value == call.TargetObjectId.Value);
        }
        if (detail is null)
        {
            var externalType = ResolveExternalTaskTypeReference(call);
            var externalClass = externalType?.Split('.').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(externalClass))
            {
                detail = manifest.ProgramDetails.FirstOrDefault(p =>
                    string.Equals(p.ClassName, externalClass, StringComparison.OrdinalIgnoreCase));
            }
        }

        return detail;
    }

    private static ProjectManifestProgram? ResolveCurrentProjectProgramDetail(TaskCallDef call)
    {
        if (_currentProjectManifest is null ||
            !string.IsNullOrWhiteSpace(call.TargetComponentName) ||
            !string.Equals(call.OperationType, "P", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(call.TargetPublicName))
        {
            var byPublicName = _currentProjectManifest.ProgramDetails.FirstOrDefault(p =>
                string.Equals(p.PublicName, call.TargetPublicName, StringComparison.OrdinalIgnoreCase));
            if (byPublicName is not null)
                return byPublicName;
        }

        var objectIndex = call.TargetObjectId ?? call.TaskId;
        if (!objectIndex.HasValue)
            return null;

        return _currentProjectManifest.ProgramDetails.FirstOrDefault(p =>
            p.ObjectIndex.HasValue && p.ObjectIndex.Value == objectIndex.Value);
    }

    private static IReadOnlyList<string>? ResolveExternalProgramParameterTypes(TaskCallDef call)
    {
        var detail = ResolveExternalProgramDetail(call);
        if (detail is null)
            return null;

        return detail.ParameterTypes;
    }

    private static IReadOnlyList<string>? ResolveCompatCallParameterTypes(
        TaskCallDef call,
        TaskSemantic? targetTask)
    {
        if (targetTask is not null)
        {
            var targetParameters = GetTaskParameters(targetTask);
            if (targetParameters.Count > 0)
                return targetParameters.Select(p => p.ParameterType).ToArray();
        }

        return ResolveExternalProgramParameterTypes(call);
    }

    private static IReadOnlyList<string>? ResolveCompatCallParameterDirections(
        TaskSemantic? targetTask)
    {
        if (targetTask is null)
            return null;

        var targetParameters = GetTaskParameters(targetTask);
        if (targetParameters.Count == 0)
            return null;

        return targetParameters.Select(p => p.ParameterDirection).ToArray();
    }

    private static string BuildExternalProgramCompatKey(TaskCallDef call)
    {
        return string.Join("|",
            call.TargetComponentName ?? "",
            call.TargetPublicName ?? "",
            call.TargetObjectId?.ToString() ?? "",
            call.TaskId?.ToString() ?? "",
            call.OperationType ?? "");
    }

    private static string GetExternalProgramCompatMethodName(TaskCallDef call)
    {
        var component = !string.IsNullOrWhiteSpace(call.TargetComponentName)
            ? call.TargetComponentName!
            : "External";
        var program =
            !string.IsNullOrWhiteSpace(call.TargetPublicName) ? call.TargetPublicName! :
            call.TargetObjectId.HasValue ? $"Obj{call.TargetObjectId.Value}" :
            call.TaskId.HasValue ? $"Task{call.TaskId.Value}" :
            "Unknown";
        return ToCodeIdentifierPreservingCase($"{component}_{program}");
    }

    private static Dictionary<string, string> BuildComponentFunctionReturnTypeMap(ProjectSemantic parsed)
    {
        var knownNames = parsed.ComponentFunctions
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !ReservedRuntimeFunctionNames.Contains(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (knownNames.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var attrByFunction = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var topLevelAttrByFunction = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var usageHintsByFunction = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in parsed.Tasks)
        {
            foreach (var expr in task.Expressions)
            {
                if (string.IsNullOrWhiteSpace(expr.Syntax))
                    continue;
                var syntax = expr.Syntax.Trim();
                foreach (var functionName in EnumerateKnownFunctionCalls(syntax, knownNames))
                {
                    if (!attrByFunction.TryGetValue(functionName, out var attrs))
                    {
                        attrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        attrByFunction[functionName] = attrs;
                    }
                    if (!string.IsNullOrWhiteSpace(expr.Attribute))
                        attrs.Add(expr.Attribute);

                    if (TryParseFunctionCall(syntax, out var topLevelFunctionName, out _) &&
                        string.Equals(topLevelFunctionName, functionName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!topLevelAttrByFunction.TryGetValue(functionName, out var topLevelAttrs))
                        {
                            topLevelAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            topLevelAttrByFunction[functionName] = topLevelAttrs;
                        }
                        if (!string.IsNullOrWhiteSpace(expr.Attribute))
                            topLevelAttrs.Add(expr.Attribute);
                    }

                    if (IsWrappedByValCall(syntax, functionName))
                    {
                        if (!usageHintsByFunction.TryGetValue(functionName, out var usageHints))
                        {
                            usageHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            usageHintsByFunction[functionName] = usageHints;
                        }

                        usageHints.Add("Text");
                    }

                    if (ComponentFunctionHasNumericSourceUsage(syntax, functionName))
                    {
                        if (!usageHintsByFunction.TryGetValue(functionName, out var usageHints))
                        {
                            usageHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            usageHintsByFunction[functionName] = usageHints;
                        }

                        usageHints.Add("Number");
                    }
                }
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var functionName in knownNames)
        {
            if (TryGetComponentFunctionCallContract(functionName, out var publicContract))
            {
                result[functionName] = publicContract.ReturnType;
                continue;
            }

            if (usageHintsByFunction.TryGetValue(functionName, out var usageHints))
            {
                if (usageHints.Contains("Number"))
                {
                    result[functionName] = "Number";
                    continue;
                }

                if (usageHints.Contains("Text"))
                {
                    result[functionName] = "Text";
                    continue;
                }
            }

            var chosenAttrs =
                topLevelAttrByFunction.TryGetValue(functionName, out var topLevelAttrs) && topLevelAttrs.Count > 0
                    ? topLevelAttrs
                    : attrByFunction.TryGetValue(functionName, out var attrs)
                        ? attrs
                        : null;

            if (chosenAttrs is not null)
            {
                var returnType =
                    chosenAttrs.Contains("B") || chosenAttrs.Contains("L") ? "Bool" :
                    chosenAttrs.Contains("A") ? "Text" :
                    chosenAttrs.Contains("N") ? "Number" :
                    chosenAttrs.Contains("D") ? "Date" :
                    chosenAttrs.Contains("T") ? "Time" :
                    "Text";
                result[functionName] = returnType;
                continue;
            }

            if (ShouldDefaultComponentFunctionReturnToBool(functionName))
            {
                result[functionName] = "Bool";
                continue;
            }

            result[functionName] = "Text";
        }
        return result;
    }

    private static IEnumerable<string> EnumerateKnownFunctionCalls(string syntax, ISet<string> knownNames)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < syntax.Length; i++)
        {
            var ch = syntax[i];
            if (!(ch == '_' || char.IsLetter(ch)))
                continue;
            if (i > 0 && (char.IsLetterOrDigit(syntax[i - 1]) || syntax[i - 1] == '_' || syntax[i - 1] == '.'))
                continue;

            var start = i;
            i++;
            while (i < syntax.Length && (char.IsLetterOrDigit(syntax[i]) || syntax[i] == '_'))
                i++;

            var end = i;
            while (i < syntax.Length && char.IsWhiteSpace(syntax[i]))
                i++;

            if (i >= syntax.Length || syntax[i] != '(')
            {
                i = end - 1;
                continue;
            }

            var name = syntax[start..end];
            if (knownNames.Contains(name) && yielded.Add(name))
                yield return name;
        }
    }

    private static bool IsWrappedByValCall(string syntax, string functionName)
    {
        var index = 0;
        while (index < syntax.Length)
        {
            var valIndex = IndexOfSourceFunctionName(syntax, "Val", index);
            if (valIndex < 0)
                return false;

            var cursor = valIndex + 3;
            while (cursor < syntax.Length && char.IsWhiteSpace(syntax[cursor]))
                cursor++;
            if (cursor >= syntax.Length || syntax[cursor] != '(')
            {
                index = valIndex + 3;
                continue;
            }

            cursor++;
            while (cursor < syntax.Length && char.IsWhiteSpace(syntax[cursor]))
                cursor++;

            if (MatchesFunctionCallAt(syntax, functionName, cursor))
                return true;

            index = cursor;
        }

        return false;
    }

    private static bool MatchesFunctionCallAt(string syntax, string functionName, int index)
    {
        if (index < 0 || index + functionName.Length > syntax.Length)
            return false;
        if (!syntax.AsSpan(index, functionName.Length).Equals(functionName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (index > 0 && (char.IsLetterOrDigit(syntax[index - 1]) || syntax[index - 1] == '_' || syntax[index - 1] == '.'))
            return false;

        var cursor = index + functionName.Length;
        if (cursor < syntax.Length && (char.IsLetterOrDigit(syntax[cursor]) || syntax[cursor] == '_'))
            return false;
        while (cursor < syntax.Length && char.IsWhiteSpace(syntax[cursor]))
            cursor++;
        return cursor < syntax.Length && syntax[cursor] == '(';
    }

    private static bool ShouldDefaultComponentFunctionReturnToBool(string functionName)
        => functionName.StartsWith("RuntimeIs", StringComparison.OrdinalIgnoreCase) ||
           functionName.StartsWith("Is", StringComparison.OrdinalIgnoreCase) ||
           functionName.StartsWith("Chk", StringComparison.OrdinalIgnoreCase) ||
           functionName.StartsWith("Verifica", StringComparison.OrdinalIgnoreCase);

    private static bool ComponentFunctionHasNumericSourceUsage(string sourceSyntax, string functionName)
    {
        if (string.IsNullOrWhiteSpace(sourceSyntax) || string.IsNullOrWhiteSpace(functionName))
            return false;

        var searchIndex = 0;
        while (searchIndex < sourceSyntax.Length)
        {
            var callIndex = IndexOfSourceFunctionName(sourceSyntax, functionName, searchIndex);
            if (callIndex < 0)
                return false;

            var openParen = callIndex + functionName.Length;
            while (openParen < sourceSyntax.Length && char.IsWhiteSpace(sourceSyntax[openParen]))
                openParen++;
            if (openParen >= sourceSyntax.Length || sourceSyntax[openParen] != '(')
            {
                searchIndex = callIndex + functionName.Length;
                continue;
            }

            if (!TryFindMatchingSourceParenthesis(sourceSyntax, openParen, out var closeParen))
                return false;

            if (HasNumericComparisonAfterSourceCall(sourceSyntax, closeParen + 1) ||
                HasNumericComparisonBeforeSourceCall(sourceSyntax, callIndex))
            {
                return true;
            }

            searchIndex = closeParen + 1;
        }

        return false;
    }

    private static int IndexOfSourceFunctionName(string sourceSyntax, string functionName, int startIndex)
    {
        var index = sourceSyntax.IndexOf(functionName, startIndex, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 ? '\0' : sourceSyntax[index - 1];
            var afterIndex = index + functionName.Length;
            var after = afterIndex >= sourceSyntax.Length ? '\0' : sourceSyntax[afterIndex];
            if (!IsSourceIdentifierPart(before) && !IsSourceIdentifierPart(after) && after != '.')
                return index;

            index = sourceSyntax.IndexOf(functionName, index + functionName.Length, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    private static bool TryFindMatchingSourceParenthesis(string text, int openParenIndex, out int closeParenIndex)
    {
        closeParenIndex = -1;
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (IsQuotedSegmentStart(text, i))
            {
                if (!TryReadQuotedSegmentEnd(text, i, out var quoteEnd))
                    return false;
                i = quoteEnd;
                continue;
            }

            var ch = text[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch != ')')
                continue;

            depth--;
            if (depth == 0)
            {
                closeParenIndex = i;
                return true;
            }
        }

        return false;
    }

    private static bool HasNumericComparisonAfterSourceCall(string sourceSyntax, int startIndex)
    {
        var index = SkipSourceWhiteSpace(sourceSyntax, startIndex);
        if (!TryReadSourceComparisonOperator(sourceSyntax, index, out _, out var operatorLength))
            return false;

        return IsSourceNumericLiteralAt(sourceSyntax, SkipSourceWhiteSpace(sourceSyntax, index + operatorLength), scanBackwards: false);
    }

    private static bool HasNumericComparisonBeforeSourceCall(string sourceSyntax, int callIndex)
    {
        var index = callIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(sourceSyntax[index]))
            index--;
        if (index < 0)
            return false;

        var operatorEnd = index + 1;
        var operatorStart = operatorEnd - 1;
        if (operatorStart > 0)
        {
            var maybeTwo = sourceSyntax.Substring(operatorStart - 1, 2);
            if (IsSourceComparisonOperator(maybeTwo))
                operatorStart--;
        }

        var op = sourceSyntax[operatorStart..operatorEnd];
        if (!IsSourceComparisonOperator(op))
            return false;

        return IsSourceNumericLiteralAt(sourceSyntax, operatorStart - 1, scanBackwards: true);
    }

    private static int SkipSourceWhiteSpace(string value, int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
        return index;
    }

    private static bool TryReadSourceComparisonOperator(string value, int index, out string op, out int length)
    {
        op = "";
        length = 0;
        if (index >= value.Length)
            return false;

        foreach (var candidate in new[] { "<>", ">=", "<=", "=", ">", "<" })
        {
            if (!value.AsSpan(index).StartsWith(candidate.AsSpan(), StringComparison.Ordinal))
                continue;

            op = candidate;
            length = candidate.Length;
            return true;
        }

        return false;
    }

    private static bool IsSourceComparisonOperator(string op)
        => string.Equals(op, "=", StringComparison.Ordinal) ||
           string.Equals(op, "<>", StringComparison.Ordinal) ||
           string.Equals(op, ">=", StringComparison.Ordinal) ||
           string.Equals(op, "<=", StringComparison.Ordinal) ||
           string.Equals(op, ">", StringComparison.Ordinal) ||
           string.Equals(op, "<", StringComparison.Ordinal);

    private static bool IsSourceNumericLiteralAt(string value, int index, bool scanBackwards)
    {
        if (!scanBackwards)
        {
            if (index < value.Length && (value[index] == '+' || value[index] == '-'))
                index++;
            var digitStart = index;
            while (index < value.Length && char.IsDigit(value[index]))
                index++;
            if (index < value.Length && value[index] == '.')
            {
                index++;
                while (index < value.Length && char.IsDigit(value[index]))
                    index++;
            }

            return index > digitStart && (index >= value.Length || !IsSourceIdentifierPart(value[index]));
        }

        while (index >= 0 && char.IsWhiteSpace(value[index]))
            index--;
        if (index < 0)
            return false;

        var end = index;
        while (index >= 0 && char.IsDigit(value[index]))
            index--;
        if (index >= 0 && value[index] == '.')
        {
            index--;
            while (index >= 0 && char.IsDigit(value[index]))
                index--;
        }
        if (index >= 0 && (value[index] == '+' || value[index] == '-'))
            index--;

        return end > index && (index < 0 || !IsSourceIdentifierPart(value[index]));
    }

    private static bool IsSourceIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private static Dictionary<string, string> BuildComponentFunctionCompatReturnTypeMap(ProjectSemantic parsed)
    {
        var result = BuildComponentFunctionReturnTypeMap(parsed);
        foreach (var functionName in result.Keys.ToList())
        {
            if (TryGetComponentFunctionCallContract(functionName, out _))
                result.Remove(functionName);
        }

        return result;
    }

    private static string GetComponentFunctionDefaultValueExpression(string returnType)
        => returnType switch
        {
            "Bool" => "false",
            "Number" => "0",
            "Date" => "Types.Date.Empty",
            "Time" => "Types.Time.StartOfDay",
            _ => "\"\""
        };

    private static void WriteComponentFunctionCompatAsset(string outputRoot, string appNamespace, ProjectSemantic parsed)
    {
        var returnTypeMap = BuildComponentFunctionCompatReturnTypeMap(parsed);
        if (returnTypeMap.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("using ENV;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("internal static class ComponentFunctionCompat");
        sb.AppendLine("{");
        sb.AppendLine("    static T Report<T>(string component, string functionName, T fallback)");
        sb.AppendLine("    {");
        sb.AppendLine("        Debug.WriteLine($\"GAP: Component function {component}.{functionName} not resolved\");");
        sb.AppendLine("        return fallback;");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var functionName in returnTypeMap.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var methodName = ToCodeIdentifierPreservingCase(functionName);
            var returnType = returnTypeMap[functionName];
            var defaultValue = GetComponentFunctionDefaultValueExpression(returnType);
            var componentName = _componentFunctionSourceByName.TryGetValue(functionName, out var component)
                ? component
                : "?";
            sb.AppendLine($"    internal static {returnType} {methodName}(params object[] args) => Report(\"{Escape(componentName)}\", \"{Escape(functionName)}\", {defaultValue});");
        }

        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "ComponentFunctionCompat.cs"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteExternalProgramCompatAsset(string outputRoot, string appNamespace, ProjectSemantic parsed)
    {
        var calls = BuildExternalProgramCompatCalls(parsed);
        if (calls.Count == 0)
            return;

        var methods = ReadExistingExternalProgramCompatMethods(Path.Combine(outputRoot, "ExternalProgramCompat.cs"));
        foreach (var call in calls.OrderBy(BuildExternalProgramCompatKey, StringComparer.OrdinalIgnoreCase))
        {
            var methodName = GetExternalProgramCompatMethodName(call);
            var componentName = call.TargetComponentName ?? "?";
            var programName =
                !string.IsNullOrWhiteSpace(call.TargetPublicName) ? call.TargetPublicName! :
                call.TargetObjectId?.ToString() ?? call.TaskId?.ToString() ?? "?";
            methods[methodName] = (componentName, programName);
        }

        var sb = new StringBuilder();
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("internal static class ExternalProgramCompat");
        sb.AppendLine("{");
        sb.AppendLine("    static void Report(string component, string program)");
        sb.AppendLine("    {");
        sb.AppendLine("        Debug.WriteLine($\"GAP: External program {component}.{program} not resolved\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var method in methods.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"    internal static void {method.Key}(params object[] args) => Report(\"{Escape(method.Value.Component)}\", \"{Escape(method.Value.Program)}\");");
        }

        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "ExternalProgramCompat.cs"), sb.ToString(), Encoding.UTF8);
    }

    private static Dictionary<string, (string Component, string Program)> ReadExistingExternalProgramCompatMethods(string path)
    {
        var result = new Dictionary<string, (string Component, string Program)>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        var text = File.ReadAllText(path);
        foreach (Match match in Regex.Matches(text, @"internal\s+static\s+void\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*params\s+object\[\]\s+args\s*\)\s*=>\s*Report\(""([^""]*)"",\s*""([^""]*)""\);"))
        {
            var methodName = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(methodName))
                continue;
            result[methodName] = (match.Groups[2].Value, match.Groups[3].Value);
        }

        return result;
    }

    private static void WriteExternalTypeCompatAsset(string outputRoot, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Dynamic;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("internal static class ExternalTypeCompat");
        sb.AppendLine("{");
        sb.AppendLine("    internal static dynamic Create(string typeName, params object[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (TryCreateReal(typeName, args, out var instance))");
        sb.AppendLine("            return instance;");
        sb.AppendLine();
        sb.AppendLine("        Debug.WriteLine($\"GAP: External type {typeName} not resolved\");");
        sb.AppendLine("        return new MissingExternalDynamic(typeName);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static object InvokeStatic(string typeName, string methodName, params object[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (TryInvokeStaticReal(typeName, methodName, args, out var result))");
        sb.AppendLine("            return result;");
        sb.AppendLine();
        sb.AppendLine("        Debug.WriteLine($\"GAP: External static method {typeName}.{methodName} not resolved\");");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static T ConvertTo<T>(object value)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (value is T typed)");
        sb.AppendLine("            return typed;");
        sb.AppendLine("        return (T)ConvertArgument(value, typeof(T));");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static bool TryCreateReal(string typeName, object[] args, out object instance)");
        sb.AppendLine("    {");
        sb.AppendLine("        instance = null;");
        sb.AppendLine("        if (string.IsNullOrWhiteSpace(typeName))");
        sb.AppendLine("            return false;");
        sb.AppendLine();
        sb.AppendLine("        if (typeName.IndexOf(\"(\", StringComparison.Ordinal) >= 0)");
        sb.AppendLine("            return false;");
        sb.AppendLine();
        sb.AppendLine("        if (!TryResolveRuntimeType(typeName, out var runtimeType))");
        sb.AppendLine("            return false;");
        sb.AppendLine();
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            instance = Activator.CreateInstance(runtimeType, args);");
        sb.AppendLine("            return instance is not null;");
        sb.AppendLine("        }");
        sb.AppendLine("        catch");
        sb.AppendLine("        {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static bool TryInvokeStaticReal(string typeName, string methodName, object[] args, out object result)");
        sb.AppendLine("    {");
        sb.AppendLine("        result = null;");
        sb.AppendLine("        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))");
        sb.AppendLine("            return false;");
        sb.AppendLine("        if (!TryResolveRuntimeType(typeName, out var runtimeType))");
        sb.AppendLine("            return false;");
        sb.AppendLine();
        sb.AppendLine("        foreach (var method in runtimeType.GetMethods(BindingFlags.Public | BindingFlags.Static))");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("                continue;");
        sb.AppendLine("            var parameters = method.GetParameters();");
        sb.AppendLine("            if (parameters.Length != args.Length)");
        sb.AppendLine("                continue;");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                var converted = ConvertArguments(args, parameters);");
        sb.AppendLine("                result = method.Invoke(null, converted);");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static bool TryResolveRuntimeType(string typeName, out Type runtimeType)");
        sb.AppendLine("    {");
        sb.AppendLine("        runtimeType = Type.GetType(typeName, throwOnError: false);");
        sb.AppendLine("        if (runtimeType is not null)");
        sb.AppendLine("            return true;");
        sb.AppendLine();
        sb.AppendLine("        runtimeType = AppDomain.CurrentDomain.GetAssemblies()");
        sb.AppendLine("            .Select(a => a.GetType(typeName, throwOnError: false))");
        sb.AppendLine("            .FirstOrDefault(t => t is not null);");
        sb.AppendLine("        if (runtimeType is not null)");
        sb.AppendLine("            return true;");
        sb.AppendLine();
        sb.AppendLine("        foreach (var file in Directory.EnumerateFiles(AppDomain.CurrentDomain.BaseDirectory, \"*.dll\"))");
        sb.AppendLine("        {");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                var assembly = Assembly.LoadFrom(file);");
        sb.AppendLine("                runtimeType = assembly.GetType(typeName, throwOnError: false);");
        sb.AppendLine("                if (runtimeType is not null)");
        sb.AppendLine("                    return true;");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static object[] ConvertArguments(object[] args, ParameterInfo[] parameters)");
        sb.AppendLine("    {");
        sb.AppendLine("        var converted = new object[args.Length];");
        sb.AppendLine("        for (var i = 0; i < args.Length; i++)");
        sb.AppendLine("            converted[i] = ConvertArgument(args[i], parameters[i].ParameterType);");
        sb.AppendLine("        return converted;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static object ConvertArgument(object arg, Type targetType)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (arg is null)");
        sb.AppendLine("            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null ? Activator.CreateInstance(targetType) : null;");
        sb.AppendLine("        if (targetType.IsInstanceOfType(arg))");
        sb.AppendLine("            return arg;");
        sb.AppendLine("        var nullable = Nullable.GetUnderlyingType(targetType);");
        sb.AppendLine("        if (nullable is not null)");
        sb.AppendLine("            targetType = nullable;");
        sb.AppendLine("        if (targetType == typeof(string))");
        sb.AppendLine("            return arg.ToString();");
        sb.AppendLine("        if (targetType.IsEnum)");
        sb.AppendLine("            return Enum.Parse(targetType, arg.ToString(), ignoreCase: true);");
        sb.AppendLine("        return Convert.ChangeType(arg, targetType);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    sealed class MissingExternalDynamic : DynamicObject");
        sb.AppendLine("    {");
        sb.AppendLine("        readonly string _typeName;");
        sb.AppendLine();
        sb.AppendLine("        internal MissingExternalDynamic(string typeName)");
        sb.AppendLine("        {");
        sb.AppendLine("            _typeName = typeName;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override bool TryGetMember(GetMemberBinder binder, out object result)");
        sb.AppendLine("        {");
        sb.AppendLine("            result = new MissingExternalDynamic($\"{_typeName}.{binder.Name}\");");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override bool TrySetMember(SetMemberBinder binder, object value) => true;");
        sb.AppendLine();
        sb.AppendLine("        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)");
        sb.AppendLine("        {");
        sb.AppendLine("            result = binder.Name.StartsWith(\"Get\", StringComparison.OrdinalIgnoreCase) ||");
        sb.AppendLine("                     binder.Name.IndexOf(\"Url\", StringComparison.OrdinalIgnoreCase) >= 0 ||");
        sb.AppendLine("                     binder.Name.IndexOf(\"Token\", StringComparison.OrdinalIgnoreCase) >= 0");
        sb.AppendLine("                ? string.Empty");
        sb.AppendLine("                : new MissingExternalDynamic($\"{_typeName}.{binder.Name}\");");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override bool TryInvoke(InvokeBinder binder, object[] args, out object result)");
        sb.AppendLine("        {");
        sb.AppendLine("            result = new MissingExternalDynamic(_typeName);");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override bool TryConvert(ConvertBinder binder, out object result)");
        sb.AppendLine("        {");
        sb.AppendLine("            var targetType = binder.Type;");
        sb.AppendLine("            if (targetType == typeof(string))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = string.Empty;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(bool))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = false;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(byte))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = (byte)0;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(short))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = (short)0;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(int))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = 0;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(long))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = 0L;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(float))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = 0f;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(double))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = 0d;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(decimal))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = 0m;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("            var nullableUnderlying = Nullable.GetUnderlyingType(targetType);");
        sb.AppendLine("            if (nullableUnderlying is not null)");
        sb.AppendLine("            {");
        sb.AppendLine("                result = null;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType.IsEnum)");
        sb.AppendLine("            {");
        sb.AppendLine("                result = Enum.GetValues(targetType).Cast<object>().FirstOrDefault();");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(DateTime))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = default(DateTime);");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType == typeof(Guid))");
        sb.AppendLine("            {");
        sb.AppendLine("                result = Guid.Empty;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            if (targetType.IsValueType)");
        sb.AppendLine("            {");
        sb.AppendLine("                result = Activator.CreateInstance(targetType);");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            result = new MissingExternalDynamic(_typeName);");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public override string ToString() => string.Empty;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(outputRoot, "ExternalTypeCompat.cs"), sb.ToString(), Encoding.UTF8);
    }



    private static bool IsPublicTopLevelTask(TaskSemantic task)
    {
        return (!string.IsNullOrWhiteSpace(_targetComponent)
                || !string.IsNullOrWhiteSpace(task.SourceComponent)
                || string.Equals(_outputType, "Library", StringComparison.OrdinalIgnoreCase))
               && !task.ParentOrdinal.HasValue;
    }

}

