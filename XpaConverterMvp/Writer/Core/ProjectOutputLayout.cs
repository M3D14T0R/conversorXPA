using System.IO;

namespace XpaConverterMvp;

internal sealed class ProjectOutputLayout
{
    public ProjectOutputLayout(string outputRoot)
    {
        OutputRoot = outputRoot;
        TypesDir = Path.Combine(outputRoot, "Types");
        ModelsDir = Path.Combine(outputRoot, "Models");
        ViewsDir = Path.Combine(outputRoot, "Views");
        SharedDir = Path.Combine(outputRoot, "Shared");
        PropertiesDir = Path.Combine(outputRoot, "Properties");
        ProgramsDir = outputRoot;
    }

    public string OutputRoot { get; }
    public string TypesDir { get; }
    public string ModelsDir { get; }
    public string ViewsDir { get; }
    public string SharedDir { get; }
    public string PropertiesDir { get; }
    public string ProgramsDir { get; }
}
