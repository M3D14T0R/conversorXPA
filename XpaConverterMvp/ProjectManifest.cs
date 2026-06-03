using System.Text.Json;
using System.Text.Json.Serialization;

namespace XpaConverterMvp;

internal sealed class ProjectManifest
{
    public string ComponentName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public Dictionary<string, string> Programs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> ProgramsByIndex { get; set; } = new();
    public List<ProjectManifestProgram> ProgramDetails { get; set; } = new();
    public Dictionary<string, string> DataObjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> DataObjectsByIndex { get; set; } = new();
    public List<ProjectManifestDataObject> DataObjectDetails { get; set; } = new();
    public Dictionary<string, string> Rights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProjectManifestFunction> Functions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string GetManifestPath(string sourcePath)
    {
        return Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? "",
            Path.GetFileNameWithoutExtension(sourcePath) + ".xpa-manifest.json");
    }

    public static ProjectManifest? LoadForSource(string sourcePath)
    {
        var manifestPath = GetManifestPath(sourcePath);
        if (!File.Exists(manifestPath))
            return null;

        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions);
    }

    public static ProjectManifest? LoadForProject(string projectPath)
    {
        return LoadForSource(projectPath);
    }

    public static ProjectManifest? LoadForBinary(string binaryPath)
    {
        return LoadForSource(binaryPath);
    }

    public void SaveForProject(string projectPath)
    {
        var manifestPath = GetManifestPath(projectPath);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(manifestPath, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class ProjectManifestProgram
{
    public int? ObjectIndex { get; set; }
    public string ClassName { get; set; } = "";
    public string? PublicName { get; set; }
    public bool IsPublic { get; set; }
    public List<string> ParameterTypes { get; set; } = new();
}

internal sealed class ProjectManifestFunction
{
    public string ClassName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public List<string> ParameterTypes { get; set; } = new();
}

internal sealed class ProjectManifestDataObject
{
    public int ObjectIndex { get; set; }
    public string Name { get; set; } = "";
    public string PhysicalName { get; set; } = "";
    public string? Comment { get; set; }
    public bool? Resident { get; set; }
    public string? PublicName { get; set; }
    public string? Owner { get; set; }
    public string DataSource { get; set; } = "";
    public List<ProjectManifestDataColumn> Columns { get; set; } = new();
    public List<ProjectManifestDataIndex> Indexes { get; set; } = new();
}

internal sealed class ProjectManifestDataColumn
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string AttrObj { get; set; } = "";
    public string Picture { get; set; } = "";
    public string? FieldPhysicalName { get; set; }
    public string? FieldPhysicalPicture { get; set; }
    public int? FieldPhysicalSize { get; set; }
    public bool? AllowedNull { get; set; }
    public string? Attribute { get; set; }
    public string? ContextCookies { get; set; }
    public string? DatabaseDefinition { get; set; }
    public string? Storage { get; set; }
    public string? Translate { get; set; }
    public string? DbColumnName { get; set; }
    public string? DbType { get; set; }
    public string? ModelRefObj { get; set; }
}

internal sealed class ProjectManifestIndexSegment
{
    public int ColumnId { get; set; }
    public string Order { get; set; } = "";
}

internal sealed class ProjectManifestDataIndex
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Unique { get; set; }
    public bool Primary { get; set; }
    public List<ProjectManifestIndexSegment> Segments { get; set; } = new();
}
