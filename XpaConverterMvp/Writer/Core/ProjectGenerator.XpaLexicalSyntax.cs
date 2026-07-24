using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool IsRuntimeCounterBinding(string key, string value)
        => string.Equals(key, "Counter", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, "Counter_", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "Counter", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "Counter_", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadLiteralPostfixToken(
        string text,
        int start,
        out string token,
        out int tokenEnd)
    {
        token = "";
        tokenEnd = start - 1;
        if (start >= text.Length || !char.IsLetter(text[start]))
            return false;

        var index = start;
        while (index < text.Length && char.IsLetter(text[index]))
            index++;

        token = text[start..index];
        tokenEnd = index - 1;
        return token.Length > 0;
    }

    private static bool TryReadXpaLiteral(
        string text,
        int startIndex,
        out int endIndex,
        out string literalValue,
        out string literalCode)
    {
        endIndex = startIndex;
        literalValue = "";
        literalCode = "";
        if (startIndex < 0 || startIndex >= text.Length)
            return false;

        var quote = text[startIndex];
        if (quote is not ('"' or '\''))
            return false;

        var value = new StringBuilder();
        var index = startIndex + 1;
        while (index < text.Length)
        {
            if (text[index] == quote)
            {
                if (quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    value.Append('\'');
                    index += 2;
                    continue;
                }

                if (quote == '"' && index > startIndex && text[index - 1] == '\\')
                {
                    value.Append('"');
                    index++;
                    continue;
                }

                endIndex = index;
                literalValue = value.ToString();
                literalCode = text[startIndex..(endIndex + 1)];
                return true;
            }

            value.Append(text[index]);
            index++;
        }

        return false;
    }
}
