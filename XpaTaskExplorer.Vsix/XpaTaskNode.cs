using System.Collections.ObjectModel;

namespace XpaTaskExplorer;

public sealed class XpaTaskNode
{
    public string DisplayName => $"{Kind}  {OriginalName}";
    public string OriginalName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public ObservableCollection<XpaTaskNode> Children { get; } = new();
}
