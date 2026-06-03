using System;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string IndentLines(string text, int levels)
    {
        var pad = new string(' ', levels * 4);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(l => l.Length == 0 ? l : pad + l));
    }
}

