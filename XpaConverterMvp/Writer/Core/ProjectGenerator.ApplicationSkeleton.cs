using System;
using System.IO;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteApplicationSkeleton(ProjectSemantic parsed, string outputRoot, string appNamespace)
    {
        var tasks = parsed.Tasks.Where(ShouldGenerateForTarget).ToList();
        var main = tasks.FirstOrDefault(t => t.MainProgram) ?? tasks.FirstOrDefault();
        if (main is null)
            return;

        var appName = string.IsNullOrWhiteSpace(appNamespace) ? "Application" : appNamespace.Split('.').First();
        var hasReferencedModules = string.IsNullOrWhiteSpace(_targetComponent) && parsed.Components.Count > 0;
        var hasContextMenu = parsed.ProjectMenuSettings.SystemContextMenuObj.HasValue || parsed.Menus.Any(IsContextMenu);
        var sb = new StringBuilder();
        sb.AppendLine("using ENV;");
        sb.AppendLine("using ENV.Data;");
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine("using XPARuntimeCore.Box.Advanced;");
        sb.AppendLine("using System.Windows.Forms;");
        sb.AppendLine("using Message = ENV.Message;");
        sb.AppendLine();
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{Escape(main.Description)} (MVP generated)</summary>");
        sb.AppendLine("public class Application : ApplicationControllerBase");
        sb.AppendLine("{");
        if (main.ResourcesSemantic.Ordered.Count > 0)
        {
            sb.AppendLine("    #region Columns");
            foreach (var c in main.ResourcesSemantic.Ordered)
            {
                var member = ResolveTaskResourceMemberName(main, c);
                var declaration = BuildTaskResourceColumnDeclaration(c, parsed.FieldModels, main, member);
                sb.AppendLine($"    internal readonly {declaration};");
            }
            sb.AppendLine("    #endregion");
        }
        if (parsed.Menus.Count > 0)
            sb.AppendLine("    Views.ApplicationMdi _mdi;");
        sb.AppendLine("    public Application()");
        sb.AppendLine("    {");
        if (hasContextMenu)
            sb.AppendLine("        SetDefaultContextMenu(typeof(Views.DefaultContextMenu));");
        if (hasReferencedModules)
            sb.AppendLine("        LoadReferencedModules();");
        sb.AppendLine("        _applicationRoles = new ENV.Security.RolesCollection(typeof(Roles));");
        sb.AppendLine($"        base.Title = \"{Escape(main.Description)}\";");
        sb.AppendLine($"        Name = \"{Escape(appName)}\";");
        sb.AppendLine("        InitializeDataView();");
        sb.AppendLine("        InitializeHandlers();");
        sb.AppendLine("        _applicationPrograms = _staticPrograms;");
        sb.AppendLine("        _applicationEntities = _staticEntities;");
        sb.AppendLine("    }");
        sb.AppendLine();
        EmitInitializeDataView(sb, main, parsed.DataObjects, tasks, "InitializeDataView");
        sb.AppendLine();
        EmitInitializeHandlers(sb, main, parsed.DataObjects, tasks);
        sb.AppendLine();
        sb.AppendLine("    protected override ProgramCollection LoadAllProgramsCollection()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_staticPrograms == null)");
        sb.AppendLine("            _staticPrograms = new ApplicationProgramsMvp();");
        sb.AppendLine("        return _staticPrograms;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    protected override ApplicationEntityCollection LoadAllEntitiesCollection()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_staticEntities == null)");
        sb.AppendLine("            _staticEntities = new ApplicationEntitiesMvp();");
        sb.AppendLine("        return _staticEntities;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    protected override void OnLoad()");
        sb.AppendLine("    {");
        if (main.ErrorStrategy == "R")
            sb.AppendLine("        OnDatabaseErrorRetry = true;");
        var appViewClass = ResolveApplicationViewClassName(main, appNamespace);
        sb.AppendLine($"        View = () => new Views.{appViewClass}(this);");
        sb.AppendLine("    }");
        sb.AppendLine();
        if (parsed.Menus.Count > 0)
        {
            sb.AppendLine("    protected override void Execute()");
            sb.AppendLine("    {");
            sb.AppendLine("        ENV.Security.UserManager.Load();");
            sb.AppendLine("        if (!ENV.Security.UserManager.ShowLoginDialog(false))");
            sb.AppendLine("            return;");
            sb.AppendLine("        #if DEBUG");
            sb.AppendLine("        Common.EnableDeveloperTools = Roles.DeveloperTools.Allowed;");
            sb.AppendLine("        #endif");
            sb.AppendLine("        ;");
            sb.AppendLine("        Common.BindStatusBar(_mdi.mainStatusLabel, _mdi.userStatusLabel, _mdi.activityStatusLabel, _mdi.expandStatusLabel, _mdi.expandTextBoxStatusLabel, _mdi.insertOverrideStatusLabel, _mdi.versionStatusLabel);");
            sb.AppendLine("        base.Execute();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        if (hasReferencedModules)
        {
            sb.AppendLine("    void LoadReferencedModules()");
            sb.AppendLine("    {");
            foreach (var component in parsed.Components.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var ns = ResolveNamespaceForComponent(component);
                if (string.IsNullOrWhiteSpace(ns))
                    continue;
                sb.AppendLine($"        AddReference({ns}.Application.Instance, true);");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        EmitOnStart(sb, main, parsed.DataObjects, tasks);
        EmitOnEnd(sb, main, parsed.DataObjects, tasks);
        EmitFunctionOverrides(sb, main, parsed.DataObjects);
        sb.AppendLine();
        sb.AppendLine("    public static void Run()");
        sb.AppendLine("    {");
        if (parsed.Menus.Count > 0)
        {
            sb.AppendLine("        Instance._mdi = new Views.ApplicationMdi();");
            sb.AppendLine("        Instance.Run(Instance._mdi, () => Instance);");
        }
        else
        {
            sb.AppendLine("        Instance.Run(() => Instance);");
        }
        sb.AppendLine("        Context.Current[typeof(Application)] = null;");
        sb.AppendLine("    }");

        if (parsed.Menus.Count > 0)
        {
            var menus = parsed.Menus.OrderBy(m => m.Obj).ToList();
            var systemPulldownObj = parsed.ProjectMenuSettings.SystemPulldownMenuObj
                                    ?? menus.FirstOrDefault(IsPulldownMenu)?.Obj;
            var menuName = "Default Pulldown menu";
            if (systemPulldownObj.HasValue)
            {
                var sys = menus.FirstOrDefault(m => m.Obj == systemPulldownObj.Value);
                if (sys is not null && !string.IsNullOrWhiteSpace(sys.Name))
                    menuName = sys.Name;
            }
            sb.AppendLine();
            sb.AppendLine("    protected override string[] GetMenusNames()");
            sb.AppendLine("    {");
            sb.AppendLine($"        return new []{{\"{Escape(menuName)}\"}};");
            sb.AppendLine("    }");
        }
        if (main.EventsSemantic.Items.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    #region CustomCommands");
            foreach (var ev in main.EventsSemantic.Items)
            {
                var cmdExpr = ev.InternalEventId.HasValue ? ResolveCommandByInternalEventId(ev.InternalEventId.Value) : null;
                var keyExpr = ev.EventKeyCombinationId.HasValue ? ResolveKeyCombination(ev.EventKeyCombinationId.Value) : "";
                var name = ResolveTaskCommandIdentifier(main, ev.Description, preserveCase: true);
                if (!string.IsNullOrWhiteSpace(cmdExpr) || !string.IsNullOrWhiteSpace(keyExpr))
                {
                    sb.AppendLine($"    internal static readonly CustomCommand {name} = {BuildCustomCommandExpression(ev.Description, cmdExpr, keyExpr, ev.ForceExit, ev.EventType)};");
                }
                else if (!string.IsNullOrWhiteSpace(ev.PublicName))
                {
                    var pre = ev.ForceExit switch
                    {
                        "C" => "Precondition = CustomCommandPrecondition.LeaveControl, CancelTrigger = true, ",
                        "P" => "Precondition = CustomCommandPrecondition.LeaveRow, CancelTrigger = true, ",
                        "E" => "Precondition = CustomCommandPrecondition.SaveControlDataToColumn, ",
                        _ => ""
                    };
                    sb.AppendLine($"    internal static readonly CustomCommand {name} = new CustomCommand(\"{Escape(ev.Description)}\") {{ {pre}Key = \"{Escape(ev.PublicName)}\", AllowInvokeByKey = CustomCommandAllowInvokeByKey.FromSameModuleOnly }};");
                }
                else
                {
                    sb.AppendLine($"    internal static readonly CustomCommand {name} = {BuildCustomCommandExpression(ev.Description, null, null, ev.ForceExit, ev.EventType)};");
                }

                if (ev.Parameters.Count > 0)
                {
                    var signature = string.Join(", ", ev.Parameters.Select(BuildEventParameterSignature));
                    var args = string.Join(", ", ev.Parameters.Select(p => ToParameterIdentifier(p.Name)));
                    sb.AppendLine($"    public static CommandWithArgs {name}WithArgs({signature}) => new CommandWithArgs({name}, {args});");
                }
            }
            sb.AppendLine("    #endregion");
        }
        sb.AppendLine();
        sb.AppendLine("    static Application()");
        sb.AppendLine("    {");
        sb.AppendLine("        AsyncHelperBase.ApplicationClassType = typeof(Application);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static Application Instance");
        sb.AppendLine("    {");
        sb.AppendLine("        get");
        sb.AppendLine("        {");
        sb.AppendLine("            var result = Context.Current[typeof(Application)] as Application;");
        sb.AppendLine("            if (result == null)");
        sb.AppendLine("            {");
        sb.AppendLine("                result = new Application();");
        sb.AppendLine("                Context.Current[typeof(Application)] = result;");
        sb.AppendLine("            }");
        sb.AppendLine("            return result;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    static ApplicationProgramsMvp _staticPrograms;");
        sb.AppendLine("    static ApplicationEntitiesMvp _staticEntities;");

        var children = tasks
            .Where(x => x.ParentOrdinal == main.Ordinal)
            .OrderBy(x => x.SubtaskIndex ?? int.MaxValue)
            .ToList();
        foreach (var child in children)
        {
            sb.AppendLine();
            sb.Append(IndentLines(BuildTaskClassBlock(child, parsed.DataObjects, parsed.FieldModels, tasks), 1));
        }
        sb.AppendLine("}");

        WriteGeneratedSourceFile(outputRoot, "Application", NormalizeGeneratedTaskCode(sb.ToString(), main, parsed.DataObjects));
        WriteTaskSnippetFiles(main, tasks, outputRoot);
    }
}

