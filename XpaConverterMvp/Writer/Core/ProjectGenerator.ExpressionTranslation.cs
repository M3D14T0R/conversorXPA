using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private const string XpaLikeOperandPattern =
        @"(?:\([^\(\)]*\)|@?""(?:[^""]|"""")*""|'[^']*'|[_A-Za-z][A-Za-z0-9_\.]*(?:\([^\(\)]*\))?|[-+]?\d+(?:\.\d+)?)";

    private static readonly Regex XpaPowOperatorRegex = new(
        @"(?<left>(?:[-+]?\s*(?:\([^\(\)]*\)|[_A-Za-z][A-Za-z0-9_\.]*(?:\([^\(\)]*\))?|[-+]?\d+(?:\.\d+)?)))\s*\^\s*(?<right>(?:[-+]?\s*(?:\([^\(\)]*\)|[_A-Za-z][A-Za-z0-9_\.]*(?:\([^\(\)]*\))?|[-+]?\d+(?:\.\d+)?)))",
        RegexOptions.Compiled);

    private static readonly Regex XpaNotLikeOperatorRegex = new(
        $@"(?<left>{XpaLikeOperandPattern})\s+NOT\s+LIKE\s+(?<right>{XpaLikeOperandPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex XpaLikeOperatorRegex = new(
        $@"(?<left>{XpaLikeOperandPattern})\s+LIKE\s+(?<right>{XpaLikeOperandPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string RewriteIndexLiteralsForOrderBy(
        string rawSyntax,
        string translatedExpression,
        DataObjectDef entity,
        string entityMember)
    {
        if (string.IsNullOrWhiteSpace(rawSyntax) ||
            string.IsNullOrWhiteSpace(translatedExpression) ||
            rawSyntax.IndexOf("INDEX", StringComparison.OrdinalIgnoreCase) < 0)
            return translatedExpression;

        var replacements = Regex.Matches(rawSyntax, "['\\\"](?<id>\\d+)['\\\"]\\s*INDEX", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => BuildIndexReferenceExpression(entity, entityMember, int.Parse(m.Groups["id"].Value)))
            .ToList();
        if (replacements.Count == 0)
            return translatedExpression;

        var rewritten = translatedExpression;

        var literalQueue = new Queue<string>(replacements);
        rewritten = Regex.Replace(
            rewritten,
            "['\\\"](?<id>\\d+)['\\\"]\\s*INDEX",
            m => literalQueue.Count > 0 ? literalQueue.Dequeue() : m.Value,
            RegexOptions.IgnoreCase);

        if (!rewritten.Contains("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            var numericQueue = new Queue<string>(replacements);
            rewritten = Regex.Replace(
                rewritten,
                @"(?<=^|,)\s*(?<n>\d+)\s*(?=,|\))",
                m => numericQueue.Count > 0 ? numericQueue.Dequeue() : m.Value);
        }

        return rewritten;
    }

    private static string BuildIndexReferenceExpression(DataObjectDef entity, string entityMember, int indexId)
    {
        return indexId.ToString(CultureInfo.InvariantCulture);
    }

    private static string ApplyXpaNumericOperators(string expr)
    {
        if (expr.IndexOf('^') < 0)
            return expr;

        // XPA exponentiation operator: a ^ b -> u.Pow(a, b)
        for (var i = 0; i < 32; i++)
        {
            var replaced = XpaPowOperatorRegex.Replace(expr, m => $"u.Pow({m.Groups["left"].Value.Trim()}, {m.Groups["right"].Value.Trim()})");
            if (string.Equals(replaced, expr, StringComparison.Ordinal))
                break;
            expr = replaced;
        }
        return expr;
    }

    private static string ApplyXpaLikeOperators(string expr)
    {
        if (expr.IndexOf("LIKE", StringComparison.OrdinalIgnoreCase) < 0)
            return expr;

        // XPA infix LIKE/NOT LIKE operators:
        //   a LIKE b     -> u.Like(a, b)
        //   a NOT LIKE b -> u.Not(u.Like(a, b))
        // Keep this conservative to avoid rewriting unrelated tokens.
        for (var i = 0; i < 32; i++)
        {
            var replaced = XpaNotLikeOperatorRegex.Replace(expr, m => $"u.Not(u.Like({m.Groups["left"].Value}, {m.Groups["right"].Value}))");
            replaced = XpaLikeOperatorRegex.Replace(replaced, m => $"u.Like({m.Groups["left"].Value}, {m.Groups["right"].Value})");
            if (string.Equals(replaced, expr, StringComparison.Ordinal))
                break;
            expr = replaced;
        }

        return expr;
    }

    private static string BuildBindValueSuffix(string bindExpr, TaskSemantic? task = null)
    {
        if (LooksLikeTaskRunConstruction(bindExpr))
            return $".BindValueToColumnChange(() => {bindExpr})";
        var simpleMember = IsSimpleMemberPath(bindExpr);
        var hasOperatorsOrCalls = ContainsDynamicOperators(bindExpr);
        var isNowProperty = bindExpr.EndsWith(".Now", StringComparison.Ordinal);
        if (simpleMember && !hasOperatorsOrCalls)
        {
            if (isNowProperty)
                return $".BindValue(() => {bindExpr})";
            return $".BindValue({bindExpr})";
        }
        return $".BindValue(() => {bindExpr})";
    }

    private static bool IsResourceDisplayedInView(TaskSemantic task, TaskResourceColumnDef resource)
    {
        var legacy = ResolveTaskResourceMemberName(task, resource);
        foreach (var control in task.View.SelectedFormControls)
        {
            if (string.IsNullOrWhiteSpace(control.DataColumn))
                continue;

            var resolved = ResolveDataColumnOrdinalBinding(control.DataColumn, task, _allTasks ?? Array.Empty<TaskSemantic>());
            if (string.Equals(resolved, legacy, StringComparison.Ordinal))
                return true;

            if (task.ResourcesSemantic.ByName.TryGetValue(control.DataColumn, out var byName) &&
                byName.Id == resource.Id)
                return true;
        }
        return false;
    }


    private static string ResolveExpressionOrdinalBinding(string token, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";
        var normalized = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalized))
            return "";

        var directSelect = task.SelectsSemantic.Items
            .FirstOrDefault(s => string.Equals(s.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (directSelect is null &&
            task.SelectsSemantic.ItemsByName.TryGetValue(normalized, out var mappedSelect) &&
            mappedSelect is not null)
            directSelect = mappedSelect;

        if (directSelect is not null)
        {
            var directExpr = ResolveSelectExpression(directSelect, task, dataObjects, "");
            if (!string.IsNullOrWhiteSpace(directExpr))
                return directExpr;
        }

        if (_parentSelectMapByTaskOrdinal.TryGetValue(task.Ordinal, out var parentSelectMap) &&
            parentSelectMap.TryGetValue(normalized, out var parentSelectExpr) &&
            !string.IsNullOrWhiteSpace(parentSelectExpr))
        {
            return parentSelectExpr;
        }

        long slotLong = 0;
        foreach (var ch in normalized)
        {
            slotLong = (slotLong * 26) + (ch - 'A' + 1);
            if (slotLong > int.MaxValue)
                return "";
        }

        var slot = (int)slotLong;

        // In task expressions, single-letter ordinal bindings are offset from C:
        // C -> column 1, D -> column 2, etc.
        var columnIndex = slot - 2;
        if (columnIndex <= 0)
        {
            var applicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            return applicationBinding ?? "";
        }

        var minLocalSelectSlot = task.SelectsSemantic.Items
            .Select(s => ToAlphabeticSlot(s.Name))
            .Where(slotValue => slotValue > 0)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        if (slot < minLocalSelectSlot && minLocalSelectSlot != int.MaxValue)
        {
            if (task.ParentOrdinal.HasValue)
            {
                var parentBinding = ResolveOrdinalBindingFromParentChain(columnIndex, task, allTasks);
                if (!string.IsNullOrWhiteSpace(parentBinding))
                    return parentBinding;
            }

            var applicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            if (!string.IsNullOrWhiteSpace(applicationBinding))
                return applicationBinding;
        }

        if (columnIndex <= task.ResourcesSemantic.Ordered.Count)
            return ResolveTaskResourceMemberName(task, task.ResourcesSemantic.Ordered[columnIndex - 1]);

        var fallbackParentBinding = ResolveOrdinalBindingFromParentChain(columnIndex, task, allTasks);
        if (!string.IsNullOrWhiteSpace(fallbackParentBinding))
            return fallbackParentBinding;

        // Application-level single-letter aliases are valid only as a fallback. Local
        // task resources and parent resources must win, especially for source VAR
        // literals that feed u.IndexOf/u.Level-style runtime calls.
        if (normalized.Length == 1)
        {
            var applicationSelectMap = BuildApplicationSelectMap(allTasks, dataObjects);
            if (applicationSelectMap.TryGetValue(normalized, out var singleLetterApplicationSelectBinding) &&
                !string.IsNullOrWhiteSpace(singleLetterApplicationSelectBinding))
                return singleLetterApplicationSelectBinding;

            var singleLetterApplicationBinding = ResolveApplicationOrdinalBinding(slot, allTasks);
            if (!string.IsNullOrWhiteSpace(singleLetterApplicationBinding))
                return singleLetterApplicationBinding!;
        }

        return "";
    }

    private static bool IsAlphabeticBindingToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (ch < 'A' || ch > 'Z')
                return false;
        }
        return true;
    }

    private static string? ResolveApplicationOrdinalBinding(int slot, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (slot <= 0)
            return null;

        var appTask = _applicationTask;
        if (appTask is null)
            return null;

        if (slot <= appTask.ResourcesSemantic.Ordered.Count)
        {
            var memberName = ResolveTaskResourceMemberName(appTask, appTask.ResourcesSemantic.Ordered[slot - 1]);
            return IsRuntimeCounterBinding(appTask.ResourcesSemantic.Ordered[slot - 1].Name ?? "", memberName)
                ? "Counter"
                : $"Application.Instance.{memberName}";
        }

        return null;
    }

    private static string ResolveOrdinalBindingFromParentChain(int columnIndex, TaskSemantic task, IReadOnlyList<TaskSemantic> allTasks)
    {
        if (columnIndex <= 0)
            return "";

        var parentPrefix = "_parent";
        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, allTasks);
            if (parentTask is null)
                break;

            if (columnIndex <= parentTask.ResourcesSemantic.Ordered.Count)
                return $"{parentPrefix}.{ResolveTaskResourceMemberName(parentTask, parentTask.ResourcesSemantic.Ordered[columnIndex - 1])}";

            parentOrdinal = parentTask.ParentOrdinal;
            parentPrefix += "._parent";
        }

        return "";
    }

    private static int ToAlphabeticSlot(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return 0;
        var normalized = token.Trim().ToUpperInvariant();
        if (!IsAlphabeticBindingToken(normalized))
            return 0;
        long slot = 0;
        foreach (var ch in normalized)
        {
            slot = (slot * 26) + (ch - 'A' + 1);
            if (slot > int.MaxValue)
                return 0;
        }
        return (int)slot;
    }

    private static string TranslateXpaExpressionToCSharp(string xpaExpr, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, string? expressionAttr = null)
    {
        if (string.IsNullOrWhiteSpace(xpaExpr))
            return "";
        var expr = WebUtility.HtmlDecode(xpaExpr);
        if (TryParseWholeXpaSingleQuotedLiteral(expr, out var literalText))
            return ToCSharpLiteral(literalText);
        var selectMap = BuildSelectNameToExpressionMap(task, dataObjects);
        expr = ApplyExpressionSelectReplacement(expr, task, selectMap, dataObjects);
        expr = ApplyLegacyExpressionAliasReplacement(expr, task, dataObjects);
        expr = ApplyExpressionOperatorNormalization(expr);
        expr = NormalizeMalformedNotFunctionCalls(expr);
        expr = ApplyExpressionLiteralNormalization(expr, dataObjects, expressionAttr);
        expr = RenderRegisteredExpressionCalculation(expr);
        if (TryTranslateExternalStaticDotNetMethodCall(expr.Trim(), task, dataObjects, out var externalStaticCall))
            return externalStaticCall;
        expr = ApplyXpaFunctionMap(expr, task, expressionAttr);
        var expectedReturnType = ResolveSimpleReturnTypeForExpressionAttribute(expressionAttr);
        if (TryTranslateWholeKnownSourceFunctionCall(expr, task, dataObjects, out var typedSourceExpression, expectedReturnType))
            expr = typedSourceExpression.Code;
        expr = RenderArrayIsNullCallsFromEvidence(expr, task);
        expr = ApplyXpaNumericOperators(expr);
        expr = ApplyXpaLikeOperators(expr);
        expr = ApplyExpressionRuntimeNormalization(expr);
        expr = RewriteDnCastExpressions(expr);
        expr = RewriteDnSetExpressions(expr);
        expr = RenderDotNetConstructorLikeExecuteCalls(expr);
        expr = RestoreApplicationQuoteDelimiterMisbindings(expr, task, expressionAttr, dataObjects);
        expr = ApplyExpressionFormattingNormalization(expr);
        expr = NormalizeCSharpStringLiterals(expr);
        expr = NormalizeMalformedAdjacentArgumentSeparators(expr);
        expr = RenderWinHandleInteropCallsFromFunctionContract(expr, task);
        expr = RewriteDnCastExpressions(expr);
        expr = BalanceMalformedNumericExpressionParentheses(expr);
        expr = RewriteDnRefExpressions(expr);
        expr = expr.Trim();

        expr = NormalizeTimeFormattingExpression(expr, task, dataObjects);
        expr = NormalizeDateConstructorMappings(expr);
        expr = NormalizeDotNetArrayConstructorMappings(expr);
        expr = RenderTypeValuedCaseExpressions(expr);
        return expr;
    }

    private static string RenderArrayIsNullCallsFromEvidence(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            expression.IndexOf("IsNull", StringComparison.OrdinalIgnoreCase) < 0)
            return expression;

        string? RewriteIsNull(List<string> args)
        {
            if (args.Count != 1)
                return null;

            var argument = StripRedundantOuterParentheses(args[0].Trim());
            if (string.IsNullOrWhiteSpace(argument) || !IsSimpleIdentifierPath(argument))
                return null;

            var targetInfo = ResolveTargetValueInfo(task, null, argument);
            return targetInfo.IsArray || IsArrayTargetByDirectResourceEvidence(task, argument)
                ? $"({argument}.Value == null)"
                : null;
        }

        var rewritten = RewriteFunctionCalls(expression, "u.IsNull", RewriteIsNull);
        return RewriteFunctionCalls(rewritten, "IsNull", RewriteIsNull);
    }

    private static bool IsArrayTargetByDirectResourceEvidence(TaskSemantic task, string target)
    {
        var normalizedTarget = StripRedundantOuterParentheses(target.Trim());
        if (normalizedTarget.EndsWith(".Value", StringComparison.Ordinal))
            normalizedTarget = normalizedTarget[..^".Value".Length];

        foreach (var resource in task.ResourcesSemantic.Ordered)
        {
            var memberName = ResolveTaskResourceMemberName(task, resource);
            if (!MatchesResourceTargetName(resource, memberName, normalizedTarget))
                continue;

            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, task);
            return resolvedType.StartsWith("ArrayColumn<", StringComparison.Ordinal);
        }

        if (ResolveResourceByTargetPath(task, normalizedTarget, _allTasks ?? Array.Empty<TaskSemantic>()) is not { } pathResource)
            return false;

        var ownerTask = ResolveOwningTaskForResource(pathResource);
        if (ownerTask is null)
            return false;

        var pathResolvedType = ResolveTaskResourceColumnType(pathResource, _allFieldModels, ownerTask);
        return pathResolvedType.StartsWith("ArrayColumn<", StringComparison.Ordinal);
    }

    private static bool MatchesResourceTargetName(TaskResourceColumnDef resource, string memberName, string target)
    {
        if (string.Equals(memberName, target, StringComparison.Ordinal))
            return true;

        return string.Equals(ToCodeIdentifierPreservingCase(resource.Name ?? ""), target, StringComparison.Ordinal) ||
               string.Equals(ToPascalIdentifier(resource.Name ?? ""), target, StringComparison.Ordinal) ||
               string.Equals(ToLegacyVariableName(resource.Name ?? ""), target, StringComparison.Ordinal);
    }

    private static string RenderTypeValuedCaseExpressions(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var stripped = StripRedundantOuterParentheses(expression.Trim());
        if (stripped.IndexOf("u.Case", StringComparison.Ordinal) < 0 &&
            stripped.IndexOf("u.CaseUntyped", StringComparison.Ordinal) < 0)
        {
            return stripped;
        }

        if (!TryParseFunctionCall(stripped, out var functionName, out var args) ||
            args.Count == 0)
        {
            return stripped;
        }

        var changed = false;
        for (var i = 0; i < args.Count; i++)
        {
            var renderedArg = RenderTypeValuedCaseExpressions(args[i].Trim());
            changed |= !string.Equals(renderedArg, args[i].Trim(), StringComparison.Ordinal);
            args[i] = renderedArg;
        }

        if ((IsTopLevelCall(functionName, "u.Case") ||
             IsTopLevelCall(functionName, "u.CaseUntyped")) &&
            BuildTypeValuedCaseExpression(args, out var typeCase))
        {
            return typeCase;
        }

        return changed
            ? $"{functionName}({string.Join(", ", args)})"
            : stripped;
    }

    private static bool BuildTypeValuedCaseExpression(IReadOnlyList<string> args, out string expression)
    {
        expression = "";
        if (args.Count < 4 || args.Count % 2 != 0)
            return false;

        var selector = StripRedundantOuterParentheses(args[0].Trim());
        if (string.IsNullOrWhiteSpace(selector) || !IsTypeOfExpression(args[^1]))
            return false;

        for (var i = 2; i < args.Count - 1; i += 2)
        {
            if (!IsTypeOfExpression(args[i]))
                return false;
        }

        var fallback = args[^1].Trim();
        for (var valueIndex = args.Count - 3; valueIndex >= 1; valueIndex -= 2)
        {
            var value = args[valueIndex].Trim();
            var result = args[valueIndex + 1].Trim();
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(result))
                return false;

            fallback = $"({selector} == {value} ? {result} : {fallback})";
        }

        expression = fallback;
        return true;
    }

    private static string RenderRegisteredExpressionCalculation(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) ||
            !expr.Contains("ExpCalc", StringComparison.OrdinalIgnoreCase))
            return expr;

        var current = expr;
        for (var i = 0; i < current.Length;)
        {
            if (IsQuotedSegmentStart(current, i))
            {
                if (!TryReadQuotedSegmentEnd(current, i, out var quoteEnd))
                    return current;

                i = quoteEnd + 1;
                continue;
            }

            const string functionName = "ExpCalc";
            if (i > current.Length - functionName.Length ||
                !string.Equals(current.Substring(i, functionName.Length), functionName, StringComparison.OrdinalIgnoreCase) ||
                (i > 0 && IsIdentifierChar(current[i - 1])) ||
                (i + functionName.Length < current.Length && IsIdentifierChar(current[i + functionName.Length])))
            {
                i++;
                continue;
            }

            var argsStart = i + functionName.Length;
            while (argsStart < current.Length && char.IsWhiteSpace(current[argsStart]))
                argsStart++;
            if (argsStart >= current.Length || current[argsStart] != '(')
            {
                i += functionName.Length;
                continue;
            }

            var argsEnd = FindMatchingParen(current, argsStart);
            if (argsEnd < 0)
                return current;

            var args = SplitTopLevelArguments(current[(argsStart + 1)..argsEnd]);
            if (args.Count != 1 ||
                !TryReadRegisteredExpressionOrdinalArgument(args[0].Trim(), out var ordinal))
            {
                i = argsEnd + 1;
                continue;
            }

            var replacement = $"Exp_{ordinal}()";
            current = current[..i] + replacement + current[(argsEnd + 1)..];
            i += replacement.Length;
        }

        return current;
    }

    private static string ApplyLegacyExpressionAliasReplacement(string expression, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var resourceReferenceMap = BuildAccessibleLegacyResourceReferenceMap(task, dataObjects);
        var aliasMap = BuildLegacyExpressionAliasMap(task, dataObjects);
        if (aliasMap.Count == 0 && resourceReferenceMap.Count == 0)
            return expression;

        var result = new StringBuilder(expression.Length);
        var quote = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (quote != '\0')
            {
                result.Append(ch);
                if (ch == quote && !(quote == '"' && i > 0 && expression[i - 1] == '\\'))
                    quote = '\0';
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                quote = ch;
                result.Append(ch);
                continue;
            }

            if (!char.IsLetter(ch))
            {
                result.Append(ch);
                continue;
            }

            var start = i;
            var end = i + 1;
            while (end < expression.Length && (char.IsLetterOrDigit(expression[end]) || expression[end] == '_'))
                end++;

            var token = expression[start..end];
            var prev = start > 0 ? expression[start - 1] : '\0';
            var next = end < expression.Length ? expression[end] : '\0';
            var nextNonSpace = end;
            while (nextNonSpace < expression.Length && char.IsWhiteSpace(expression[nextNonSpace]))
                nextNonSpace++;

            if (string.Equals(token, "u", StringComparison.Ordinal) &&
                nextNonSpace < expression.Length &&
                expression[nextNonSpace] == '.')
            {
                result.Append(token);
                i = end - 1;
                continue;
            }

            if ((start == 0 || (!char.IsLetterOrDigit(prev) && prev != '_' && prev != '.')) &&
                (end == expression.Length || (!char.IsLetterOrDigit(next) && next != '_')) &&
                (nextNonSpace >= expression.Length || expression[nextNonSpace] != '('))
            {
                if (resourceReferenceMap.TryGetValue(token, out var resourceReference) &&
                    !ShouldPreserveReservedAliasAsOperator(expression, start, end, token))
                {
                    result.Append(resourceReference);
                }
                else if (IsLegacyAliasReplacementReservedToken(token))
                {
                    result.Append(token);
                }
                else if (aliasMap.TryGetValue(token, out var invocation))
                {
                    result.Append(invocation);
                }
                else
                {
                    result.Append(token);
                }
            }
            else
            {
                result.Append(token);
            }

            i = end - 1;
        }

        return result.ToString();
    }

    private static bool ShouldPreserveReservedAliasAsOperator(string expression, int start, int end, string token)
    {
        if (!string.Equals(token, "AND", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(token, "OR", StringComparison.OrdinalIgnoreCase))
            return IsLegacyAliasReplacementReservedToken(token);

        return HasLikelyOperandBefore(expression, start) &&
               HasLikelyOperandAfter(expression, end);
    }

    private static bool HasLikelyOperandBefore(string expression, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var ch = expression[i];
            if (char.IsWhiteSpace(ch))
                continue;

            return char.IsLetterOrDigit(ch) ||
                   ch == '_' ||
                   ch == ')' ||
                   ch == ']' ||
                   ch == '\'' ||
                   ch == '"';
        }

        return false;
    }

    private static bool HasLikelyOperandAfter(string expression, int index)
    {
        for (var i = index; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (char.IsWhiteSpace(ch))
                continue;

            return char.IsLetterOrDigit(ch) ||
                   ch == '_' ||
                   ch == '(' ||
                   ch == '\'' ||
                   ch == '"';
        }

        return false;
    }

    private static bool IsLegacyAliasReplacementReservedToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return token.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("MOD", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("DIV", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("LIKE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("IN", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("TIME", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("LOG", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("DSOURCE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("VAR", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("MENU", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("FORM", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildLegacyExpressionAliasMap(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_legacyExpressionAliasMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var reserved = new HashSet<string>(GetAccessibleResourceKeys(task, dataObjects), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in task.ExpressionsSemantic.Entries)
        {
            var alias = ToLegacyExpressionAlias(entry.Ordinal);
            if (alias.Length < 2 ||
                reserved.Contains(alias) ||
                _topLevelAccessibleResourceKeys.Contains(alias))
                continue;

            result[alias] = $"Exp_{entry.Ordinal}()";
        }

        _legacyExpressionAliasMapCache[task.Ordinal] = result;
        return result;
    }

    private static Dictionary<string, string> BuildAccessibleLegacyResourceReferenceMap(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_accessibleLegacyResourceReferenceMapCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSelectKeys(task, "");
        AddResourceKeys(task, "", includeSlotAliases: true, slotAliasBase: 3);

        void AddResourceKeys(TaskSemantic owner, string prefix, bool includeSlotAliases, int slotAliasBase, int maxSlotAliasLength = int.MaxValue)
        {
            for (var i = 0; i < owner.ResourcesSemantic.Ordered.Count; i++)
            {
                var resource = owner.ResourcesSemantic.Ordered[i];
                var memberName = ResolveTaskResourceMemberName(owner, resource);
                if (prefix.StartsWith("Application.Instance.", StringComparison.Ordinal) &&
                    IsRuntimeCounterBinding(resource.Name ?? "", memberName))
                    continue;
                var reference = prefix + memberName;

                if (!string.IsNullOrWhiteSpace(resource.Name))
                    result.TryAdd(resource.Name, reference);

                if (includeSlotAliases)
                {
                    var slotAlias = ToLegacyExpressionAlias(i + slotAliasBase);
                    if (!string.IsNullOrWhiteSpace(slotAlias) &&
                        slotAlias.Length <= maxSlotAliasLength)
                        result.TryAdd(slotAlias, reference);
                }
            }

            foreach (var pair in owner.ResourcesSemantic.ByLegacyName)
                result.TryAdd(pair.Key, prefix + ResolveTaskResourceMemberName(owner, pair.Value));
        }

        void AddSelectKeys(TaskSemantic owner, string prefix)
        {
            foreach (var pair in BuildSelectNameToExpressionMap(owner, dataObjects))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                if (IsRuntimeCounterBinding(pair.Key, pair.Value))
                    continue;

                var reference = pair.Value.StartsWith("Application.Instance.", StringComparison.Ordinal)
                    ? pair.Value
                    : prefix + pair.Value;
                result.TryAdd(pair.Key, reference);
            }
        }

        if (_allTasks is not null)
        {
            var depth = 0;
            var parentOrdinal = task.ParentOrdinal;
            while (parentOrdinal.HasValue)
            {
                var parentTask = GetTaskByOrdinal(parentOrdinal, _allTasks);
                if (parentTask is null)
                    break;

                depth++;
                var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
                AddSelectKeys(parentTask, prefix);
                AddResourceKeys(parentTask, prefix, includeSlotAliases: true, slotAliasBase: 3);
                parentOrdinal = parentTask.ParentOrdinal;
            }

            var appTask = _applicationTask;
            if (appTask is not null && !ReferenceEquals(appTask, task))
            {
                AddSelectKeys(appTask, "Application.Instance.");
                AddResourceKeys(appTask, "Application.Instance.", includeSlotAliases: true, slotAliasBase: 1, maxSlotAliasLength: 1);
            }
        }

        _accessibleLegacyResourceReferenceMapCache[task.Ordinal] = result;
        return result;

    }

    private static IReadOnlyList<string> GetAccessibleResourceKeys(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (_accessibleResourceKeysCache.TryGetValue(task.Ordinal, out var cached))
            return cached;

        var result = new List<string>();
        AddAccessibleResourceKeys(task, dataObjects, result);
        _accessibleResourceKeysCache[task.Ordinal] = result;
        return result;
    }

    private static void AddAccessibleResourceKeys(TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects, List<string> result)
    {
        foreach (var key in task.ResourcesSemantic.ByName.Keys)
            result.Add(key);
        foreach (var key in task.ResourcesSemantic.ByLegacyName.Keys)
            result.Add(key);
        for (var i = 0; i < task.ResourcesSemantic.Ordered.Count; i++)
            result.Add(ToLegacyExpressionAlias(i + 3));
        foreach (var key in BuildSelectNameToExpressionMap(task, dataObjects).Keys)
            result.Add(key);

        if (_allTasks is null)
            return;

        var parentOrdinal = task.ParentOrdinal;
        while (parentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(parentOrdinal, _allTasks);
            if (parentTask is null)
                return;

            foreach (var key in parentTask.ResourcesSemantic.ByName.Keys)
                result.Add(key);
            foreach (var key in parentTask.ResourcesSemantic.ByLegacyName.Keys)
                result.Add(key);
            foreach (var key in BuildSelectNameToExpressionMap(parentTask, dataObjects).Keys)
                result.Add(key);
            for (var i = 0; i < parentTask.ResourcesSemantic.Ordered.Count; i++)
                result.Add(ToLegacyExpressionAlias(i + 3));

            parentOrdinal = parentTask.ParentOrdinal;
        }

        // Resource keys from other top-level programs are held in one shared
        // immutable index. BuildLegacyExpressionAliasMap checks that index
        // directly instead of copying every application's keys into every
        // nested task cache (quadratic on projects such as CGGeral).
    }

    private static string ToLegacyExpressionAlias(int ordinal)
    {
        if (ordinal <= 0)
            return "";

        var value = ordinal;
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return sb.ToString();
    }

    private static string RenderDotNetConstructorLikeExecuteCalls(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) || expr.IndexOf(".Execute(", StringComparison.Ordinal) < 0)
            return expr;

        var start = 0;
        while (start < expr.Length)
        {
            var match = Regex.Match(
                expr[start..],
                @"\b(?<name>(?:[A-Za-z_][A-Za-z0-9_]*\.)+[A-Z][A-Za-z0-9_]*)\s*\(",
                RegexOptions.CultureInvariant);
            if (!match.Success)
                break;

            var absStart = start + match.Index;
            if (absStart >= 4 &&
                string.Equals(expr.Substring(absStart - 4, 4), "new ", StringComparison.Ordinal))
            {
                start = absStart + match.Length;
                continue;
            }

            var openParen = absStart + match.Length - 1;
            var closeParen = FindMatchingParen(expr, openParen);
            if (closeParen < 0)
                break;

            var next = closeParen + 1;
            while (next < expr.Length && char.IsWhiteSpace(expr[next]))
                next++;

            var typeName = StripLeadingDotNetQualifier(match.Groups["name"].Value);
            if (next + ".Execute(".Length <= expr.Length &&
                string.Equals(expr.Substring(next, ".Execute(".Length), ".Execute(", StringComparison.Ordinal) &&
                LooksLikeClrConstructorName(typeName))
            {
                var originalNameLength = match.Groups["name"].Length;
                var replacement = "new " + typeName;
                expr = expr[..absStart] + replacement + expr[(absStart + originalNameLength)..];
                start = absStart + replacement.Length;
                continue;
            }

            start = closeParen + 1;
        }

        return expr;
    }

    private static string RenderWinHandleInteropCallsFromFunctionContract(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        string RenderExpectedIntPtrArgument(string argument)
        {
            var candidate = StripExpectedAttributeCastWrappers(argument.Trim(), "FIELD_NUMERIC");
            return TryEmitExpectedArgumentFromReliableEvidence(candidate, "System.IntPtr", task, out var rendered)
                ? rendered
                : argument.Trim();
        }

        string RewriteFromHandle(string functionName, List<string> args)
        {
            if (args.Count == 0)
                return $"{functionName}()";

            args[0] = RenderExpectedIntPtrArgument(args[0]);
            return $"{functionName}({string.Join(", ", args)})";
        }

        string RewriteFromHandleCalls(string source, string functionName)
        {
            var start = 0;
            while (start < source.Length)
            {
                var index = source.IndexOf(functionName, start, StringComparison.Ordinal);
                if (index < 0)
                    return source;

                var argsStart = index + functionName.Length;
                while (argsStart < source.Length && char.IsWhiteSpace(source[argsStart]))
                    argsStart++;
                if (argsStart >= source.Length || source[argsStart] != '(')
                {
                    start = index + functionName.Length;
                    continue;
                }

                var argsEnd = FindMatchingParen(source, argsStart);
                if (argsEnd < 0)
                    return source;

                var args = SplitTopLevelArguments(source[(argsStart + 1)..argsEnd]);
                var replacement = RewriteFromHandle(functionName, args);
                source = source[..index] + replacement + source[(argsEnd + 1)..];
                start = index + replacement.Length;
            }

            return source;
        }

        var normalized = RewriteFromHandleCalls(
            expression,
            "System.Windows.Forms.Form.FromHandle");

        normalized = RewriteFromHandleCalls(
            normalized,
            "Form.FromHandle");

        normalized = Regex.Replace(
            normalized,
            @"(?<prefix>GeneratedSnippets\.[A-Za-z0-9_\.]+\.func\s*\()(?<arg>u\.WinHWND\s*\([^()]*\))(?<suffix>\s*\))",
            m =>
            {
                var originalArg = m.Groups["arg"].Value.Trim();
                return $"{m.Groups["prefix"].Value}{RenderExpectedIntPtrArgument(originalArg)}{m.Groups["suffix"].Value}";
            },
            RegexOptions.CultureInvariant);

        return normalized;
    }

    private static string NormalizeTaskFunctionCallArguments(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = expression;

        foreach (var function in task.FunctionOverridesSemantic)
        {
            if (string.IsNullOrWhiteSpace(function.Name) ||
                string.IsNullOrWhiteSpace(function.MethodName) ||
                function.Parameters.Count == 0)
                continue;

            normalized = RewriteTaskFunctionCallArguments(
                normalized,
                function.MethodName,
                function.Parameters.Select(p => p.ParameterType).ToArray(),
                task);
        }

        foreach (var kv in ResolveAccessibleApplicationFunctionTargets(task))
        {
            var appTask = _applicationTask;
            var function = appTask?.FunctionOverridesSemantic.FirstOrDefault(fn => string.Equals(fn.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
            if (function is null || function.Parameters.Count == 0)
                continue;

            normalized = RewriteTaskFunctionCallArguments(
                normalized,
                kv.Value,
                function.Parameters.Select(p => p.ParameterType).ToArray(),
                task);
        }

        if (_allTasks is not null && task.ParentOrdinal.HasValue)
        {
            var depth = 1;
            var parentOrdinal = task.ParentOrdinal;
            while (parentOrdinal.HasValue)
            {
                var parentTask = GetTaskByOrdinal(parentOrdinal.Value, _allTasks);
                if (parentTask is null)
                    break;

                var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
                foreach (var function in parentTask.FunctionOverridesSemantic)
                {
                    if (string.IsNullOrWhiteSpace(function.Name) ||
                        string.IsNullOrWhiteSpace(function.MethodName) ||
                        !string.Equals(function.Definition.Scope, "S", StringComparison.OrdinalIgnoreCase) ||
                        function.Parameters.Count == 0)
                        continue;

                    normalized = RewriteTaskFunctionCallArguments(
                        normalized,
                        prefix + function.MethodName,
                        function.Parameters.Select(p => p.ParameterType).ToArray(),
                        task);
                }

                parentOrdinal = parentTask.ParentOrdinal;
                depth++;
            }
        }

        if (!string.Equals(expression, normalized, StringComparison.Ordinal))
            TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTaskFunctionCallArguments));

        return normalized;
    }

    private static string RewriteTaskFunctionCallArguments(
        string expression,
        string functionName,
        IReadOnlyList<string> parameterTypes,
        TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            string.IsNullOrWhiteSpace(functionName) ||
            parameterTypes.Count == 0)
            return expression;

        return RewriteFunctionCalls(expression, functionName, args =>
        {
            if (args.Count == 0)
                return null;

            for (var i = 0; i < args.Count && i < parameterTypes.Count; i++)
                args[i] = CoerceCallArgumentForParameter(args[i].Trim(), parameterTypes[i], task);

            return $"{functionName}({string.Join(", ", args)})";
        });
    }

    private static string RestoreApplicationQuoteDelimiterMisbindings(string expression, TaskSemantic task, string? expressionAttr, IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (!string.Equals(expressionAttr, "A", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(expression))
            return expression;

        var quoteMember = ResolveApplicationQuoteMember();
        if (string.IsNullOrWhiteSpace(quoteMember))
            return expression;

        expression = Regex.Replace(
            expression,
            @"(?<expr>(?:_parent\.)?[A-Za-z_][A-Za-z0-9_\.]*)\s*\+\s*(?<middle>u\.RTrim\(u\.(?:CastToText|Str)\([^\r\n]*?\)\))\s*\+\s*\k<expr>",
            m =>
            {
                var path = m.Groups["expr"].Value.Trim();
                if (!LooksLikeDateQuoteDelimiter(path, task))
                    return m.Value;
                return $"{quoteMember} + {m.Groups["middle"].Value} + {quoteMember}";
            });

        expression = Regex.Replace(
            expression,
            @"(?<expr>(?:_parent\.)?[A-Za-z_][A-Za-z0-9_\.]*)\s*\+\s*\k<expr>",
            m =>
            {
                var path = m.Groups["expr"].Value.Trim();
                if (!LooksLikeDateQuoteDelimiter(path, task))
                    return m.Value;
                return $"{quoteMember} + {quoteMember}";
            });

        return expression;
    }

    private static string? ResolveApplicationQuoteMember()
    {
        var appTask = _applicationTask;
        if (appTask is null)
            return null;

        var quoteResource = appTask.ResourcesSemantic.Ordered.FirstOrDefault(r =>
            string.Equals(r.Name, "v_aspa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ResolveTaskResourceMemberName(appTask, r), "v_aspa", StringComparison.OrdinalIgnoreCase));
        if (quoteResource is null)
            return null;

        return $"Application.Instance.{ResolveTaskResourceMemberName(appTask, quoteResource)}";
    }

    private static bool LooksLikeDateQuoteDelimiter(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsSimpleIdentifierPath(expression))
            return false;

        var info = ResolveTargetValueInfo(task, null, expression);
        return string.Equals(info.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(info.ModelAttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTimeFormattingExpression(
        string expr,
        TaskSemantic? task = null,
        IReadOnlyList<DataObjectDef>? dataObjects = null)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return expr;

        if (!TryParseFunctionCall(expr, out var functionName, out var args) ||
            !string.Equals(functionName, "u.TStr", StringComparison.Ordinal) ||
            args.Count != 2)
            return expr;

        var valueExpr = args[0].Trim();
        var pictureExpr = args[1].Trim();
        if (!IsTimePictureLiteral(pictureExpr))
            return expr;

        if (TryParseFunctionCall(valueExpr, out var innerFunctionName, out var innerArgs) &&
            (string.Equals(innerFunctionName, "u.ToTime", StringComparison.Ordinal) ||
             string.Equals(innerFunctionName, "UserMethods.ToTime", StringComparison.Ordinal)) &&
            innerArgs.Count == 1 &&
            IsTimeTypedExpression(innerArgs[0].Trim(), task, dataObjects))
        {
            TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTimeFormattingExpression));
            return $"u.TStr({innerArgs[0].Trim()}, {pictureExpr})";
        }

        if (IsTimeTypedExpression(valueExpr, task, dataObjects))
            return expr;

        TrackLegacyExpressionTreatment("NormalizeType", nameof(NormalizeTimeFormattingExpression));
        return $"u.TStr(u.ToTime({valueExpr}), {pictureExpr})";
    }

    private static bool IsTimePictureLiteral(string pictureExpr)
    {
        if (!TryGetWholeCSharpStringLiteral(pictureExpr, out var literal))
            return false;

        var picture = ExtractWholeCSharpStringLiteralContent(literal);
        if (string.IsNullOrWhiteSpace(picture))
            return false;

        return picture.IndexOf('H') >= 0 || picture.IndexOf('h') >= 0 ||
               picture.IndexOf('M') >= 0 || picture.IndexOf('m') >= 0 ||
               picture.IndexOf('S') >= 0 || picture.IndexOf('s') >= 0;
    }

    private static bool IsTimeTypedExpression(
        string valueExpr,
        TaskSemantic? task = null,
        IReadOnlyList<DataObjectDef>? dataObjects = null)
    {
        var trimmed = valueExpr.Trim();
        if (trimmed.StartsWith("u.ToTime(", StringComparison.Ordinal) ||
            trimmed.StartsWith("u.CastToTime(", StringComparison.Ordinal) ||
            trimmed.StartsWith("UserMethods.ToTime(", StringComparison.Ordinal) ||
            trimmed.StartsWith("XPARuntimeCore.Box.Time.", StringComparison.Ordinal) ||
            trimmed.StartsWith("Time.", StringComparison.Ordinal) ||
            string.Equals(trimmed, "XPARuntimeCore.Box.Time.Now", StringComparison.Ordinal) ||
            string.Equals(trimmed, "Time.Now", StringComparison.Ordinal) ||
            IsTopLevelCall(TryGetTopLevelFunctionName(trimmed), "u.Time") ||
            IsTopLevelCall(TryGetTopLevelFunctionName(trimmed), "Time") ||
            IsTopLevelCall(TryGetTopLevelFunctionName(trimmed), "u.AddTime"))
            return true;

        var arithmetic = TrySplitTopLevelSourceArithmeticExpression(StripRedundantOuterParentheses(trimmed));
        if (arithmetic.HasValue &&
            string.Equals(arithmetic.Value.Operator, "-", StringComparison.Ordinal) &&
            IsTimeTypedExpression(arithmetic.Value.Left, task, dataObjects) &&
            IsTimeTypedExpression(arithmetic.Value.Right, task, dataObjects))
            return true;

        if (task is not null &&
            TryResolveSimpleSourceReturnTypeFromResourcePath(task, trimmed, out var sourceReturnType) &&
            string.Equals(GetValueReturnType(NormalizeReturnTypeToken(sourceReturnType)), "Time", StringComparison.Ordinal))
            return true;

        if (task is not null && dataObjects is not null)
        {
            var resolvedSourceReturnType = ResolveSourceExpressionReturnType(trimmed, task, dataObjects, 0);
            if (string.Equals(GetValueReturnType(NormalizeReturnTypeToken(resolvedSourceReturnType)), "Time", StringComparison.Ordinal))
                return true;
        }

        if (task is not null &&
            TryResolveTaskResourceSourceReturnTypeFromTarget(task, trimmed, out var resourceReturnType) &&
            string.Equals(GetValueReturnType(NormalizeReturnTypeToken(resourceReturnType)), "Time", StringComparison.Ordinal))
            return true;

        if (task is null || !IsSimpleIdentifierPath(trimmed))
            return false;

        var valueInfo = ResolveTargetValueInfo(task, null, trimmed);
        var attrObj = ResolveEffectiveTargetAttrObj(
            NormalizeAttrObjKind(valueInfo.AttrObj),
            NormalizeAttrObjKind(valueInfo.ModelAttrObj));
        return string.Equals(GetValueReturnType(MapAttrObjToReturnType(attrObj)), "Time", StringComparison.Ordinal);
    }

    private static bool TryResolveTaskResourceSourceReturnTypeFromTarget(
        TaskSemantic task,
        string target,
        out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(target) || !IsSimpleIdentifierPath(target))
            return false;

        var resource = ResolveTaskResourceForAssignment(task, null, target);
        if (resource is null)
            return false;

        var ownerTask = ResolveTaskOwnerFromTargetPath(task, target) ??
                        (task.ResourcesSemantic.Ordered.Any(r => ReferenceEquals(r, resource))
                            ? task
                            : ResolveOwningTaskForResource(resource) ?? task);
        if (TryMapSourceResourceReturnType(resource, ownerTask, out returnType))
            return true;

        var attrObj = ResolveEffectiveTargetAttrObj(
            NormalizeAttrObjKind(resource.AttrObj),
            NormalizeAttrObjKind(resource.CellModelAttrObj));
        if (string.IsNullOrWhiteSpace(attrObj))
        {
            var resolvedType = ResolveTaskResourceColumnType(resource, _allFieldModels, ownerTask);
            attrObj = ResolveAttrObjForColumnType(resolvedType, _allFieldModels, ownerTask);
        }

        returnType = NormalizeReturnTypeToken(MapAttrObjToReturnType(attrObj));
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static string ExtractWholeCSharpStringLiteralContent(string literalCode)
    {
        if (string.IsNullOrWhiteSpace(literalCode))
            return "";

        if (literalCode.StartsWith("@\"", StringComparison.Ordinal) && literalCode.Length >= 3)
            return literalCode[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);

        if (literalCode.StartsWith("\"", StringComparison.Ordinal) && literalCode.EndsWith("\"", StringComparison.Ordinal) && literalCode.Length >= 2)
        {
            var inner = literalCode[1..^1];
            return inner
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return literalCode;
    }

    private static string RewriteDnSetExpressions(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) || expr.IndexOf("DNSet(", StringComparison.OrdinalIgnoreCase) < 0)
            return expr;

        var start = 0;
        while (true)
        {
            var match = Regex.Match(expr[start..], @"\bDNSet\s*\(", RegexOptions.IgnoreCase);
            if (!match.Success)
                return expr;

            var absStart = start + match.Index;
            var openParen = absStart + match.Length - 1;
            var closeParen = FindMatchingParen(expr, openParen);
            if (closeParen < 0)
                return expr;

            var inner = expr[(openParen + 1)..closeParen];
            var args = SplitTopLevelArgs(inner);
            if (args.Count == 2)
            {
                var replacement = $"{args[0].Trim()} = {args[1].Trim()}";
                expr = expr[..absStart] + replacement + expr[(closeParen + 1)..];
                start = absStart + replacement.Length;
                continue;
            }

            start = closeParen + 1;
        }
    }

    private static string RewriteDnCastExpressions(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) || expr.IndexOf("DNCast(", StringComparison.OrdinalIgnoreCase) < 0)
            return expr;

        var start = 0;
        while (true)
        {
            var match = Regex.Match(expr[start..], @"\bDNCast\s*\(", RegexOptions.IgnoreCase);
            if (!match.Success)
                return expr;

            var absStart = start + match.Index;
            var openParen = absStart + match.Length - 1;
            var closeParen = FindMatchingParen(expr, openParen);
            if (closeParen < 0)
                return expr;

            var inner = expr[(openParen + 1)..closeParen];
            var args = SplitTopLevelArgs(inner);
            if (args.Count == 2)
            {
                var replacement = TryRewriteDnCast(args[0].Trim(), args[1].Trim());
                if (!string.IsNullOrWhiteSpace(replacement))
                {
                    expr = expr[..absStart] + replacement + expr[(closeParen + 1)..];
                    start = absStart + replacement.Length;
                    continue;
                }
            }

            start = closeParen + 1;
        }
    }

    private static string RewriteDnRefExpressions(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr) || expr.IndexOf("DNRef(", StringComparison.OrdinalIgnoreCase) < 0)
            return expr;

        return RewriteFunctionCalls(expr, "DNRef", args =>
        {
            if (args.Count != 1)
                return null;
            return args[0].Trim();
        });
    }

    private static string? TryRewriteDnCast(string valueExpr, string typeExpr)
        => TryRewriteDnCastCentral(valueExpr, typeExpr);

}

