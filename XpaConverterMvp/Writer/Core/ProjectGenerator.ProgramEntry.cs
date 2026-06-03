using System.IO;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteProgramEntry(string outputRoot, string appNamespace)
    {
        var appName = string.IsNullOrWhiteSpace(appNamespace) ? "Application" : appNamespace.Split('.').First();
        var sb = new StringBuilder();
        sb.AppendLine("using XPARuntimeCore.Box;");
        sb.AppendLine($"namespace {appNamespace};");
        sb.AppendLine();
        sb.AppendLine("public class Program");
        sb.AppendLine("{");
        sb.AppendLine("    [System.STAThread]");
        sb.AppendLine("    public static void Main(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            Init(args);");
        sb.AppendLine("            Application.Run();");
        sb.AppendLine("            ENV.UserSettings.FinalizeINI();");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (System.Exception e)");
        sb.AppendLine("        {");
        sb.AppendLine("            ENV.ErrorLog.WriteToLogFile(e, \"TOTAL CRASH\");");
        sb.AppendLine("            System.Environment.ExitCode = 1;");
        sb.AppendLine("            ENV.Common.ShowExceptionDialog(e);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static void Init(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        System.Windows.Forms.Application.EnableVisualStyles();");
        sb.AppendLine("        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);");
        sb.AppendLine("        ENV.Data.TextColumn.DefaultUseBackslashToDistinguishWildcards = true;");
        sb.AppendLine("        ENV.Data.DataProvider.XmlEntityDataProvider.SaveAnsiAsUTF8 = true;");
        sb.AppendLine("        ENV.UserSettings.Version10Compatible = true;");
        sb.AppendLine("        ENV.UserSettings.VersionXpaCompatible = true;");
        sb.AppendLine("        ENV.MenuManager.AllowEnabledHiddenMenuItems = true;");
        sb.AppendLine("        ENV.AbstractUIController.SuppressInputValidationOfFocusedControlWhenClosingByClickOnParentAndRowHasNotChanged = true;");
        sb.AppendLine("        ENV.BackwardCompatible.NullBehaviour.EqualToNullReturnsNull = false;");
        sb.AppendLine("        ENV.Data.DateColumn.GlobalDefault = new Date(1901,1,1);");
        sb.AppendLine("        ENV.Commands.SetDefaultKeyboardMapping();");
        sb.AppendLine("        ENV.Commands.SetVersion10CompatibleKeyMapping();");
        sb.AppendLine($"        ENV.Common.ApplicationTitle = \"{Escape(appName)}\";");
        sb.AppendLine("        ENV.AbstractUIController.UndoChangesInRowRevertsControllerState = true;");
        sb.AppendLine("        ENV.Data.DataProvider.SQLiteEntityDataProvider.AddToConnectionManager();");
        sb.AppendLine($"        ENV.UserSettings.InitUserSettings(\"{Escape(appName)}.ini\", args);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        WriteGeneratedSourceFile(outputRoot, "Program", sb.ToString());
    }

}

