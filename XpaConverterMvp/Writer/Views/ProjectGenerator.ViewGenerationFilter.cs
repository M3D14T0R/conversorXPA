namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static bool ShouldGenerateView(TaskSemantic t)
    {
        return (t.View.ShouldGenerate || HasMultiFormViewSwitchCandidate(t)) &&
               !ShouldSuppressViewForBusinessProcessTextIo(t);
    }

    private static bool HasMultiFormViewSwitchCandidate(TaskSemantic t)
        => t.DisplayExpressionId.HasValue &&
           t.FormEntries.Count(fe =>
               string.Equals(fe.Model, "FORM_GUI0", System.StringComparison.OrdinalIgnoreCase) &&
               fe.Form is not null) > 1;
}

