using System;
using System.Collections.Generic;
using System.IO;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static void WriteHandlingGuiCompatAsset(string outputRoot, string appNamespace)
    {
        var lines = new List<string>
        {
            $"global using static {appNamespace}.HandlingGuiCompat;",
            "",
            "using System;",
            "using System.Reflection;",
            "using ENV;",
            "using XPARuntimeCore.Box;",
            "",
            $"namespace {appNamespace};",
            "",
            "internal static class HandlingGuiCompat",
            "{",
            "    public static Bool BrowserScriptExecute(Text controlName, object value, Bool sync)",
            "    {",
            "        return BrowserScriptExecute(controlName, value, sync, null);",
            "    }",
            "",
            "    public static Bool BrowserScriptExecute(Text controlName, object value, Bool sync, Text language)",
            "    {",
            "        try",
            "        {",
            "            var userMethods = UserMethods.Instance;",
            "            var getTask = typeof(UserMethods).GetMethod(\"GetTaskByGeneration\", BindingFlags.Instance | BindingFlags.NonPublic);",
            "            var task = getTask?.Invoke(userMethods, new object[] { 0 });",
            "            if (task == null)",
            "                return false;",
            "            var view = task.GetType().GetProperty(\"View\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(task);",
            "            if (view is not ENV.UI.Form form)",
            "                return false;",
            "            var findControlByTag = typeof(ENV.UI.Form).GetMethod(\"FindControlByTag\", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);",
            "            var control = findControlByTag?.Invoke(form, new object[] { (controlName ?? \"\").Trim() });",
            "            if (control == null)",
            "                return false;",
            "            var script = (value ?? \"\").ToString() ?? \"\";",
            "            var scriptLanguage = string.IsNullOrWhiteSpace((language ?? \"\").ToString()) ? \"JScript\" : (language ?? \"\").ToString();",
            "            Context.Current.InvokeUICommand(() =>",
            "            {",
            "                var controlType = control.GetType();",
            "                var document = controlType.GetProperty(\"Document\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control);",
            "                var window = document?.GetType().GetProperty(\"Window\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(document);",
            "                var domWindow = window?.GetType().GetProperty(\"DomWindow\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window);",
            "                var execScript = domWindow?.GetType().GetMethod(\"execScript\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);",
            "                if (execScript != null)",
            "                {",
            "                    var parameters = execScript.GetParameters().Length >= 2",
            "                        ? new object[] { script, scriptLanguage }",
            "                        : new object[] { script };",
            "                    execScript.Invoke(domWindow, parameters);",
            "                    return;",
            "                }",
            "                document?.GetType().GetMethod(\"InvokeScript\", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(document, new object[] { \"eval\", new object[] { script } });",
            "            });",
            "            return true;",
            "        }",
            "        catch",
            "        {",
            "            return false;",
            "        }",
            "    }",
            "}"
        };

        File.WriteAllText(Path.Combine(outputRoot, "HandlingGuiCompat.cs"), string.Join(Environment.NewLine, lines));
    }
}

