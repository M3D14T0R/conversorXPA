using System.Collections.Generic;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool TryParseFunctionCall(string expression, out string functionName, out List<string> args)
    {
        TrackLegacyExpressionTreatment("Parser", nameof(TryParseFunctionCall));
        functionName = "";
        args = new List<string>();
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var trimmed = expression.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen <= 0)
            return false;

        var closeParen = FindMatchingParen(trimmed, openParen);
        if (closeParen != trimmed.Length - 1)
            return false;

        functionName = trimmed[..openParen].Trim();
        args = SplitTopLevelArguments(trimmed[(openParen + 1)..closeParen]);
        return !string.IsNullOrWhiteSpace(functionName);
    }

    private static bool IsEmptyFunctionArgumentList(IReadOnlyList<string> args)
        => args.Count == 0 ||
           args.Count == 1 && string.IsNullOrWhiteSpace(args[0]);
}

