using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveWholeExpressionPage(TaskSemantic task)
    {
        var textStreams = ResolveTextIoStreams(task);
        if (textStreams.Count > 0)
            return $"{textStreams[0].VariableName}.Page";
        return "_ioPrint.Page";
    }

    private static string ResolveWholeExpressionLine(TaskSemantic task)
    {
        var textStreams = ResolveTextIoStreams(task);
        if (textStreams.Count > 0 &&
            string.Equals(textStreams[0].StreamType, "TextPrinterWriter", StringComparison.OrdinalIgnoreCase))
            return $"{textStreams[0].VariableName}.HeightFromStartOfPage";
        return "u.Line()";
    }
}

