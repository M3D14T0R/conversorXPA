using System;
using System.Collections.Generic;
using System.Globalization;

namespace XpaConverterMvp.TypeSystem;

internal readonly record struct XpaTypedExpressionContext(
    Func<string, XpaTypedExpression?> ResolveSymbol,
    Func<string, IReadOnlyList<XpaTypedExpression>, XpaTypedExpression?> ResolveFunction,
    Func<string, string, XpaTypedExpression?> ResolveLiteralSuffix,
    XpaExpressionDestination Destination);

/// <summary>
/// Parser e emissor tipado de expressões XPA. A árvore determina o tipo de
/// cada nó antes da emissão; nenhuma etapa posterior corrige strings C#.
/// </summary>
internal static class XpaTypedExpressionEmitter
{
    internal static bool TryEmit(
        string source,
        XpaTypedExpressionContext context,
        out XpaTypedExpression emitted)
    {
        emitted = default;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        try
        {
            var parser = new Parser(source, context);
            var result = parser.Parse();
            if (!parser.AtEnd)
                return false;

            if (context.Destination.Type != XpaType.Unknown)
            {
                if (!XpaExpressionTypeMap.TryApply(result, context.Destination, out result))
                    return false;
            }

            emitted = result;
            return true;
        }
        catch (ExpressionParseException)
        {
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private readonly XpaTypedExpressionContext _context;
        private Token _current;

        internal Parser(string source, XpaTypedExpressionContext context)
        {
            _lexer = new Lexer(source);
            _context = context;
            _current = _lexer.Next();
        }

        internal bool AtEnd => _current.Kind == TokenKind.End;

        internal XpaTypedExpression Parse()
        {
            var result = ParseExpression(0);
            if (_current.Kind != TokenKind.End)
                throw new ExpressionParseException();
            return result;
        }

        private XpaTypedExpression ParseExpression(int minimumPrecedence)
        {
            var left = ParsePrefix();
            while (true)
            {
                var concatenatedOperator = TryGetConcatenatedBinaryOperator(
                    out var op,
                    out var precedence,
                    out var rightOperand);
                if (!concatenatedOperator &&
                    !TryGetBinaryOperator(out op, out precedence))
                    break;
                if (precedence < minimumPrecedence)
                    break;

                if (concatenatedOperator)
                {
                    // The lexer has already consumed the complete exported token
                    // (for example ANDBR). Keep its typed suffix as the current
                    // operand instead of advancing to the following token.
                    _current = rightOperand;
                }
                else
                {
                    Advance();
                    if (op == "NOT LIKE")
                        Advance();
                }

                var right = ParseExpression(precedence + 1);
                left = EmitBinary(op, left, right);
            }
            return left;
        }

        private bool TryGetConcatenatedBinaryOperator(
            out string op,
            out int precedence,
            out Token rightOperand)
        {
            op = string.Empty;
            precedence = -1;
            rightOperand = default;
            if (_current.Kind != TokenKind.Identifier)
                return false;

            foreach (var candidate in ConcatenatedBinaryOperators)
            {
                if (_current.Text.Length <= candidate.Word.Length ||
                    !_current.Text.StartsWith(candidate.Word, StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = _current.Text[candidate.Word.Length..];
                var fullSymbol = _context.ResolveSymbol(_current.Text);
                var suffixSymbol = _context.ResolveSymbol(suffix);
                var suffixStartsFunctionCall = _lexer.Peek().Kind == TokenKind.OpenParenthesis;
                if (!IsUnresolvedSymbol(fullSymbol, _current.Text) ||
                    (IsUnresolvedSymbol(suffixSymbol, suffix) && !suffixStartsFunctionCall))
                    continue;

                op = candidate.Operator;
                precedence = candidate.Precedence;
                rightOperand = new Token(TokenKind.Identifier, suffix);
                return true;
            }

            return false;
        }

        private static bool IsUnresolvedSymbol(
            XpaTypedExpression? symbol,
            string sourceName)
            => !symbol.HasValue ||
               (symbol.Value.Type == XpaType.Object &&
                string.Equals(symbol.Value.Code, sourceName, StringComparison.OrdinalIgnoreCase));

        private static readonly (string Word, string Operator, int Precedence)[]
            ConcatenatedBinaryOperators =
            [
                ("AND", "&&", 20),
                ("OR", "||", 10),
                ("LIKE", "LIKE", 30),
                ("MOD", "%", 50)
            ];

        // Word operators (AND/OR/MOD/LIKE/NOT LIKE) are valid only in infix
        // position. In operand position the same words are legacy variable
        // names (XPA codes such as OR, AND, MOD) and stay identifiers.
        private bool TryGetBinaryOperator(out string op, out int precedence)
        {
            if (_current.Kind == TokenKind.Identifier)
            {
                switch (_current.Text.ToUpperInvariant())
                {
                    case "OR":
                        op = "||";
                        precedence = 10;
                        return true;
                    case "AND":
                        op = "&&";
                        precedence = 20;
                        return true;
                    case "LIKE":
                        op = "LIKE";
                        precedence = 30;
                        return true;
                    case "MOD":
                        op = "%";
                        precedence = 50;
                        return true;
                    case "NOT":
                        var next = _lexer.Peek();
                        if (next.Kind == TokenKind.Identifier &&
                            string.Equals(next.Text, "LIKE", StringComparison.OrdinalIgnoreCase))
                        {
                            op = "NOT LIKE";
                            precedence = 30;
                            return true;
                        }
                        break;
                }
            }

            return XpaTypedExpressionEmitter.TryGetBinaryOperator(_current, out op, out precedence);
        }

        private XpaTypedExpression ParsePrefix()
        {
            if (_current.Kind == TokenKind.Operator &&
                _current.Text is "+" or "-" or "!")
            {
                var op = _current.Text;
                Advance();
                return EmitUnary(op, ParseExpression(70));
            }

            if (_current.Kind == TokenKind.Identifier &&
                string.Equals(_current.Text, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                return EmitUnary("!", ParseExpression(70));
            }

            return ParsePrimary();
        }

        private XpaTypedExpression ParsePrimary()
        {
            if (_current.Kind == TokenKind.String)
            {
                var value = _current.Text;
                Advance();
                return ParsePostfix(ApplyLiteralSuffix(
                    new XpaTypedExpression(ToCSharpString(value), "Text", XpaType.Text, value),
                    value));
            }

            if (_current.Kind == TokenKind.Number)
            {
                var value = _current.Text;
                Advance();
                return ParsePostfix(ApplyLiteralSuffix(
                    new XpaTypedExpression(value, "Number", XpaType.Number),
                    value));
            }

            if (_current.Kind == TokenKind.Date)
            {
                var value = _current.Text;
                Advance();
                var parts = value.Split('/');
                if (parts.Length != 3 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month) ||
                    !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
                {
                    throw new ExpressionParseException();
                }

                return ParsePostfix(day == 0 || month == 0 || year == 0
                    ? new XpaTypedExpression("Date.Empty", "Date", XpaType.Date)
                    : new XpaTypedExpression($"new Date({year}, {month}, {day})", "Date", XpaType.Date));
            }

            if (_current.Kind == TokenKind.OpenParenthesis)
            {
                Advance();
                var value = ParseExpression(0);
                Require(TokenKind.CloseParenthesis);
                Advance();
                return ParsePostfix(value with { Code = $"({value.Code})" });
            }

            if (_current.Kind != TokenKind.Identifier)
                throw new ExpressionParseException();

            var name = _current.Text;
            Advance();
            if (_current.Kind == TokenKind.OpenParenthesis)
                return ParsePostfix(ParseFunction(name));

            if (string.Equals(name, "TRUE", StringComparison.OrdinalIgnoreCase))
                return ParsePostfix(new XpaTypedExpression("true", "Bool", XpaType.Bool));
            if (string.Equals(name, "FALSE", StringComparison.OrdinalIgnoreCase))
                return ParsePostfix(new XpaTypedExpression("false", "Bool", XpaType.Bool));
            if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase))
                return ParsePostfix(new XpaTypedExpression("u.Null()", "object", XpaType.Object));

            var resolvedSymbol = _context.ResolveSymbol(name);
            // Some XPA exports concatenate the unary NOT token with the
            // alphabetic variable alias (for example NOTCH instead of NOT CH).
            // Split it only when the complete token is not a symbol and the
            // suffix is a real symbol in the current task.
            if (name.Length > 3 &&
                name.StartsWith("NOT", StringComparison.OrdinalIgnoreCase))
            {
                var operand = _context.ResolveSymbol(name[3..]);
                var fullTokenIsUnresolved =
                    !resolvedSymbol.HasValue ||
                    (resolvedSymbol.Value.Type == XpaType.Object &&
                     string.Equals(resolvedSymbol.Value.Code, name, StringComparison.OrdinalIgnoreCase));
                if (fullTokenIsUnresolved &&
                    operand.HasValue &&
                    !(operand.Value.Type == XpaType.Object &&
                      string.Equals(operand.Value.Code, name[3..], StringComparison.OrdinalIgnoreCase)))
                    return ParsePostfix(EmitUnary("!", operand.Value));
            }

            if (resolvedSymbol.HasValue)
                return ParsePostfix(resolvedSymbol.Value);

            return ParsePostfix(new XpaTypedExpression(name, "object", XpaType.Object));
        }

        private XpaTypedExpression ApplyLiteralSuffix(
            XpaTypedExpression literal,
            string literalValue)
        {
            if (_current.Kind != TokenKind.Identifier)
                return literal;

            var suffix = _current.Text.ToUpperInvariant();
            var contextualResult = _context.ResolveLiteralSuffix(suffix, literalValue);
            if (contextualResult.HasValue)
            {
                Advance();
                return contextualResult.Value;
            }

            XpaTypedExpression result;
            switch (suffix)
            {
                case "LOG":
                    if (string.Equals(literalValue, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(literalValue, "S", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(literalValue, "Y", StringComparison.OrdinalIgnoreCase))
                        result = new XpaTypedExpression("true", "Bool", XpaType.Bool);
                    else if (string.Equals(literalValue, "FALSE", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(literalValue, "N", StringComparison.OrdinalIgnoreCase))
                        result = new XpaTypedExpression("false", "Bool", XpaType.Bool);
                    else
                        return literal;
                    break;
                case "DATE":
                    var parts = literalValue.Split('/');
                    if (parts.Length != 3 ||
                        !int.TryParse(parts[0], out var day) ||
                        !int.TryParse(parts[1], out var month) ||
                        !int.TryParse(parts[2], out var year))
                    {
                        return literal;
                    }
                    result = day == 0 || month == 0 || year == 0
                        ? new XpaTypedExpression("Date.Empty", "Date", XpaType.Date)
                        : new XpaTypedExpression(
                            $"new Date({year}, {month}, {day})",
                            "Date",
                            XpaType.Date);
                    break;
                case "TIME":
                    if (string.Equals(literalValue, "00/00/0000", StringComparison.Ordinal))
                    {
                        result = new XpaTypedExpression("Time.Empty", "Time", XpaType.Time);
                        break;
                    }
                    var timeParts = literalValue.Split(':');
                    var second = 0;
                    if (timeParts.Length is < 2 or > 3 ||
                        !int.TryParse(timeParts[0], out var hour) ||
                        !int.TryParse(timeParts[1], out var minute) ||
                        (timeParts.Length == 3 && !int.TryParse(timeParts[2], out second)))
                    {
                        return literal;
                    }
                    result = new XpaTypedExpression(
                        $"new Time({hour}, {minute}, {second})",
                        "Time",
                        XpaType.Time);
                    break;
                case "PROG":
                    var comma = literalValue.IndexOf(',');
                    if (comma <= 0 ||
                        !int.TryParse(literalValue[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out var program))
                    {
                        return literal;
                    }
                    result = new XpaTypedExpression(
                        program.ToString(CultureInfo.InvariantCulture),
                        "Number",
                        XpaType.Number);
                    break;
                case "INDEX":
                    result = int.TryParse(
                        literalValue,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var index)
                        ? new XpaTypedExpression(
                            index.ToString(CultureInfo.InvariantCulture),
                            "Number",
                            XpaType.Number)
                        : literal;
                    break;
                case "EXP":
                    if (!int.TryParse(
                            literalValue,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var expressionOrdinal))
                    {
                        return literal;
                    }
                    result = new XpaTypedExpression(
                        $"Exp_{expressionOrdinal}()",
                        "object",
                        XpaType.Object);
                    break;
                case "MODE":
                case "HEB":
                case "DSOURCE":
                case "RIGHT":
                case "MENU":
                    result = literal with { ReturnType = "Text", Type = XpaType.Text };
                    break;
                case "F":
                    if (!decimal.TryParse(
                            literalValue,
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out _))
                    {
                        return literal;
                    }
                    result = new XpaTypedExpression(literalValue, "Number", XpaType.Number);
                    break;
                case "FORM":
                    if (!decimal.TryParse(
                            literalValue.Replace(',', '.'),
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out var formNumber))
                    {
                        return literal;
                    }
                    result = new XpaTypedExpression(
                        formNumber.ToString(CultureInfo.InvariantCulture) + "m",
                        "Number",
                        XpaType.Number);
                    break;
                case "VAR":
                    var variable = _context.ResolveSymbol(literalValue);
                    result = variable is null
                        ? literal
                        : new XpaTypedExpression(
                            $"u.IndexOf({variable.Value.Code})",
                            "Number",
                            XpaType.Number);
                    break;
                case "KBD":
                    result = new XpaTypedExpression(
                        ToCSharpString($"<{literalValue}>"),
                        "Text",
                        XpaType.Text);
                    break;
                case "EVENT":
                    result = new XpaTypedExpression(
                        ToCSharpString($"[{literalValue}]"),
                        "Text",
                        XpaType.Text);
                    break;
                default:
                    return literal;
            }

            Advance();
            return result;
        }

        private XpaTypedExpression ParsePostfix(XpaTypedExpression target)
        {
            while (_current.Kind is TokenKind.Dot or TokenKind.OpenBracket)
            {
                if (_current.Kind == TokenKind.OpenBracket)
                {
                    Advance();
                    var index = ParseExpression(0);
                    Require(TokenKind.CloseBracket);
                    Advance();
                    var numericIndex = ConvertRequired(index, XpaType.Number);
                    target = new XpaTypedExpression(
                        $"{target.Code}[{numericIndex.Code}]",
                        ResolveArrayItemReturnType(target.ReturnType),
                        ResolveArrayItemType(target.ReturnType));
                    continue;
                }

                Advance();
                Require(TokenKind.Identifier);
                var member = _current.Text;
                Advance();
                var qualifiedName = $"{target.Code}.{member}";
                if (_current.Kind == TokenKind.OpenParenthesis)
                {
                    target = ParseFunction(qualifiedName);
                    continue;
                }

                // DotNet is an XPA namespace marker, not a C# identifier. Keep
                // the complete qualified path intact until we know whether its
                // terminal node is a type constructor, static call or member.
                target = qualifiedName.StartsWith("DotNet.", StringComparison.Ordinal)
                    ? new XpaTypedExpression(qualifiedName, "object", XpaType.Object)
                    : _context.ResolveSymbol(qualifiedName) ??
                      new XpaTypedExpression(qualifiedName, "object", XpaType.Object);
            }

            if (target.Code.StartsWith("DotNet.", StringComparison.Ordinal))
                target = _context.ResolveSymbol(target.Code) ?? target;

            return target;
        }

        private static string ResolveArrayItemReturnType(string returnType)
        {
            var canonical = XpaExpressionTypeMap.CanonicalTypeName(returnType);
            if (!canonical.EndsWith("[]", StringComparison.Ordinal))
                return "object";
            return canonical[..^2];
        }

        private static XpaType ResolveArrayItemType(string returnType)
            => XpaExpressionTypeMap.FromReturnType(ResolveArrayItemReturnType(returnType));

        private XpaTypedExpression ParseFunction(string name)
        {
            Require(TokenKind.OpenParenthesis);
            Advance();
            var arguments = new List<XpaTypedExpression>();
            if (_current.Kind != TokenKind.CloseParenthesis)
            {
                while (true)
                {
                    arguments.Add(ParseExpression(0));
                    if (_current.Kind == TokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    // Some XPA exports omit an argument separator between two
                    // complete expressions. Keep recovery in the source parser:
                    // the emitted C# is still produced from typed argument nodes.
                    if (!CanStartImplicitArgument(_current.Kind))
                        break;
                }
            }
            Require(TokenKind.CloseParenthesis);
            Advance();

            if (string.Equals(name, "IF", StringComparison.OrdinalIgnoreCase) && arguments.Count == 3)
                return EmitConditional(arguments[0], arguments[1], arguments[2]);

            return _context.ResolveFunction(name, arguments) ??
                   new XpaTypedExpression(
                       $"{name}({JoinCodes(arguments)})",
                       "object",
                       XpaType.Object);
        }

        private static bool CanStartImplicitArgument(TokenKind kind)
            => kind is TokenKind.Identifier or
                       TokenKind.Number or
                       TokenKind.Date or
                       TokenKind.String or
                       TokenKind.OpenParenthesis;

        private static XpaTypedExpression EmitUnary(
            string op,
            XpaTypedExpression operand)
        {
            if (op == "!")
            {
                var boolean = ConvertRequired(operand, XpaType.Bool);
                return new XpaTypedExpression($"!({boolean.Code})", "Bool", XpaType.Bool);
            }

            var number = ConvertRequired(operand, XpaType.Number);
            return new XpaTypedExpression(
                op == "-" ? $"-({number.Code})" : $"+({number.Code})",
                "Number",
                XpaType.Number);
        }

        private static XpaTypedExpression EmitBinary(
            string op,
            XpaTypedExpression left,
            XpaTypedExpression right)
        {
            if (op is "&&" or "||")
            {
                var lhs = ConvertRequired(left, XpaType.Bool);
                var rhs = ConvertRequired(right, XpaType.Bool);
                return new XpaTypedExpression(
                    $"({lhs.Code}) {op} ({rhs.Code})",
                    "Bool",
                    XpaType.Bool);
            }

            if (op is "==" or "!=" or "<" or "<=" or ">" or ">=")
                return EmitComparison(op, left, right);

            if (op is "LIKE" or "NOT LIKE")
            {
                var lhs = ConvertRequired(left, XpaType.Text);
                var rhs = ConvertRequired(right, XpaType.Text);
                var call = $"u.Like({lhs.Code}, {rhs.Code})";
                return new XpaTypedExpression(
                    op == "NOT LIKE" ? $"u.Not({call})" : call,
                    "Bool",
                    XpaType.Bool);
            }

            if (op == "^")
            {
                var lhs = ConvertRequired(left, XpaType.Number);
                var rhs = ConvertRequired(right, XpaType.Number);
                return new XpaTypedExpression(
                    $"u.Pow({lhs.Code}, {rhs.Code})",
                    "Number",
                    XpaType.Number);
            }

            if (op == "&" ||
                (op == "+" && (left.Type == XpaType.Text || right.Type == XpaType.Text)))
            {
                var lhs = ConvertRequired(left, XpaType.Text);
                var rhs = ConvertRequired(right, XpaType.Text);
                return new XpaTypedExpression(
                    $"{lhs.Code} + {rhs.Code}",
                    "Text",
                    XpaType.Text);
            }

            if (op is "+" or "-" &&
                left.Type is XpaType.Date or XpaType.Time &&
                right.Type == XpaType.Number)
            {
                return new XpaTypedExpression(
                    $"{left.Code} {op} {right.Code}",
                    left.ReturnType,
                    left.Type);
            }

            if (op == "-" && left.Type == right.Type && left.Type == XpaType.Time)
            {
                return new XpaTypedExpression(
                    $"u.ToNumber({left.Code}) - u.ToNumber({right.Code})",
                    "Number",
                    XpaType.Number);
            }

            if (op == "-" && left.Type == right.Type && left.Type == XpaType.Date)
            {
                return new XpaTypedExpression(
                    $"{left.Code} - {right.Code}",
                    "Number",
                    XpaType.Number);
            }

            var numericLeft = ConvertRequired(left, XpaType.Number);
            var numericRight = ConvertRequired(right, XpaType.Number);
            return new XpaTypedExpression(
                $"{numericLeft.Code} {op} {numericRight.Code}",
                "Number",
                XpaType.Number);
        }

        private static XpaTypedExpression EmitComparison(
            string op,
            XpaTypedExpression left,
            XpaTypedExpression right)
        {
            var commonType = XpaExpressionTypeMap.Unify(left.Type, right.Type);
            var lhs = ConvertRequired(left, commonType);
            var rhs = ConvertRequired(right, commonType);
            return new XpaTypedExpression(
                $"{lhs.Code} {op} {rhs.Code}",
                "Bool",
                XpaType.Bool);
        }

        private XpaTypedExpression EmitConditional(
            XpaTypedExpression condition,
            XpaTypedExpression whenTrue,
            XpaTypedExpression whenFalse)
        {
            var boolean = ConvertRequired(condition, XpaType.Bool);
            var resultType = XpaExpressionTypeMap.Unify(whenTrue.Type, whenFalse.Type);
            if (resultType == XpaType.Object &&
                _current.Kind == TokenKind.End &&
                _context.Destination.Type is not (XpaType.Unknown or XpaType.Object))
            {
                resultType = _context.Destination.Type;
            }
            var trueValue = ConvertRequired(whenTrue, resultType);
            var falseValue = ConvertRequired(whenFalse, resultType);
            if (resultType == XpaType.Object)
            {
                return new XpaTypedExpression(
                    $"(({boolean.Code}) ? ({trueValue.Code}) : ({falseValue.Code}))",
                    "object",
                    XpaType.Object);
            }

            return new XpaTypedExpression(
                $"u.If({boolean.Code}, {trueValue.Code}, {falseValue.Code})",
                trueValue.ReturnType,
                resultType);
        }

        private static XpaTypedExpression ConvertRequired(
            XpaTypedExpression source,
            XpaType destinationType)
        {
            var destination = new XpaExpressionDestination(
                ReturnType(destinationType),
                destinationType);
            if (!XpaExpressionTypeMap.TryApply(source, destination, out var converted))
                throw new ExpressionParseException();
            return converted;
        }

        private static string ReturnType(XpaType type)
            => type switch
            {
                XpaType.Text => "Text",
                XpaType.Number => "Number",
                XpaType.Date => "Date",
                XpaType.Time => "Time",
                XpaType.Bool => "Bool",
                XpaType.Blob => "byte[]",
                XpaType.Array => "object[]",
                XpaType.Object => "object",
                _ => ""
            };

        private static string JoinCodes(IReadOnlyList<XpaTypedExpression> values)
        {
            if (values.Count == 0)
                return "";
            var codes = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
                codes[i] = values[i].Code;
            return string.Join(", ", codes);
        }

        private static string ToCSharpString(string value)
            => "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

        private void Require(TokenKind kind)
        {
            if (_current.Kind != kind)
                throw new ExpressionParseException();
        }

        private void Advance() => _current = _lexer.Next();
    }

    private sealed class Lexer
    {
        private readonly string _source;
        private int _position;

        internal Lexer(string source) => _source = source;

        internal Token Next()
        {
            SkipWhiteSpace();
            if (_position >= _source.Length)
                return new Token(TokenKind.End, "");

            var ch = _source[_position];
            if (ch is '\'' or '"')
                return ReadString(ch);
            if (char.IsDigit(ch))
                return TryReadDate(out var date) ? date : ReadNumber();
            if (IsIdentifierStart(ch))
                return ReadIdentifierOrWordOperator();

            _position++;
            return ch switch
            {
                '(' => new Token(TokenKind.OpenParenthesis, "("),
                ')' => new Token(TokenKind.CloseParenthesis, ")"),
                '[' => new Token(TokenKind.OpenBracket, "["),
                ']' => new Token(TokenKind.CloseBracket, "]"),
                '.' => new Token(TokenKind.Dot, "."),
                ',' => new Token(TokenKind.Comma, ","),
                '+' or '-' or '*' or '/' or '%' or '^' =>
                    new Token(TokenKind.Operator, ch.ToString()),
                '=' when Match('=') => new Token(TokenKind.Operator, "=="),
                '=' => new Token(TokenKind.Operator, "=="),
                '!' when Match('=') => new Token(TokenKind.Operator, "!="),
                '!' => new Token(TokenKind.Operator, "!"),
                '<' when Match('=') => new Token(TokenKind.Operator, "<="),
                '<' when Match('>') => new Token(TokenKind.Operator, "!="),
                '<' => new Token(TokenKind.Operator, "<"),
                '>' when Match('=') => new Token(TokenKind.Operator, ">="),
                '>' => new Token(TokenKind.Operator, ">"),
                '&' when Match('&') => new Token(TokenKind.Operator, "&&"),
                '&' => new Token(TokenKind.Operator, "&"),
                '|' when Match('|') => new Token(TokenKind.Operator, "||"),
                _ => throw new ExpressionParseException()
            };
        }

        private Token ReadString(char quote)
        {
            _position++;
            var result = new System.Text.StringBuilder();
            while (_position < _source.Length)
            {
                var ch = _source[_position++];
                if (ch != quote)
                {
                    result.Append(ch);
                    continue;
                }

                if (_position < _source.Length && _source[_position] == quote)
                {
                    result.Append(quote);
                    _position++;
                    continue;
                }

                return new Token(TokenKind.String, result.ToString());
            }
            throw new ExpressionParseException();
        }

        private Token ReadNumber()
        {
            // XPA stores decimals with '.', never with locale ','. A comma is
            // always an argument/item separator, so it must not be consumed
            // here (otherwise Stat(0,'C'MODE) lexes "0," as a number and the
            // whole call fails to parse).
            var start = _position;
            while (_position < _source.Length && char.IsDigit(_source[_position]))
                _position++;

            if (_position < _source.Length && _source[_position] == '.')
            {
                var dot = _position;
                _position++;
                while (_position < _source.Length && char.IsDigit(_source[_position]))
                    _position++;

                // XPA tolerates a trailing dot ("0."); C# does not, so drop it.
                if (_position == dot + 1)
                    return new Token(TokenKind.Number, _source[start..dot]);
            }

            return new Token(TokenKind.Number, _source[start.._position]);
        }

        private bool TryReadDate(out Token token)
        {
            token = default;
            var start = _position;
            var cursor = start;

            var dayDigits = ReadDigits(ref cursor, 2);
            if (dayDigits is < 1 or > 2 ||
                cursor >= _source.Length ||
                _source[cursor++] != '/')
            {
                return false;
            }

            var monthDigits = ReadDigits(ref cursor, 2);
            if (monthDigits is < 1 or > 2 ||
                cursor >= _source.Length ||
                _source[cursor++] != '/')
            {
                return false;
            }

            var yearStart = cursor;
            var yearDigits = ReadDigits(ref cursor, 4);
            var isZeroTwoDigitYear =
                yearDigits == 2 &&
                _source.AsSpan(yearStart, yearDigits).Trim('0').Length == 0;
            if ((yearDigits != 4 && !isZeroTwoDigitYear) ||
                (cursor < _source.Length && (char.IsDigit(_source[cursor]) || _source[cursor] == '/')))
            {
                return false;
            }

            _position = cursor;
            token = new Token(TokenKind.Date, _source[start..cursor]);
            return true;
        }

        private int ReadDigits(ref int cursor, int maximum)
        {
            var start = cursor;
            while (cursor < _source.Length &&
                   cursor - start < maximum &&
                   char.IsDigit(_source[cursor]))
            {
                cursor++;
            }

            return cursor - start;
        }

        internal Token Peek()
        {
            var saved = _position;
            var token = Next();
            _position = saved;
            return token;
        }

        private Token ReadIdentifierOrWordOperator()
        {
            var start = _position++;
            while (_position < _source.Length && IsIdentifierPart(_source[_position]))
                _position++;

            // AND/OR/MOD/LIKE/NOT are returned as plain identifiers. Whether a
            // word acts as an operator or as a legacy variable name (OR, AND,
            // MOD are valid XPA variable codes) is decided by the parser from
            // the syntactic position, not by the lexer.
            return new Token(TokenKind.Identifier, _source[start.._position]);
        }

        private bool ReadWord(string expected)
        {
            if (_position + expected.Length > _source.Length ||
                !string.Equals(
                    _source.Substring(_position, expected.Length),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var end = _position + expected.Length;
            if (end < _source.Length && IsIdentifierPart(_source[end]))
                return false;
            _position = end;
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
                _position++;
        }

        private bool Match(char expected)
        {
            if (_position >= _source.Length || _source[_position] != expected)
                return false;
            _position++;
            return true;
        }

        private static bool IsIdentifierStart(char ch)
            => char.IsLetter(ch) || ch is '_' or '@';

        private static bool IsIdentifierPart(char ch)
            => char.IsLetterOrDigit(ch) || ch is '_' or '@' or '$';
    }

    private static bool TryGetBinaryOperator(
        Token token,
        out string op,
        out int precedence)
    {
        op = token.Text;
        precedence = token.Kind == TokenKind.Operator
            ? op switch
            {
                "||" => 10,
                "&&" => 20,
                "==" or "!=" or "<" or "<=" or ">" or ">=" or "LIKE" or "NOT LIKE" => 30,
                "+" or "-" or "&" => 40,
                "*" or "/" or "%" => 50,
                "^" => 60,
                _ => -1
            }
            : -1;
        return precedence >= 0;
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private enum TokenKind
    {
        End,
        Identifier,
        Number,
        Date,
        String,
        Operator,
        OpenParenthesis,
        CloseParenthesis,
        OpenBracket,
        CloseBracket,
        Dot,
        Comma
    }

    private sealed class ExpressionParseException : Exception;
}
