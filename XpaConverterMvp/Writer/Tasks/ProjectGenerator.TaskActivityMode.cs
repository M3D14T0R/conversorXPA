namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string? ResolveActivityByInitialMode(string initialMode)
    {
        return initialMode switch
        {
            "E" => "Activities.Browse",
            "D" => "Activities.Delete",
            "I" => "Activities.Insert",
            "C" => "Activities.Insert",
            "P" => "Activities.AsParent",
            _ => null
        };
    }


}

