namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string ResolveButtonModelCommandExpression(int? internalEventId)
    {
        return internalEventId switch
        {
            42 => "Command.Select",
            13 => "Command.CloseForm",
            _ => ""
        };
    }
}

