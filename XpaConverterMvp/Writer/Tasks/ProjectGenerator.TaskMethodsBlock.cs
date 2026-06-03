using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static string BuildTaskMethodsBlock(TaskSemantic t, IReadOnlyList<DataObjectDef> dataObjects, IReadOnlyList<TaskSemantic> allTasks)
    {
        var className = ResolveTaskClassName(t, allTasks);
        var methodsStopwatch = Stopwatch.StartNew();
        var baseClass = ResolveBaseClass(t);
        var initDataViewMethod = baseClass == "FlowUIControllerBase" ? "InitializeDataViewAndUserFlow" : "InitializeDataView";
        var methods = new StringBuilder();
        TimeSection(() => EmitRunMethod(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-run");
        methods.AppendLine();
        TimeSection(() => EmitInitializeDataView(methods, t, dataObjects, allTasks, initDataViewMethod), "TASK_METHOD", className, "emit-initialize-dataview");
        methods.AppendLine();
        TimeSection(() => EmitInitializeHandlers(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-initialize-handlers");
        methods.AppendLine();
        TimeSection(() => EmitOnLoad(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onload");
        TimeSection(() => EmitOnStart(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onstart");
        TimeSection(() => EmitOnEnterRow(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onenterrow");
        if (baseClass == "BusinessProcessBase")
            TimeSection(() => EmitBusinessProcessOnLeaveRow(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onleaverow");
        else
            TimeSection(() => EmitOnSavingRow(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onsavingrow");
        TimeSection(() => EmitOnEnd(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onend");
        TimeSection(() => EmitOnUnLoad(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-onunload");
        TimeSection(() => EmitFunctionOverrides(methods, t, dataObjects), "TASK_METHOD", className, "emit-function-overrides");
        TimeSection(() => EmitControlHandlerMethods(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-control-handlers");
        TimeSection(() => EmitPrintSectionWrites(methods, t, dataObjects, allTasks), "TASK_METHOD", className, "emit-print-section-writes");
        ConversionTelemetry.LogDuration("TASK_METHOD", className, methodsStopwatch.Elapsed, "section=\"total\"");
        return methods.ToString();
    }
}

