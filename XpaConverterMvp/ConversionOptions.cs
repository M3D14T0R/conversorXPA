using System.Globalization;

namespace XpaConverterMvp;

public sealed class ConversionOptions
{
    public string XmlPath { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string? AppNamespace { get; set; }
    public string OutputType { get; set; } = "WinExe";
    public string RuntimeCoreReferenceMode { get; set; } = "Project";
    public string? RuntimeCoreDllPath { get; set; }
    public string? RuntimeRootPath { get; set; }
    public string? FolderFilter { get; set; }
    public List<string> TaskFilters { get; } = new();
    public List<TopLevelTaskRange> TaskRanges { get; } = new();
    public List<string> TablesXmlPaths { get; } = new();
    public List<string> ComponentXmlPaths { get; } = new();
    public List<string> ProjectReferencePaths { get; } = new();
    public Dictionary<string, string> ProjectReferenceMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DllReferenceMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DisableImplicitComponentXmlResolution { get; set; }
    public bool WithDependencies { get; set; }
    public bool FullSolution { get; set; }
    public bool ParallelTaskGeneration { get; set; }
    public bool IncrementalOutput { get; set; }

    public string EnvReferenceMode
    {
        get => RuntimeCoreReferenceMode;
        set => RuntimeCoreReferenceMode = value;
    }

    public string? EnvDllPath
    {
        get => RuntimeCoreDllPath;
        set => RuntimeCoreDllPath = value;
    }
}

public readonly record struct TopLevelTaskRange(int Start, int End)
{
    public bool Contains(int index)
        => index >= Start && index <= End;

    public override string ToString()
        => Start == End
            ? Start.ToString(CultureInfo.InvariantCulture)
            : $"{Start.ToString(CultureInfo.InvariantCulture)}-{End.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out TopLevelTaskRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var raw = value.Trim();
        var separator = raw.IndexOf('-');
        if (separator < 0)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var single) || single <= 0)
                return false;

            range = new TopLevelTaskRange(single, single);
            return true;
        }

        if (separator == 0 || separator == raw.Length - 1)
            return false;

        var startText = raw[..separator].Trim();
        var endText = raw[(separator + 1)..].Trim();
        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            start <= 0 ||
            end < start)
            return false;

        range = new TopLevelTaskRange(start, end);
        return true;
    }
}
