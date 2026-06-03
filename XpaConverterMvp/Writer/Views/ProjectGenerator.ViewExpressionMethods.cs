using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly Regex ExpressionMethodCallRegex = new(
        @"\bExp_(\d+)\s*\(\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static void EmitExpressionMethodsForViewBindings(StringBuilder sb, TaskSemantic task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var telemetryOwner = ToTaskClassName(task.Description);
        var collectReferencesElapsed = TimeSpan.Zero;
        var resolveCodeElapsed = TimeSpan.Zero;
        var resolveReturnTypeElapsed = TimeSpan.Zero;
        var normalizeReturnElapsed = TimeSpan.Zero;
        var scanGeneratedReferenceElapsed = TimeSpan.Zero;
        var emittedExpressionCount = 0;
        var resolvedReferenceCount = 0;

        var forceEmitIds = new HashSet<int>();
        var ids = new SortedSet<int>(
            task.View.BindingExpressionIds
                .Where(id => !CanInlineStringLiteralExpression(task, id, dataObjects)));
        var collectReferencesStopwatch = Stopwatch.StartNew();
        var referencedExpressionIds = CollectReferencedExpressionIds(sb.ToString());
        collectReferencesStopwatch.Stop();
        collectReferencesElapsed += collectReferencesStopwatch.Elapsed;
        foreach (var referencedExpressionId in referencedExpressionIds)
        {
            forceEmitIds.Add(referencedExpressionId);
            ids.Add(referencedExpressionId);
        }
        foreach (var printExpressionId in task.Layout.PrintForms
                     .SelectMany(fe => fe.Form.Controls)
                     .Select(c => c.DataExpressionId)
                     .Where(x => x.HasValue)
                     .Select(x => x!.Value))
        {
            forceEmitIds.Add(printExpressionId);
            ids.Add(printExpressionId);
        }
        foreach (var textExpressionId in task.Layout.TextForms
                     .SelectMany(fe => fe.Form.Controls)
                     .Select(c => c.DataExpressionId)
                     .Where(x => x.HasValue)
                     .Select(x => x!.Value))
        {
            forceEmitIds.Add(textExpressionId);
            ids.Add(textExpressionId);
        }
        var expectedReturnTypeByExpressionId = BuildViewExpressionMethodExpectedReturnTypes(task);
        foreach (var formTextExpId in task.View.FormTextExpressionIds)
        {
            if (!ids.Contains(formTextExpId) && !TryResolveExpressionAsStringLiteralCode(task, formTextExpId, dataObjects, out _))
                ids.Add(formTextExpId);
        }
        foreach (var treeRootExpId in task.View.SelectedFormControls
                     .Where(c => string.Equals(c.Model, "CTRL_GUI0_TREE", StringComparison.OrdinalIgnoreCase) && c.TreeRootExpressionId.HasValue)
                     .Select(c => c.TreeRootExpressionId!.Value))
        {
            forceEmitIds.Add(treeRootExpId);
            ids.Add(treeRootExpId);
        }
        if (task.View.SelectedFormTextExpressionId is int selectedFormTextExpId)
        {
            forceEmitIds.Add(selectedFormTextExpId);
            ids.Add(selectedFormTextExpId);
        }
        if (ids.Count == 0)
            return;

        var emittedIds = new HashSet<int>();
        var pendingIds = ids;
        var expressionBlock = new StringBuilder();

        while (true)
        {
            var appendedAny = false;
            var passIds = pendingIds.Where(id => !emittedIds.Contains(id)).ToArray();
            foreach (var id in passIds)
            {
                if (!emittedIds.Add(id))
                    continue;

                task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(id, out var exp);
                if (exp is null)
                    continue;
                if (!forceEmitIds.Contains(id) && CanInlineStringLiteralExpression(task, id, dataObjects))
                    continue;

                var resolveCodeStopwatch = Stopwatch.StartNew();
                var context = expectedReturnTypeByExpressionId.TryGetValue(id, out var expectedReturnType) &&
                              !string.IsNullOrWhiteSpace(expectedReturnType)
                    ? CreateExpectedEmissionContext(ExpectedTypeForReturnType(expectedReturnType))
                    : CreateViewBindingEmissionContext(exp.Attribute);
                var emitted = ResolveTypedExpressionEntryCode(exp, task, dataObjects, context);
                resolveCodeStopwatch.Stop();
                resolveCodeElapsed += resolveCodeStopwatch.Elapsed;
                var code = emitted.Code;
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                var returnTypeStopwatch = Stopwatch.StartNew();
                var returnType = expectedReturnTypeByExpressionId.TryGetValue(id, out expectedReturnType) &&
                                 !string.IsNullOrWhiteSpace(expectedReturnType)
                    ? expectedReturnType
                    : TryResolveStrictSourceReturnType(code, task, out var strictReturnType) &&
                      !string.IsNullOrWhiteSpace(strictReturnType) &&
                      !string.Equals(strictReturnType, "object", StringComparison.Ordinal)
                    ? strictReturnType
                    : emitted.HasEffectiveType
                    ? emitted.PreferredReturnType
                    : ResolveExpressionReturnType(exp.Attribute, code, task);
                if (string.IsNullOrWhiteSpace(expectedReturnType) &&
                    TryResolveSimpleViewExpressionMethodReturnType(code, task, out var simpleReturnType))
                    returnType = simpleReturnType;
                returnTypeStopwatch.Stop();
                resolveReturnTypeElapsed += returnTypeStopwatch.Elapsed;

                RegisterTypedExpressionReturnType(task, $"Exp_{id}()", returnType);
                expressionBlock.AppendLine($"    internal {returnType} Exp_{id}() => {code};");
                expressionBlock.AppendLine();
                appendedAny = true;
                emittedExpressionCount++;

                var scanGeneratedReferenceStopwatch = Stopwatch.StartNew();
                var generatedReferences = CollectReferencedExpressionIds(code);
                scanGeneratedReferenceStopwatch.Stop();
                scanGeneratedReferenceElapsed += scanGeneratedReferenceStopwatch.Elapsed;
                foreach (var referencedId in generatedReferences)
                {
                    forceEmitIds.Add(referencedId);
                    pendingIds.Add(referencedId);
                    resolvedReferenceCount++;
                }
            }

            if (!appendedAny)
                break;

            if (!pendingIds.Any(id => !emittedIds.Contains(id)))
                break;
        }

        if (expressionBlock.Length > 0)
        {
            sb.AppendLine("    #region Expressions");
            sb.Append(expressionBlock.ToString());
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }

        if (emittedExpressionCount > 0)
        {
            var measuredElapsed =
                collectReferencesElapsed +
                resolveCodeElapsed +
                resolveReturnTypeElapsed +
                normalizeReturnElapsed +
                scanGeneratedReferenceElapsed;
            ConversionTelemetry.LogDuration(
                "VIEW_EXPR",
                telemetryOwner,
                measuredElapsed,
                $"ids={ids.Count} emitted={emittedExpressionCount} refs={resolvedReferenceCount} collectRefsMs={collectReferencesElapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} resolveCodeMs={resolveCodeElapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} returnTypeMs={resolveReturnTypeElapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} normalizeMs={normalizeReturnElapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} scanRefsMs={scanGeneratedReferenceElapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
    }

    private static bool TryResolveSimpleViewExpressionMethodReturnType(string code, TaskSemantic task, out string returnType)
    {
        returnType = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var trimmed = StripRedundantOuterParentheses(code.Trim());
        if (!IsSimpleIdentifierPath(trimmed))
            return false;

        var targetInfo = ResolveTargetValueInfo(task, null, trimmed);
        if (targetInfo.IsDotNet &&
            targetInfo.Resource is not null &&
            !string.IsNullOrWhiteSpace(targetInfo.Resource.ObjectType))
        {
            returnType = NormalizeDotNetObjectType(targetInfo.Resource.ObjectType);
            return !string.IsNullOrWhiteSpace(returnType);
        }

        var attrObj = ResolveEffectiveTargetAttrObj(targetInfo.AttrObj ?? "", targetInfo.ModelAttrObj ?? "");
        if (string.IsNullOrWhiteSpace(attrObj))
            return false;

        returnType = MapAttrObjToReturnType(attrObj);
        return !string.IsNullOrWhiteSpace(returnType);
    }

    private static Dictionary<int, string> BuildViewExpressionMethodExpectedReturnTypes(TaskSemantic task)
    {
        var expected = new Dictionary<int, string>();
        foreach (var control in task.Layout.TextForms.SelectMany(fe => fe.Form.Controls))
        {
            if (!control.DataExpressionId.HasValue ||
                !task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(control.DataExpressionId.Value, out var exp) ||
                exp is null)
                continue;

            expected[control.DataExpressionId.Value] = ResolveViewExpressionReturnTypeForControl(control, exp.Attribute);
        }

        foreach (var control in task.Layout.PrintForms.SelectMany(fe => fe.Form.Controls))
        {
            if (!control.DataExpressionId.HasValue ||
                !task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(control.DataExpressionId.Value, out var exp) ||
                exp is null)
                continue;

            expected[control.DataExpressionId.Value] = ResolveViewExpressionReturnTypeForControl(control, exp.Attribute);
        }

        foreach (var control in task.View.SelectedFormControls)
        {
            if (!control.DataExpressionId.HasValue ||
                !task.ExpressionsSemantic.EntriesByOrdinal.TryGetValue(control.DataExpressionId.Value, out var exp) ||
                exp is null)
                continue;

            expected[control.DataExpressionId.Value] = ResolveViewExpressionReturnTypeForControl(control, exp.Attribute);
        }

        return expected;
    }

    private static string ResolveViewExpressionReturnTypeForControl(TaskFormControlDef control, string? attribute)
    {
        if (IsViewCheckBoxControl(control))
            return "Bool";

        if (string.Equals(control.Model, "CTRL_GUI0_IMAGE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(control.Model, "CTRL_GUI1_IMAGE", StringComparison.OrdinalIgnoreCase))
            return string.Equals(attribute, "O", StringComparison.OrdinalIgnoreCase) ? "byte[]" : "Text";

        return attribute switch
        {
            "N" => "Number",
            "D" => "Date",
            "T" => "Time",
            "B" => "Bool",
            _ => "Text"
        };
    }

    private static IReadOnlyList<int> CollectReferencedExpressionIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();

        var ids = new HashSet<int>();
        foreach (Match match in ExpressionMethodCallRegex.Matches(text))
        {
            if (match.Groups.Count < 2)
                continue;
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
        }

        return ids.OrderBy(x => x).ToList();
    }

}
