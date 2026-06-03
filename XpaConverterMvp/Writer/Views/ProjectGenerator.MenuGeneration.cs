using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteApplicationMdiMenu(MenuDef? menu, string viewsDir, string appNamespace, IReadOnlyList<TaskSemantic> allTasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using ENV.UI.Menus;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Views;");
        sb.AppendLine();
        sb.AppendLine("public class ApplicationMdiMenu : MenuStripBase");
        sb.AppendLine("{");
        sb.AppendLine("    public ApplicationMdiMenu()");
        sb.AppendLine("    {");
        if (menu is not null && menu.Entries.Count > 0)
        {
            var toolGroups = CollectMenuToolGroups(menu);
            foreach (var tg in toolGroups)
                sb.AppendLine($"        var toolbarGroup{tg} = GetToolBarGroup({tg});");
            var names = new List<string>();
            for (var i = 0; i < menu.Entries.Count; i++)
            {
                var varName = $"item{i + 1}";
                if (EmitMenuEntryVariable(sb, menu.Entries[i], varName, "        ", allTasks, new List<string>(), skipAppLifecycleEntries: false))
                    names.Add(varName);
            }
            if (names.Count > 0)
                sb.AppendLine($"        Add({string.Join(", ", names)});");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, "ApplicationMdiMenu.cs"), sb.ToString());
    }

    private static void WriteDefaultPulldownMenu(string viewsDir, string appNamespace, string appRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {appNamespace}.Views;");
        sb.AppendLine();
        sb.AppendLine("public class DefaultPulldownMenu : ENV.UI.Menus.ContextMenuStripBase");
        sb.AppendLine("{");
        sb.AppendLine("    public DefaultPulldownMenu(System.ComponentModel.IContainer container) : base(container)");
        sb.AppendLine("    {");
        sb.AppendLine($"        InitBasedOn(\"{Escape(appRoot)}\", \"{appNamespace}.Views.ApplicationMdiMenu\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, "DefaultPulldownMenu.cs"), sb.ToString());
    }

    private static void WriteContextMenuFromXml(MenuDef menu, string className, string viewsDir, string appNamespace, IReadOnlyList<TaskSemantic> allTasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using ENV.UI.Menus;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Views;");
        sb.AppendLine();
        sb.AppendLine($"public class {className} : ContextMenuStripBase");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}(System.ComponentModel.IContainer container) : base(container)");
        sb.AppendLine("    {");
        if (menu.Entries.Count > 0)
        {
            var toolGroups = CollectMenuToolGroups(menu);
            foreach (var tg in toolGroups)
                sb.AppendLine($"        var toolbarGroup{tg} = GetToolBarGroup({tg});");
            var names = new List<string>();
            for (var i = 0; i < menu.Entries.Count; i++)
            {
                var varName = $"item{i + 1}";
                if (EmitMenuEntryVariable(sb, menu.Entries[i], varName, "        ", allTasks, new List<string>(), skipAppLifecycleEntries: true))
                    names.Add(varName);
            }
            if (names.Count > 0)
                sb.AppendLine($"        Add({string.Join(", ", names)});");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, $"{className}.cs"), sb.ToString());
    }

    private static void WriteEmptyContextMenu(string className, string viewsDir, string appNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using ENV.UI.Menus;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace}.Views;");
        sb.AppendLine();
        sb.AppendLine($"public class {className} : ContextMenuStripBase");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}(System.ComponentModel.IContainer container) : base(container)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(viewsDir, $"{className}.cs"), sb.ToString());
    }
}

