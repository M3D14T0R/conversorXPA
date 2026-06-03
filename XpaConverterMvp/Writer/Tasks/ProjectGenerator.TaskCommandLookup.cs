using System;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string NormalizeCommandLookupKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = Regex.Replace(value, @"[^A-Za-z0-9]+", "", RegexOptions.CultureInvariant)
            .ToLowerInvariant();
        if (normalized.EndsWith("command", StringComparison.Ordinal))
            normalized = normalized[..^"command".Length];
        return normalized;
    }
}

