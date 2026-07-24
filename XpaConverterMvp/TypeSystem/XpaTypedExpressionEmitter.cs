using System;
using System.Collections.Generic;
using System.Globalization;

namespace XpaConverterMvp.TypeSystem;

internal readonly record struct XpaTypedExpressionContext(
    Func<string, XpaTypedExpression?> ResolveSymbol,
    Func<string, IReadOnlyList<XpaTypedExpression>, XpaTypedExpression?> ResolveFunction,
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
            while (TryGetBinaryOperator(_current, out var op, out var precedence) &&
                   precedence >= minimumPrecedence)
            {
                Advance();
                var right = ParseExpression(precedence + 1);
                left = EmitBinary(op, left, right);
            }
            return left;
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
                    new XpaTypedExpression(ToCSharpString(value), "Text", XpaType.Text),
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

            return ParsePostfix(
                _context.ResolveSymbol(name) ??
                new XpaTypedExpression(name, "object", XpaType.Object));
        }

        private XpaTypedExpression ApplyLiteralSuffix(
            XpaTypedExpression literal,
            string literalValue)
        {
            if (_current.Kind != TokenKind.Identifier)
                return literal;

            var suffix = _current.Text.ToUpperInvariant();
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
                    result = literal with { ReturnType = "Text", Type = XpaType.Text };
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
                    result = _context.ResolveSymbol(literalValue) ?? literal;
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

                target = _context.ResolveSymbol(qualifiedName) ??
                         new XpaTypedExpression(qualifiedName, "object", XpaType.Object);
            }

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
                    if (_current.Kind != TokenKind.Comma)
                        break;
                    Advance();
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

            if (op == "-" && left.Type == right.Type &&
                left.Type is XpaType.Date or XpaType.Time)
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

        private static XpaTypedExpression EmitConditional(
            XpaTypedExpression condition,
            XpaTypedExpression whenTrue,
            XpaTypedExpression whenFalse)
        {
            var boolean = ConvertRequired(condition, XpaType.Bool);
            var resultType = XpaExpressionTypeMap.Unify(whenTrue.Type, whenFalse.Type);
            var trueValue = ConvertRequired(whenTrue, resultType);
            var falseValue = ConvertRequired(whenFalse, resultType);
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
            if (ch == '\'' && TryReadApostropheSuffix(out var suffix))
                return new Token(TokenKind.Identifier, suffix);
            if (ch is '\'' or '"')
                return ReadString(ch);
            if (char.IsDigit(ch))
                return ReadNumber();
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

        private bool TryReadApostropheSuffix(out string suffix)
        {
            suffix = "";
            var start = _position + 1;
            if (start >= _source.Length || !char.IsLetter(_source[start]))
                return false;

            var end = start + 1;
            while (end < _source.Length && char.IsLetter(_source[end]))
                end++;

            var candidate = _source[start..end];
            if (!IsTypedSuffix(candidate) ||
                (end < _source.Length && IsIdentifierPart(_source[end])))
            {
                return false;
            }

            _position = end;
            suffix = candidate;
            return true;
        }

        private static bool IsTypedSuffix(string value)
            => value.ToUpperInvariant() is
                "DSOURCE" or "RIGHT" or "LOG" or "VAR" or "EXP" or
                "DATE" or "TIME" or "KBD" or "EVENT" or "HEB" or
                "FORM" or "PROG" or "MODE" or "INDEX";

        private Token ReadNumber()
        {
            var start = _position;
            while (_position < _source.Length &&
                   (char.IsDigit(_source[_position]) ||
                    _source[_position] is '.' or ','))
            {
                _position++;
            }

            var raw = _source[start.._position].Replace(',', '.');
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                throw new ExpressionParseException();
            return new Token(TokenKind.Number, raw);
        }

        private Token ReadIdentifierOrWordOperator()
        {
            var start = _position++;
            while (_position < _source.Length && IsIdentifierPart(_source[_position]))
                _position++;

            var value = _source[start.._position];
            if (string.Equals(value, "AND", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Operator, "&&");
            if (string.Equals(value, "OR", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Operator, "||");
            if (string.Equals(value, "MOD", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Operator, "%");
            if (string.Equals(value, "LIKE", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Operator, "LIKE");
            if (string.Equals(value, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                var saved = _position;
                SkipWhiteSpace();
                if (ReadWord("LIKE"))
                    return new Token(TokenKind.Operator, "NOT LIKE");
                _position = saved;
            }

            return new Token(TokenKind.Identifier, value);
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
