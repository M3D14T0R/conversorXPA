using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteMdiAndMenus(ProjectSemantic parsed, string viewsDir, string appNamespace)
    {
        var menus = parsed.Menus.OrderBy(m => m.Obj).ToList();
        var appRoot = appNamespace.Split('.').FirstOrDefault() ?? appNamespace;
        var systemPulldownObj = parsed.ProjectMenuSettings.SystemPulldownMenuObj
                                ?? menus.FirstOrDefault(IsPulldownMenu)?.Obj;
        var systemContextObj = parsed.ProjectMenuSettings.SystemContextMenuObj
                               ?? menus.FirstOrDefault(IsContextMenu)?.Obj;

        var systemPulldown = systemPulldownObj.HasValue ? menus.FirstOrDefault(m => m.Obj == systemPulldownObj.Value) : null;
        var systemContext = systemContextObj.HasValue ? menus.FirstOrDefault(m => m.Obj == systemContextObj.Value) : null;
        var extraPulldowns = menus.Where(m => IsPulldownMenu(m) && m.Obj != systemPulldownObj).ToList();

        var menuTasks = parsed.Tasks.Where(ShouldGenerateForTarget).ToList();

        WriteApplicationMdiMenu(systemPulldown, viewsDir, appNamespace, menuTasks);
        WriteDefaultPulldownMenu(viewsDir, appNamespace, appRoot);
        for (var i = 0; i < extraPulldowns.Count; i++)
        {
            var className = "DefaultPulldownMenu" + new string('_', i + 1);
            WriteContextMenuFromXml(extraPulldowns[i], className, viewsDir, appNamespace, menuTasks);
        }

        if (systemContext is not null)
            WriteContextMenuFromXml(systemContext, "DefaultContextMenu", viewsDir, appNamespace, menuTasks);
        else
            WriteEmptyContextMenu("DefaultContextMenu", viewsDir, appNamespace);

        WriteContextMenuMap(systemPulldownObj, systemContextObj, extraPulldowns, viewsDir, appNamespace);
        WriteApplicationMdiFiles(viewsDir, appNamespace, appRoot);
    }

    private static bool IsPulldownMenu(MenuDef m)
    {
        return string.Equals(m.MenuType, "4", StringComparison.OrdinalIgnoreCase)
               || m.Name.Contains("pulldown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContextMenu(MenuDef m)
    {
        return m.Name.Contains("context", StringComparison.OrdinalIgnoreCase)
               || (!IsPulldownMenu(m) && m.Entries.Count == 0);
    }
}

