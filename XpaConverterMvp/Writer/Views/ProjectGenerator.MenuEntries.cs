using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static IReadOnlyList<int> CollectMenuToolGroups(MenuDef menu)
    {
        var set = new SortedSet<int>();
        void Walk(MenuEntryDef e)
        {
            if (e.ToolGroup.HasValue)
                set.Add(e.ToolGroup.Value);
            foreach (var c in e.Children)
                Walk(c);
        }
        foreach (var e in menu.Entries)
            Walk(e);
        return set.ToList();
    }

    private static bool EmitMenuEntryVariable(
        StringBuilder sb,
        MenuEntryDef entry,
        string varName,
        string indent,
        IReadOnlyList<TaskSemantic> allTasks,
        List<string> parentPath,
        bool skipAppLifecycleEntries)
    {
        var text = Escape(entry.Description ?? "Menu Item");
        if (entry.MenuType == "5")
        {
            sb.AppendLine($"{indent}var {varName} = new Separator();");
            return true;
        }

        if (entry.Children.Count > 0)
        {
            sb.AppendLine($"{indent}var {varName} = new MenuEntry(\"{text}\");");
            var parentLabel = entry.Description ?? "Menu Item";
            parentPath.Add(parentLabel);
            var hasChild = false;
            var prevWasSeparator = false;
            for (var i = 0; i < entry.Children.Count; i++)
            {
                if (entry.Children[i].MenuType == "5" && (!hasChild || prevWasSeparator))
                    continue;
                var childVar = $"{varName}_{i + 1}";
                if (EmitMenuEntryVariable(sb, entry.Children[i], childVar, indent, allTasks, parentPath, skipAppLifecycleEntries))
                {
                    sb.AppendLine($"{indent}{varName}.Add({childVar});");
                    hasChild = true;
                    prevWasSeparator = entry.Children[i].MenuType == "5";
                }
            }
            if (parentLabel.Equals("&Options", StringComparison.OrdinalIgnoreCase) || parentLabel.Equals("Options", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{indent}{varName}.Add(new DeveloperToolsMenu(Application.Instance, typeof(Shared.DataSources), Roles.UserManager));");
                sb.AppendLine($"{indent}{varName}.Add(new ManagedCommand(\"Clear Value\", ENV.Commands.ClearCurrentValueInTemplate));");
                sb.AppendLine($"{indent}{varName}.Add(new ManagedCommand(\"Clear Template\", ENV.Commands.ClearTemplate));");
                sb.AppendLine($"{indent}{varName}.Add(new ManagedCommand(\"From Values\", ENV.Commands.TemplateFromValues));");
                sb.AppendLine($"{indent}{varName}.Add(new ManagedCommand(\"To Value\", ENV.Commands.TemplateToValues));");
                sb.AppendLine($"{indent}{varName}.Add(new ManagedCommand(\"Define Expression\", ENV.Commands.TemplateExpression));");
            }
            parentPath.RemoveAt(parentPath.Count - 1);
            return true;
        }

        if (skipAppLifecycleEntries && entry.Event?.InternalEventId is int skipId && (skipId == 23 || skipId == 24 || skipId == 25 || skipId == 27))
            return false;

        var rawText = entry.Description?.Trim();
        if (string.IsNullOrWhiteSpace(rawText) && parentPath.Any(p => p.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            sb.AppendLine($"{indent}var {varName} = new WindowsList();");
            return true;
        }

        if (TryResolveProgramMenuCommand(entry, parentPath, allTasks, out var programExpr))
        {
            sb.AppendLine($"{indent}var {varName} = {programExpr};");
            return true;
        }

        var mapped = ResolveInternalMenuCommand(entry);
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            sb.AppendLine($"{indent}var {varName} = {ApplyMenuEntryProperties(mapped!, entry, entry.Event?.InternalEventId, skipAppLifecycleEntries)};");
            return true;
        }

        if (entry.Event?.InternalEventId is int eventId)
        {
            sb.AppendLine($"{indent}var {varName} = {ApplyMenuEntryProperties($"new MenuEntry(\"{text}\", () => {{ /* InternalEventID={eventId} */ }})", entry, eventId, skipAppLifecycleEntries)};");
            return true;
        }
        sb.AppendLine($"{indent}var {varName} = {ApplyMenuEntryProperties($"new MenuEntry(\"{text}\")", entry, null, skipAppLifecycleEntries)};");
        return true;
    }

    private static bool TryResolveProgramMenuCommand(MenuEntryDef entry, IReadOnlyList<string> parentPath, IReadOnlyList<TaskSemantic> allTasks, out string expr)
    {
        expr = "";
        if (entry.Event is not null)
            return false;
        TaskSemantic? task = null;
        var label = entry.Description?.Trim();
        var programDescription = entry.ProgramDescription?.Trim();

        if (!string.IsNullOrWhiteSpace(programDescription))
        {
            var normalized = NormalizeTaskName(programDescription);
            task = allTasks
                .Where(t => !t.MainProgram && ShouldGenerateForTarget(t))
                .FirstOrDefault(t => string.Equals(NormalizeTaskName(t.Description), normalized, StringComparison.OrdinalIgnoreCase));
        }

        if (task is null && !string.IsNullOrWhiteSpace(label))
        {
            var normalized = NormalizeTaskName(label);
            task = allTasks
                .Where(t => !t.MainProgram && ShouldGenerateForTarget(t))
                .FirstOrDefault(t => string.Equals(NormalizeTaskName(t.Description), normalized, StringComparison.OrdinalIgnoreCase));
        }

        if (task is null)
        {
            if (entry.ProgramObj is int programObj)
            {
                task = allTasks.FirstOrDefault(t =>
                    t.ParentOrdinal is null &&
                    !t.MainProgram &&
                    ShouldGenerateForTarget(t) &&
                    t.TopLevelProgramIndexLocal == programObj);
            }
        }

        if (task is null)
        {
            if (!parentPath.Any(p => p.Equals("Programs", StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(label))
                return false;

            var normalized = NormalizeTaskName(label);
            task = allTasks
                .Where(t => t.ParentOrdinal is null && !t.MainProgram && ShouldGenerateForTarget(t))
                .FirstOrDefault(t => NormalizeTaskName(t.Description).Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }

        if (task is null)
            return false;

        var className = ResolveTaskClassName(task, _allTasks ?? Array.Empty<TaskSemantic>());
        expr = $"new MenuEntry(\"{Escape(label ?? task.Description)}\", () => new {className}().Run()) {{ CloseActiveControllers = true }}";
        return true;
    }

    private static string ApplyMenuEntryProperties(string baseExpr, MenuEntryDef entry, int? internalEventId, bool isSecondaryPulldownOrContext)
    {
        var inits = new List<string>();
        if (entry.ToolGroup.HasValue)
            inits.Add($"ToolBarGroup = toolbarGroup{entry.ToolGroup.Value}");
        var image = ResolveMenuImageResource(internalEventId, isSecondaryPulldownOrContext);
        if (!string.IsNullOrWhiteSpace(image))
            inits.Add($"Image = Properties.Resources.{image}");
        if (inits.Count == 0)
            return baseExpr;
        return $"{baseExpr} {{ {string.Join(", ", inits)} }}";
    }
}

