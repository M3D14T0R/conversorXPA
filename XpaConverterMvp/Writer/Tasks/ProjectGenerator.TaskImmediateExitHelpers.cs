using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? ResolveImmediateExitReevaluationArgument(string? exitCondition, TaskSemantic task)
    {
        if (string.IsNullOrWhiteSpace(exitCondition))
            return null;
        var expr = exitCondition.Trim();
        if (Regex.IsMatch(expr, @"^_?[A-Za-z]\w*(\._?[A-Za-z]\w*)?$"))
            return expr;
        return null;
    }

}

