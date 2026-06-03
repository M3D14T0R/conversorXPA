using System;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
private static string ApplyExpressionRuntimeNormalization(string expr)
{
    expr = RewriteCallProgExpressions(expr);
    expr = NormalizeDatabaseMetadataFunctions(expr);
    expr = NormalizeJsonFunctionSourceArguments(expr);
    expr = NormalizeMessageFunctionCalls(expr);
    expr = NormalizeRuntimeFunctionCalls(expr);
    expr = NormalizeRuntimeStaticMembers(expr);
    expr = NormalizeRightsExpressions(expr);
    expr = NormalizeMalformedNotFunctionCalls(expr);
    expr = NormalizeNotFileExistCalls(expr);
    return expr;
}

private static string NormalizeRuntimeStaticMembers(string expr)
{
    if (string.IsNullOrWhiteSpace(expr))
        return expr;

    expr = RewriteWholeMemberReference(expr, "u.Date.Now", "XPARuntimeCore.Box.Date.Now");
    expr = RewriteWholeMemberReference(expr, "u.Time.Now", "XPARuntimeCore.Box.Time.Now");
    return expr;
}
}

