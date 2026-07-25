using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string BuildTaskClassBlock(
        TaskSemantic t,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<FieldModelDef> fieldModels,
        IReadOnlyList<TaskSemantic> allTasks)
    {
        var className = ResolveTaskClassName(t, allTasks);
        var baseClass = ResolveBaseClass(t);
        var initDataViewMethod = baseClass == "FlowUIControllerBase" ? "InitializeDataViewAndUserFlow" : "InitializeDataView";
        var buildStopwatch = Stopwatch.StartNew();
        var methods = TimeSection(() => new StringBuilder(BuildTaskMethodsBlock(t, dataObjects, allTasks)), "TASK_BUILD", className, "methods");
        var subforms = TimeSection(() => BuildSubformBindingsFromSemantic(t, allTasks, dataObjects), "TASK_BUILD", className, "subforms");
        if (subforms.Count > 0)
            TimeSection(() => EmitSubformMethods(methods, t, subforms, allTasks), "TASK_BUILD", className, "emit-subform-methods");
        var cachedSubforms = subforms
            .Where(s => s.Kind == ViewSubformBindingKind.Task && s.TargetTask is not null)
            .ToList();
        TimeSection(() => EmitExpressionMethodsForViewBindings(methods, t, dataObjects), "TASK_BUILD", className, "emit-view-expression-methods");

        var parentTask = GetTaskByOrdinal(t.ParentOrdinal, allTasks);
        var needsParentRef = t.ParentOrdinal.HasValue
                             && TaskNeedsParentReference(t, allTasks, dataObjects);
        var exposeTopLevelType = (!string.IsNullOrWhiteSpace(_targetComponent)
                                  || !string.IsNullOrWhiteSpace(t.SourceComponent)
                                  || string.Equals(_outputType, "Library", StringComparison.OrdinalIgnoreCase))
                                 && !t.ParentOrdinal.HasValue;
        var classAccessibility = exposeTopLevelType
            ? "public"
            : "internal";

        var sb = new StringBuilder();
        sb.AppendLine($"/// <summary>{Escape(t.Description)} (MVP generated)</summary>");
        sb.AppendLine($"{classAccessibility} class {className} : {baseClass}");
        sb.AppendLine("{");
        TimeSection(() => EmitTaskMembers(sb, t, dataObjects, fieldModels), "TASK_BUILD", className, "emit-members");
        if (cachedSubforms.Count > 0)
        {
            sb.AppendLine("    #region Initialize CachedControllers");
            foreach (var subform in cachedSubforms)
            {
                var targetClass = ResolveTaskClassName(subform.TargetTask!, allTasks);
                sb.AppendLine($"    internal {targetClass} {subform.FieldName};");
            }
            sb.AppendLine("    #endregion");
            sb.AppendLine();
        }
        if (needsParentRef)
        {
            var parentClass = parentTask is null ? "object" : parentTask.MainProgram ? "Application" : ResolveTaskTypeReference(parentTask, allTasks);
            sb.AppendLine($"    internal readonly {parentClass} _parent;");
            sb.AppendLine();
        }
        var ctorSignature = $"    public {className}()";
        if (needsParentRef)
        {
            var parentClass = parentTask is null ? "object" : parentTask.MainProgram ? "Application" : ResolveTaskTypeReference(parentTask, allTasks);
            ctorSignature = $"    public {className}({parentClass} parent)";
        }
        sb.AppendLine(ctorSignature);
        sb.AppendLine("    {");
        if (needsParentRef)
            sb.AppendLine("        _parent = parent;");
        if (cachedSubforms.Count > 0)
        {
            foreach (var subform in cachedSubforms)
            {
                var targetClass = ResolveTaskClassName(subform.TargetTask!, allTasks);
                var targetCtor = subform.TargetNeedsParentCtor ? $"new {targetClass}(this)" : $"new {targetClass}()";
                sb.AppendLine($"        {subform.FieldName} = {targetCtor};");
            }
        }
        sb.AppendLine($"        base.Title = \"{Escape(t.Description)}\";");
        if (!string.IsNullOrWhiteSpace(t.TaskId))
            sb.AppendLine($"        TaskID = \"{Escape(t.TaskId!)}\";");
        sb.AppendLine($"        {initDataViewMethod}();");
        if (HasPrintGroupIo(t))
            sb.AppendLine("        InitializeGroups();");
        sb.AppendLine("        InitializeHandlers();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append(methods.ToString());

        sb.AppendLine("}");
        var normalizedLocal = TimeSection(() => sb.ToString(), "TASK_BUILD", className, "normalize");

        var children = TimeSection(() => GetChildTasks(t.Ordinal, allTasks), "TASK_BUILD", className, "resolve-children");
        if (children.Count == 0)
        {
            ConversionTelemetry.LogDuration("TASK_BUILD", className, buildStopwatch.Elapsed, "section=\"total\"");
            return normalizedLocal;
        }

        sb.Clear();
        sb.Append(RemoveFinalClassClosingBrace(normalizedLocal));
        foreach (var child in children)
        {
            sb.AppendLine();
            sb.Append(IndentLines(BuildTaskClassBlock(child, dataObjects, fieldModels, allTasks), 1));
        }

        sb.AppendLine("}");
        ConversionTelemetry.LogDuration("TASK_BUILD", className, buildStopwatch.Elapsed, "section=\"total\"");
        return sb.ToString();
    }

    private static string RemoveFinalClassClosingBrace(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var idx = code.Length - 1;
        while (idx >= 0 && char.IsWhiteSpace(code[idx]))
            idx--;

        if (idx < 0 || code[idx] != '}')
            return code.TrimEnd();

        return code[..idx].TrimEnd();
    }
}

