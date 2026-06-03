namespace XpaConverterMvp;

public sealed record XmlCatalogReference(
    string Name,
    string Kind,
    string? XmlHint
);

public sealed class XmlCatalog
{
    public List<string> Folders { get; } = new();
    public List<string> Tasks { get; } = new();
    public List<string> Components { get; } = new();
    public List<XmlCatalogReference> References { get; } = new();
}
