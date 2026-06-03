namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ToAsyncTypeReference(string taskTypeReference)
    {
        var lastDot = taskTypeReference.LastIndexOf('.');
        if (lastDot < 0)
            return taskTypeReference + "Async";
        return taskTypeReference[..(lastDot + 1)] + taskTypeReference[(lastDot + 1)..] + "Async";
    }
}

