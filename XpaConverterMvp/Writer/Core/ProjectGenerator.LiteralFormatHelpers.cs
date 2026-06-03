namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string DefaultFormatFor(string baseType)
        => baseType switch
        {
            "NumberColumn" => "10",
            "DateColumn" => "##/##/####",
            "TimeColumn" => "##:##:##",
            "BoolColumn" => "5",
            _ => "30"
        };

    private static string Escape(string s)
        => s
            .Replace("\\", "\\\\")
            .Replace("\r\n", "\\r\\n")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\"", "\\\"");

    private static string ToCSharpLiteral(string? s)
    {
        if (s is null)
            return "\"\"";
        var normalized = NormalizeMojibakedLiteralText(s);
        return "\"" + Escape(normalized) + "\"";
    }

    private static string NormalizeMojibakedLiteralText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (!value.Contains('\u00E2') && !value.Contains('\u00C3') && !value.Contains('\u00C2'))
            return value;

        return value
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
    }
}

