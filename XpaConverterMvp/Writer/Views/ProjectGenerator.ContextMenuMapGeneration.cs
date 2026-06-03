using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteContextMenuMap(int? systemPulldownObj, int? systemContextObj, IReadOnlyList<MenuDef> extraPulldowns, string viewsDir, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using XPARuntimeCore.Box.UI;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Views;");
        sb.AppendLine();
        sb.AppendLine("public class ContextMenuMap : ENV.UI.ContextMenuMap");
        sb.AppendLine("{");
        sb.AppendLine("    public ContextMenuMap(System.ComponentModel.IContainer container)");
        sb.AppendLine("    {");
        if (systemContextObj.HasValue)
            sb.AppendLine($"        this.Add({systemContextObj.Value}, () => new DefaultContextMenu(container));");
        if (systemPulldownObj.HasValue)
            sb.AppendLine($"        this.Add({systemPulldownObj.Value}, () => new DefaultPulldownMenu(container));");
        for (var i = 0; i < extraPulldowns.Count; i++)
            sb.AppendLine($"        this.Add({extraPulldowns[i].Obj}, () => new DefaultPulldownMenu{new string('_', i + 1)}(container));");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, "ContextMenuMap.cs"), sb.ToString());
    }
}

