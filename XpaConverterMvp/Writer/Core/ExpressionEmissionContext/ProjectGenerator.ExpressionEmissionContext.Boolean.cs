namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string RewriteImplicitBooleanExpression(TaskSemantic task, string expression)
        => RewriteImplicitBooleanExpressionCentral(task, expression);

    private static string RewriteBooleanContextGetTextParamCalls(TaskSemantic task, string expression)
        => RewriteBooleanContextGetTextParamCallsCentral(task, expression);

    private static string RestoreTextGetterInsideNumericValueWrappers(string expression)
        => RestoreTextGetterInsideNumericValueWrappersCentral(expression);

    private static string RewriteBooleanOperand(TaskSemantic task, string expression)
        => RewriteBooleanOperandCentral(task, expression);

    private static bool TryRewriteBooleanTextComparison(
        TaskSemantic task,
        string left,
        string @operator,
        string right,
        out string rewritten)
        => TryRewriteBooleanTextComparisonCentral(task, left, @operator, right, out rewritten);

    private static string UnwrapBooleanTextComparisonRoot(string expression)
    {
        var root = StripExpectedAttributeCastWrappers(expression.Trim(), "FIELD_ALPHA");
        while (TryUnwrapCastToTextExpressionCentral(root, out var inner))
            root = inner.Trim();
        return root;
    }

    private static bool LooksLikeTextProducingBooleanExpression(TaskSemantic task, string expression)
        => LooksLikeTextProducingBooleanExpressionCentral(task, expression);

    private static bool IsTextLikeArithmeticOperand(TaskSemantic task, string expression)
        => IsTextLikeArithmeticOperandCentral(task, expression);

    private static bool IsTextLikeExpectation(ExpectedTypeContext actual)
        => IsTextLikeExpectationCentral(actual);

    private static bool IsTextLikeBooleanOperand(TaskSemantic task, string expression, ExpectedTypeContext actual)
        => IsTextLikeBooleanOperandCentral(task, expression, actual);

    private static bool IsBooleanLikeBooleanOperand(TaskSemantic task, string expression)
        => IsBooleanLikeBooleanOperandCentral(task, expression);

    private static bool IsWholeCSharpEmptyStringLiteral(string expression)
    {
        if (!TryGetWholeCSharpStringLiteral(expression.Trim(), out var literal))
            return false;

        return string.IsNullOrEmpty(ExtractWholeCSharpStringLiteralContent(literal));
    }

    private static string NormalizeStatementBooleanConditionSyntax(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        if (_statementBooleanConditionSyntaxCache.TryGetValue(expression, out var cached))
            return cached;

        var normalized = FormatBooleanConditionSyntax(expression);
        _statementBooleanConditionSyntaxCache[expression] = normalized;
        return normalized;
    }

    private static string NormalizeSourceBooleanCode(string expression, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = NormalizeChainedBooleanComparisonsCentral(task, expression);
        return NormalizeStatementBooleanConditionSyntax(RewriteBooleanOperand(task, normalized));
    }

    private static string NormalizeChainedBooleanComparisonsCentral(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = StripRedundantOuterParentheses(expression.Trim());
        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            if (IsTopLevelCall(functionName, "u.Not") && args.Count == 1)
                return $"u.Not({NormalizeChainedBooleanComparisonsCentral(task, args[0])})";
            if (IsTopLevelCall(functionName, "u.If") && args.Count == 3)
            {
                return $"u.If({NormalizeChainedBooleanComparisonsCentral(task, args[0])}, {args[1].Trim()}, {args[2].Trim()})";
            }
        }

        var booleanBinary = SplitTopLevelBooleanBinaryExpression(trimmed);
        if (booleanBinary is not null)
        {
            var booleanLeft = NormalizeChainedBooleanComparisonsCentral(task, booleanBinary.Value.Left);
            var booleanRight = NormalizeChainedBooleanComparisonsCentral(task, booleanBinary.Value.Right);
            return $"{booleanLeft} {booleanBinary.Value.Operator} {booleanRight}";
        }

        var comparison = SplitTopLevelComparisonExpression(trimmed);
        if (comparison is null)
            return expression.Trim();

        var leftExpression = comparison.Value.Left.Trim();
        var rightExpression = comparison.Value.Right.Trim();
        var leftIsComparison = SplitTopLevelComparisonExpression(leftExpression) is not null;
        var rightIsComparison = SplitTopLevelComparisonExpression(rightExpression) is not null;
        if (!leftIsComparison && !rightIsComparison)
            return expression.Trim();

        var booleanExpected = ExpectedTypeForReturnType("Bool") with { IsBooleanCondition = false };
        var left = leftIsComparison
            ? NormalizeChainedBooleanComparisonsCentral(task, leftExpression)
            : EmitExpressionForExpectedType(leftExpression, task, booleanExpected);
        var right = rightIsComparison
            ? NormalizeChainedBooleanComparisonsCentral(task, rightExpression)
            : EmitExpressionForExpectedType(rightExpression, task, booleanExpected);
        return $"(({left}) {comparison.Value.Operator} ({right}))";
    }

    private static string FormatBooleanConditionSyntax(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = RemoveUnmatchedTopLevelClosingParenthesesBeforeBooleanOperators(expression.Trim());
        trimmed = BalanceMalformedParenthesisEnvelope(trimmed);
        trimmed = StripRedundantOuterParentheses(trimmed);
        var topLevelBoolean =
            SplitTopLevelBooleanBinaryExpression(trimmed) ??
            SplitMalformedTopLevelBooleanBinaryExpression(trimmed);
        if (topLevelBoolean is null)
            return trimmed;

        var left = FormatBooleanConditionOperand(topLevelBoolean.Value.Left, topLevelBoolean.Value.Operator);
        var right = FormatBooleanConditionOperand(topLevelBoolean.Value.Right, topLevelBoolean.Value.Operator);
        return $"{left} {topLevelBoolean.Value.Operator} {right}";
    }

    private static string FormatBooleanConditionOperand(string operand, string parentOperator)
    {
        var formatted = FormatBooleanConditionSyntax(operand);
        return NeedsBooleanConditionOperandParentheses(formatted, parentOperator)
            ? $"({formatted})"
            : formatted;
    }

    private static bool NeedsBooleanConditionOperandParentheses(string operand, string parentOperator)
    {
        if (string.IsNullOrWhiteSpace(operand))
            return false;

        var stripped = operand.Trim();
        var childBoolean =
            SplitTopLevelBooleanBinaryExpression(stripped) ??
            SplitMalformedTopLevelBooleanBinaryExpression(stripped);

        if (string.Equals(parentOperator, "&&", StringComparison.Ordinal) &&
            childBoolean is not null &&
            string.Equals(childBoolean.Value.Operator, "||", StringComparison.Ordinal))
            return true;

        return stripped.Contains('?', StringComparison.Ordinal) &&
               HasTopLevelConditionalOperator(stripped);
    }

    private static bool HasTopLevelConditionalOperator(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return false;

                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && ch == '?' &&
                (i == 0 || expression[i - 1] != '?') &&
                (i + 1 >= expression.Length || expression[i + 1] != '?'))
                return true;
        }

        return false;
    }

    private static string RemoveUnmatchedTopLevelClosingParenthesesBeforeBooleanOperators(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) ||
            (expression.IndexOf("&&", StringComparison.Ordinal) < 0 &&
             expression.IndexOf("||", StringComparison.Ordinal) < 0))
            return expression;

        var sb = new System.Text.StringBuilder(expression.Length);
        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                {
                    sb.Append(expression[i]);
                    continue;
                }

                sb.Append(expression, i, quoteEnd - i + 1);
                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                sb.Append(ch);
                continue;
            }

            if (ch == ')')
            {
                if (depth > 0)
                {
                    depth--;
                    sb.Append(ch);
                    continue;
                }

                if (NextNonWhitespaceAfterClosingsIsBooleanOperator(expression, i + 1))
                    continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool NextNonWhitespaceAfterClosingsIsBooleanOperator(string expression, int index)
    {
        for (var i = index; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (char.IsWhiteSpace(ch) || ch == ')')
                continue;

            return i + 1 < expression.Length &&
                   ((expression[i] == '&' && expression[i + 1] == '&') ||
                    (expression[i] == '|' && expression[i + 1] == '|'));
        }

        return false;
    }

    private static string BalanceMalformedParenthesisEnvelope(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = expression.Trim();

        var inString = false;
        var verbatim = false;
        var balance = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (inString)
            {
                if (verbatim)
                {
                    if (ch == '"' && i + 1 < trimmed.Length && trimmed[i + 1] == '"')
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

            if (ch == '@' && i + 1 < trimmed.Length && trimmed[i + 1] == '"')
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
            else if (ch == ')')
                balance--;
        }

        if (balance == -1)
            return "(" + trimmed;

        if (balance == 1)
            return trimmed + ")";

        return trimmed;
    }

    private static (string Left, string Operator, string Right)? SplitMalformedTopLevelBooleanBinaryExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        for (var i = 0; i < expression.Length - 1; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return null;

                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (i + 1 < expression.Length)
            {
                var op = expression.Substring(i, 2);
                if ((op == "&&" || op == "||") && depth <= 0)
                {
                    var left = expression.Substring(0, i).Trim();
                    var right = expression.Substring(i + 2).Trim();
                    if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                        return (left, op, right);
                }
            }
        }

        return null;
    }

    private static (string Left, string Operator, string Right)? SplitTopLevelBooleanBinaryExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        for (var i = 0; i < expression.Length - 1; i++)
        {
            if (IsQuotedSegmentStart(expression, i))
            {
                if (!TryReadQuotedSegmentEnd(expression, i, out var quoteEnd))
                    return null;

                i = quoteEnd;
                continue;
            }

            var ch = expression[i];
            if (ch == '(')
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && i + 1 < expression.Length)
            {
                var op = expression.Substring(i, 2);
                if (op == "&&" || op == "||")
                {
                    var left = expression.Substring(0, i).Trim();
                    var right = expression.Substring(i + 2).Trim();
                    if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                        return (left, op, right);
                }
            }
        }

        return null;
    }

    private static (string Left, string Right)? SplitTopLevelAssignmentExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < expression.Length; i++)
        {
            var ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    if (quote == '"' && i > 0 && expression[i - 1] == '\\')
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
            {
                depth++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                continue;
            }

            if (depth != 0 || ch != '=')
                continue;

            if ((i > 0 && expression[i - 1] is '=' or '!' or '<' or '>') ||
                (i + 1 < expression.Length && expression[i + 1] == '='))
                continue;

            var left = expression[..i].Trim();
            var right = expression[(i + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                return (left, right);
        }

        return null;
    }

    private static IReadOnlyList<string> GetTopLevelCallArgs(string expression)
    {
        return TryParseFunctionCall(expression, out _, out var args)
            ? args
            : Array.Empty<string>();
    }

    private static string BuildBooleanTruthinessExpressionCentral(TaskSemantic task, string expression, string scalarType)
    {
        var trimmed = expression.Trim();
        return scalarType switch
        {
            "Text" => $"{CoerceScalarCallArgumentWithTypeEngineCentral(trimmed, "FIELD_ALPHA")} != \"\"",
            "Number" => $"{CoerceScalarCallArgumentWithTypeEngineCentral(trimmed, "FIELD_NUMERIC")} != 0",
            "Date" => $"!u.IsNull({CoerceScalarCallArgumentWithTypeEngineCentral(trimmed, "FIELD_DATE")})",
            "Time" => $"{CoerceScalarCallArgumentWithTypeEngineCentral(trimmed, "FIELD_TIME")} != 0",
            "byte[]" => $"u.Not(u.IsNull({trimmed}))",
            _ => trimmed
        };
    }

    private static string RewriteImplicitBooleanExpressionCentral(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = CanonicalizeStructuredConditionForComparison(expression.Trim());
        if (TrySplitLeadingOpaqueInlineComment(trimmed, out var leadingComment, out var uncommented))
            return $"{leadingComment} {RewriteImplicitBooleanExpressionCentral(task, uncommented)}";
        if (HasOpaqueInlineComment(trimmed))
            return trimmed;

        if (IsNumericLiteralExpressionCentral(trimmed))
            return $"{trimmed} != 0";

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.CastToText") &&
            TryUnwrapCastToTextExpressionCentral(trimmed, out var castTextInner) &&
            IsBooleanLikeBooleanOperandCentral(task, castTextInner))
            return RewriteImplicitBooleanExpressionCentral(task, castTextInner);

        if (IsTopLevelCall(topLevelCall, "u.Trim") ||
            IsTopLevelCall(topLevelCall, "u.RTrim") ||
            IsTopLevelCall(topLevelCall, "u.LTrim") ||
            IsTopLevelCall(topLevelCall, "u.Upper") ||
            IsTopLevelCall(topLevelCall, "u.Lower") ||
            IsTopLevelCall(topLevelCall, "u.RepStr") ||
            IsTopLevelCall(topLevelCall, "u.CastToText"))
            return $"{trimmed} != \"\"";

        if (IsTopLevelCall(topLevelCall, "u.GetTextParam"))
            return $"u.GetBoolParam({string.Join(", ", GetTopLevelCallArgs(trimmed))})";

        if (IsTopLevelCall(topLevelCall, "u.TaskInstance"))
            return $"{trimmed} != 0";

        if (IsObjectProducingExpression(topLevelCall))
            return EmitExpressionForAttrObj(trimmed, task, "FIELD_LOGICAL");

        var inferred = ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
        var inferredReturnType = GetValueReturnType(inferred.ReturnType);
        var inferredAttrObj = inferred.AttrObj;
        if (!string.IsNullOrWhiteSpace(inferredReturnType) || !string.IsNullOrWhiteSpace(inferredAttrObj))
        {
            var scalarType = !string.IsNullOrWhiteSpace(inferredReturnType)
                ? inferredReturnType
                : MapAttrObjToReturnType(inferredAttrObj);

            switch (scalarType)
            {
                case "Bool":
                    return trimmed;
                case "Text":
                    return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Text");
                case "Number":
                    return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Number");
                case "Date":
                    return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Date");
                case "Time":
                    return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Time");
                case "byte[]":
                    return BuildBooleanTruthinessExpressionCentral(task, trimmed, "byte[]");
            }
        }

        if (LooksLikeTextProducingBooleanExpression(task, trimmed))
            return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Text");

        if (!IsSimpleIdentifierPath(trimmed))
            return expression;

        var targetInfo = ResolveTargetValueInfo(task, null, trimmed);
        if (targetInfo.IsBlob && !targetInfo.IsArray)
            return $"u.Not(u.IsNull({trimmed}))";

        if (string.Equals(targetInfo.AttrObj, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Text");
        if (string.Equals(targetInfo.AttrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Number");
        if (string.Equals(targetInfo.AttrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase))
            return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Date");
        if (string.Equals(targetInfo.AttrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase))
            return BuildBooleanTruthinessExpressionCentral(task, trimmed, "Time");

        return expression;
    }

    private static bool LooksLikeTextProducingBooleanExpressionCentral(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        if (IsWholeStringLiteralExpression(trimmed))
            return true;

        var topLevelCall = TryGetTopLevelFunctionName(trimmed);
        if (IsTopLevelCall(topLevelCall, "u.Trim") ||
            IsTopLevelCall(topLevelCall, "u.RTrim") ||
            IsTopLevelCall(topLevelCall, "u.LTrim") ||
            IsTopLevelCall(topLevelCall, "u.Upper") ||
            IsTopLevelCall(topLevelCall, "u.Lower") ||
            IsTopLevelCall(topLevelCall, "u.RepStr") ||
            IsTopLevelCall(topLevelCall, "u.CastToText"))
            return true;

        var arithmetic = SplitTopLevelArithmeticExpression(trimmed);
        if (arithmetic is null || !string.Equals(arithmetic.Value.Operator, "+", StringComparison.Ordinal))
            return false;

        return IsTextLikeArithmeticOperandCentral(task, arithmetic.Value.Left) ||
               IsTextLikeArithmeticOperandCentral(task, arithmetic.Value.Right);
    }

    private static bool IsTextLikeArithmeticOperandCentral(TaskSemantic task, string expression)
    {
        var trimmed = expression.Trim();
        if (IsWholeStringLiteralExpression(trimmed))
            return true;

        var inferred = ResolveExpectedTypeFromExpressionEvidence(task, trimmed);
        return IsTextLikeExpectationCentral(inferred);
    }

    private static bool IsTextLikeExpectationCentral(ExpectedTypeContext actual)
    {
        var attr = NormalizeAttrObjKind(actual.AttrObj);
        if (string.Equals(attr, "FIELD_ALPHA", StringComparison.OrdinalIgnoreCase))
            return true;

        var returnType = GetValueReturnType(actual.ReturnType);
        return string.Equals(returnType, "Text", StringComparison.Ordinal);
    }

    private static bool IsTextLikeBooleanOperandCentral(TaskSemantic task, string expression, ExpectedTypeContext actual)
    {
        if (!IsTextLikeExpectationCentral(actual))
            return false;

        var stripped = StripExpectedAttributeCastWrappers(expression.Trim(), "FIELD_ALPHA");
        if (string.Equals(stripped, expression.Trim(), StringComparison.Ordinal))
            return true;

        if (IsBooleanLikeBooleanOperand(task, stripped))
            return false;

        var strippedActual = ResolveExpectedTypeFromExpressionEvidence(task, stripped);
        return IsTextLikeExpectationCentral(strippedActual);
    }

    private static bool IsBooleanLikeBooleanOperandCentral(TaskSemantic task, string expression)
    {
        var stripped = StripExpectedAttributeCastWrappers(expression.Trim(), "FIELD_ALPHA");
        if (SplitTopLevelBooleanBinaryExpression(stripped) is not null ||
            SplitTopLevelComparisonExpression(stripped) is not null)
            return true;

        if (TryParseFunctionCall(stripped, out var functionName, out var args))
        {
            if (IsTopLevelCall(functionName, "u.Not") && args.Count == 1)
                return true;
            if (IsTopLevelCall(functionName, "u.GetBoolParam"))
                return true;

            if (IsObjectProducingExpression(functionName))
            {
                var inferred = ResolveExpectedTypeFromExpressionEvidence(task, stripped);
                return string.Equals(NormalizeAttrObjKind(inferred.AttrObj), "FIELD_LOGICAL", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(GetValueReturnType(inferred.ReturnType), "Bool", StringComparison.Ordinal);
            }
        }

        return bool.TryParse(stripped, out _);
    }

    private static bool TryRewriteDeclaredScalarEmptyStringComparisonCentral(
        TaskSemantic task,
        string left,
        string @operator,
        string right,
        out string rewritten)
    {
        rewritten = "";
        if (!string.Equals(@operator, "==", StringComparison.Ordinal) &&
            !string.Equals(@operator, "!=", StringComparison.Ordinal))
            return false;

        var emptyOnRight = IsWholeCSharpEmptyStringLiteral(right);
        var emptyOnLeft = IsWholeCSharpEmptyStringLiteral(left);
        if (!emptyOnRight && !emptyOnLeft)
            return false;

        var scalarSide = emptyOnRight ? left.Trim() : right.Trim();
        var emptySide = emptyOnRight ? right.Trim() : left.Trim();
        if (!(IsSimpleIdentifier(scalarSide) || IsSimpleIdentifierPath(scalarSide)))
            return false;

        var targetInfo = ResolveTargetValueInfo(task, null, scalarSide);
        var attrObj = NormalizeAttrObjKind(targetInfo.AttrObj);
        var isDeclaredNonTextScalar =
            string.Equals(attrObj, "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_DATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attrObj, "FIELD_TIME", StringComparison.OrdinalIgnoreCase);
        if (!isDeclaredNonTextScalar)
            return false;

        var normalizedScalarText = EmitExpressionForExpectedType(scalarSide, task, ExpectedTypeForReturnType("Text"));
        rewritten = emptyOnRight
            ? $"({normalizedScalarText} {@operator} {emptySide})"
            : $"({emptySide} {@operator} {normalizedScalarText})";
        return true;
    }

    private static bool TryRewriteNumericTypeComparisonCentral(
        TaskSemantic task,
        string left,
        string @operator,
        string right,
        out string rewritten)
    {
        rewritten = "";
        if (!(string.Equals(@operator, "==", StringComparison.Ordinal) ||
              string.Equals(@operator, "!=", StringComparison.Ordinal) ||
              string.Equals(@operator, ">", StringComparison.Ordinal) ||
              string.Equals(@operator, ">=", StringComparison.Ordinal) ||
              string.Equals(@operator, "<", StringComparison.Ordinal) ||
              string.Equals(@operator, "<=", StringComparison.Ordinal)))
            return false;

        static bool TryGetTypeOperand(string expression, out string typeRef)
        {
            typeRef = "";
            var candidate = expression.Trim();
            if (!candidate.StartsWith("typeof(", StringComparison.Ordinal) ||
                !candidate.EndsWith(")", StringComparison.Ordinal))
                return false;

            typeRef = candidate;
            return true;
        }

        var leftTrimmed = left.Trim();
        var rightTrimmed = right.Trim();
        var typeOnLeft = TryGetTypeOperand(leftTrimmed, out var leftTypeRef);
        var typeOnRight = TryGetTypeOperand(rightTrimmed, out var rightTypeRef);
        if (typeOnLeft == typeOnRight)
            return false;

        var scalarSide = typeOnLeft ? rightTrimmed : leftTrimmed;
        var typeRef = typeOnLeft ? leftTypeRef : rightTypeRef;
        if (!(IsSimpleIdentifier(scalarSide) || IsSimpleIdentifierPath(scalarSide)))
            return false;

        var targetInfo = ResolveTargetValueInfo(task, null, scalarSide);
        if (!targetInfo.IsNumeric &&
            !string.Equals(NormalizeAttrObjKind(targetInfo.AttrObj), "FIELD_NUMERIC", StringComparison.OrdinalIgnoreCase))
            return false;

        var foundOrdinal = false;
        var ordinal = 0;
        foreach (var pair in _dataSourceTypeByObjectOrdinal)
        {
            if (!string.Equals(pair.Value, typeRef, StringComparison.Ordinal))
                continue;

            ordinal = pair.Key;
            foundOrdinal = true;
            break;
        }

        if (!foundOrdinal)
            return false;

        rewritten = typeOnLeft
            ? $"({ordinal} {@operator} {scalarSide})"
            : $"({scalarSide} {@operator} {ordinal})";
        return true;
    }

    private static string RewriteBooleanOperandCentral(TaskSemantic task, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var trimmed = StripRedundantOuterParentheses(CanonicalizeStructuredConditionForComparison(expression.Trim()));
        while (TryUnwrapCastToTextExpressionCentral(trimmed, out var castTextInner))
            trimmed = StripRedundantOuterParentheses(CanonicalizeStructuredConditionForComparison(castTextInner));
        if (IsNullCallExpression(trimmed))
            return "u.CastToBool(u.Null())";
        if (TrySplitLeadingOpaqueInlineComment(trimmed, out var leadingComment, out var uncommented))
            return $"{leadingComment} {RewriteBooleanOperandCentral(task, uncommented)}";
        if (HasOpaqueInlineComment(trimmed))
            return trimmed;

        if (TryParseFunctionCall(trimmed, out var functionName, out var args))
        {
            if (IsTopLevelCall(functionName, "u.CastToText") && args.Count == 1)
                return RewriteBooleanOperandCentral(task, args[0]);
            if (IsTopLevelCall(functionName, "u.GetTextParam"))
                return $"u.GetBoolParam({string.Join(", ", args)})";
            if (IsObjectProducingExpression(functionName))
                return EmitExpressionForAttrObj(trimmed, task, "FIELD_LOGICAL");
            if (IsTopLevelCall(functionName, "u.Not") && args.Count == 1)
                return $"u.Not({RewriteBooleanOperandCentral(task, args[0])})";
            if (IsTopLevelCall(functionName, "u.CndRange") && args.Count >= 2)
                return RewriteBooleanOperandCentral(task, args[0]);
            if (IsTopLevelCall(functionName, "u.Case") && args.Count >= 3)
                return RewriteBooleanCaseOperandCentral(task, args);
            if (IsTopLevelCall(functionName, "u.If") && args.Count == 3)
            {
                var condition = FormatBooleanConditionSyntax(RewriteBooleanOperandCentral(task, args[0]));
                var whenTrue = RewriteBooleanOperandCentral(task, args[1]);
                var whenFalse = RewriteBooleanOperandCentral(task, args[2]);
                return $"u.If({condition}, {whenTrue}, {whenFalse})";
            }
        }

        var split = SplitTopLevelBooleanBinaryExpression(trimmed);
        if (split is not null)
        {
            var left = RewriteBooleanBinaryOperandCentral(task, split.Value.Left);
            var right = RewriteBooleanBinaryOperandCentral(task, split.Value.Right);
            return FormatBooleanConditionSyntax($"{left} {split.Value.Operator} {right}");
        }

        var assignment = SplitTopLevelAssignmentExpression(trimmed);
        if (assignment is not null)
        {
            var left = assignment.Value.Left.Trim();
            var right = assignment.Value.Right.Trim();
            var comparisonExpected = InferExpectedTypeFromComparisonOperands(task, left, right);
            if (comparisonExpected.HasExpectation)
            {
                left = EmitExpressionForExpectedType(left, task, comparisonExpected with { IsBooleanCondition = false });
                right = EmitExpressionForExpectedType(right, task, comparisonExpected with { IsBooleanCondition = false });
                left = NormalizeComparisonOperandForExpected(left, comparisonExpected, task);
                right = NormalizeComparisonOperandForExpected(right, comparisonExpected, task);
            }

            return $"({left} == {right})";
        }

        var comparison = SplitTopLevelComparisonExpression(trimmed);
        if (comparison is not null)
        {
            var left = comparison.Value.Left.Trim();
            var right = comparison.Value.Right.Trim();
            var nestedLeftComparison = SplitTopLevelComparisonExpression(left) is not null;
            var nestedRightComparison = SplitTopLevelComparisonExpression(right) is not null;
            if (nestedLeftComparison || nestedRightComparison)
            {
                var booleanExpected = ExpectedTypeForReturnType("Bool") with { IsBooleanCondition = false };
                left = nestedLeftComparison
                    ? RewriteBooleanOperandCentral(task, left)
                    : EmitExpressionForExpectedType(left, task, booleanExpected);
                right = nestedRightComparison
                    ? RewriteBooleanOperandCentral(task, right)
                    : EmitExpressionForExpectedType(right, task, booleanExpected);
                left = NormalizeComparisonOperandForExpected(left, booleanExpected, task);
                right = NormalizeComparisonOperandForExpected(right, booleanExpected, task);
                return $"(({left}) {comparison.Value.Operator} ({right}))";
            }
            if (TryRewriteNumericTypeComparisonCentral(task, left, comparison.Value.Operator, right, out var rewrittenNumericTypeComparison))
                return rewrittenNumericTypeComparison;
            if (TryRewriteDeclaredScalarEmptyStringComparisonCentral(task, left, comparison.Value.Operator, right, out var rewrittenDeclaredScalarEmptyStringComparison))
                return rewrittenDeclaredScalarEmptyStringComparison;
            if (TryRewriteBooleanTextComparison(task, left, comparison.Value.Operator, right, out var rewrittenBooleanTextComparison))
                return rewrittenBooleanTextComparison;
            if ((IsWholeCSharpEmptyStringLiteral(left) || IsWholeCSharpEmptyStringLiteral(right)) &&
                (IsSimpleIdentifier(left) || IsSimpleIdentifierPath(left) || IsSimpleIdentifier(right) || IsSimpleIdentifierPath(right)))
            {
                var scalarSide = IsWholeCSharpEmptyStringLiteral(left) ? right : left;
                var targetInfo = ResolveTargetValueInfo(task, null, scalarSide.Trim());
                ConversionTelemetry.LogDuration(
                    "EXPRWARN",
                    ResolveTaskClassName(task, _allTasks ?? System.Array.Empty<TaskSemantic>()),
                    System.TimeSpan.Zero,
                    $"kind=\"empty-string-comparison-survived\" left={QuoteTelemetry(left)} op={QuoteTelemetry(comparison.Value.Operator)} right={QuoteTelemetry(right)} attr={QuoteTelemetry(targetInfo.AttrObj)} isNumeric={targetInfo.IsNumeric}");
            }
            var comparisonExpected = InferExpectedTypeFromComparisonOperands(task, left, right);
            if (comparisonExpected.HasExpectation)
            {
                left = EmitExpressionForExpectedType(left, task, comparisonExpected with { IsBooleanCondition = false });
                right = EmitExpressionForExpectedType(right, task, comparisonExpected with { IsBooleanCondition = false });
                left = NormalizeComparisonOperandForExpected(left, comparisonExpected, task);
                right = NormalizeComparisonOperandForExpected(right, comparisonExpected, task);
            }

            if (comparison.Value.Operator is "<" or ">" or "<=" or ">=" &&
                comparisonExpected.HasExpectation &&
                IsTextLikeExpectedType(comparisonExpected))
            {
                return $"(string.Compare(u.CastToText({left}).ToString(), u.CastToText({right}).ToString(), System.StringComparison.Ordinal) {comparison.Value.Operator} 0)";
            }

            return $"({left} {comparison.Value.Operator} {right})";
        }

        return string.Equals(trimmed, expression.Trim(), StringComparison.Ordinal)
            ? RewriteImplicitBooleanExpressionCentral(task, trimmed)
            : $"({RewriteImplicitBooleanExpressionCentral(task, trimmed)})";
    }

    private static string RewriteBooleanCaseOperandCentral(TaskSemantic task, IReadOnlyList<string> args)
    {
        var rewritten = new List<string>(args.Count) { args[0].Trim() };
        var index = 1;
        while (index + 1 < args.Count)
        {
            rewritten.Add(args[index].Trim());
            rewritten.Add(RewriteBooleanOperandCentral(task, args[index + 1]));
            index += 2;
        }

        if (index < args.Count)
            rewritten.Add(RewriteBooleanOperandCentral(task, args[index]));

        return $"u.Case({string.Join(", ", rewritten)})";
    }

    private static string RewriteBooleanBinaryOperandCentral(TaskSemantic task, string expression)
    {
        var rewritten = RewriteBooleanOperandCentral(task, expression);
        var trimmed = StripRedundantOuterParentheses(rewritten.Trim());
        if (SplitTopLevelBooleanBinaryExpression(trimmed) is not null ||
            SplitTopLevelComparisonExpression(trimmed) is not null ||
            IsBooleanLikeBooleanOperand(task, trimmed))
            return rewritten;

        return RewriteImplicitBooleanExpressionCentral(task, trimmed);
    }
}
