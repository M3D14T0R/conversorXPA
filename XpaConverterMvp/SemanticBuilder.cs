using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XpaConverterMvp;

internal static class SemanticBuilder
{
    private static IReadOnlyList<TaskDef> _taskDefs = Array.Empty<TaskDef>();
    private static IReadOnlyDictionary<int, TaskDef> _taskDefsByOrdinal = new Dictionary<int, TaskDef>();
    private static IReadOnlyDictionary<int, IReadOnlyList<TaskDef>> _childTaskDefsByParentOrdinal = new Dictionary<int, IReadOnlyList<TaskDef>>();
    private static IReadOnlyDictionary<(int ParentOrdinal, int SubtaskIndex), TaskDef> _childTaskDefByParentAndSubtaskIndex = new Dictionary<(int ParentOrdinal, int SubtaskIndex), TaskDef>();
    private static IReadOnlyDictionary<int, TaskDef> _topLevelTaskDefsByProgramIndex = new Dictionary<int, TaskDef>();
    private static IReadOnlyList<TaskDef> _topLevelTaskDefsByOrdinal = Array.Empty<TaskDef>();
    private static IReadOnlyDictionary<int, DataObjectDef> _dataObjectsByOrdinal = new Dictionary<int, DataObjectDef>();
    private static IReadOnlyDictionary<int, bool> _shouldGenerateViewByTaskOrdinal = new Dictionary<int, bool>();
    private static IReadOnlyDictionary<int, string> _viewClassBaseNameByTaskOrdinal = new Dictionary<int, string>();
    private static IReadOnlyDictionary<int, int> _viewClassDuplicateIndexByTaskOrdinal = new Dictionary<int, int>();
    private static TaskDef? _applicationTask;

    public static void ReleaseBuildState()
    {
        _taskDefs = Array.Empty<TaskDef>();
        _taskDefsByOrdinal = new Dictionary<int, TaskDef>();
        _childTaskDefsByParentOrdinal = new Dictionary<int, IReadOnlyList<TaskDef>>();
        _childTaskDefByParentAndSubtaskIndex = new Dictionary<(int ParentOrdinal, int SubtaskIndex), TaskDef>();
        _topLevelTaskDefsByProgramIndex = new Dictionary<int, TaskDef>();
        _topLevelTaskDefsByOrdinal = Array.Empty<TaskDef>();
        _dataObjectsByOrdinal = new Dictionary<int, DataObjectDef>();
        _shouldGenerateViewByTaskOrdinal = new Dictionary<int, bool>();
        _viewClassBaseNameByTaskOrdinal = new Dictionary<int, string>();
        _viewClassDuplicateIndexByTaskOrdinal = new Dictionary<int, int>();
        _applicationTask = null;
    }

    public static ProjectSemantic Build(ParsedXpa parsed)
    {
        _taskDefs = parsed.Tasks;
        BuildTaskIndexes(parsed.Tasks);
        _dataObjectsByOrdinal = parsed.DataObjects
            .GroupBy(d => d.Ordinal)
            .ToDictionary(g => g.Key, g => g.First());
        BuildViewIndexes(parsed.Tasks);
        ConversionTelemetry.Log(
            "SEMANTIC",
            $"build input tasks={parsed.Tasks.Count} dataObjects={parsed.DataObjects.Count} fieldModels={parsed.FieldModels.Count} components={parsed.Components.Count}");
        var semantic = new ProjectSemantic
        {
            ProjectMenuSettings = parsed.ProjectMenuSettings,
            ApplicationViewClassName = ResolveApplicationViewClassName(parsed.Tasks.FirstOrDefault(t => t.MainProgram) ?? parsed.Tasks.FirstOrDefault()),
            HasRepositoryProperties = parsed.HasRepositoryProperties,
            HasDataRepositoryProperties = parsed.HasDataRepositoryProperties,
            HasHelpRepository = parsed.HasHelpRepository,
            HasXsdUnderViewCompound = parsed.HasXsdUnderViewCompound
        };

        semantic.FieldModels.AddRange(parsed.FieldModels);
        semantic.ControlButtonModels.AddRange(parsed.ControlButtonModels);
        semantic.DataObjects.AddRange(parsed.DataObjects);
        foreach (var dataObject in parsed.DataObjects)
            semantic.DataSourceTypeByObjectOrdinal[dataObject.Ordinal] = $"typeof(Models.{ResolveDataObjectTypeName(dataObject, parsed.DataObjects)})";
        semantic.Rights.AddRange(parsed.Rights);
        semantic.ComponentRightRefs.AddRange(parsed.ComponentRightRefs);
        semantic.Components.AddRange(parsed.Components);
        semantic.ExternalMagicComponents.AddRange(parsed.ExternalMagicComponents);
        semantic.DotNetComponentReferences.AddRange(parsed.DotNetComponentReferences);
        semantic.ComponentFunctions.AddRange(parsed.ComponentFunctions);
        semantic.Menus.AddRange(parsed.Menus);
        for (var i = 0; i < parsed.Tasks.Count; i++)
        {
            var task = parsed.Tasks[i];
            if (i == 0 || (i + 1) % 100 == 0 || i == parsed.Tasks.Count - 1)
            {
                ConversionTelemetry.Log(
                    "SEMANTIC",
                    $"task semantic {i + 1}/{parsed.Tasks.Count} ordinal={task.Ordinal} parent={task.ParentOrdinal?.ToString(CultureInfo.InvariantCulture) ?? ""} description={QuoteTelemetry(task.Description)}");
            }

            semantic.Tasks.Add(BuildTaskSemantic(task, parsed.Tasks, parsed.DataObjects, parsed.ControlButtonModels));
        }
        ConversionTelemetry.Log("SEMANTIC", $"task semantic done count={semantic.Tasks.Count}");
        foreach (var task in parsed.Tasks)
        {
            if (task.Form?.Controls is null)
                continue;
            foreach (var control in task.Form.Controls.Where(c => c.Model == "CTRL_GUI0_PUSH_BUTTON" && c.ModelRefObj.HasValue))
                semantic.UsedButtonModelObjectIds.Add(control.ModelRefObj!.Value);
        }
        ConversionTelemetry.Log("SEMANTIC", "application select map start");
        foreach (var kv in BuildApplicationSelectMap(semantic.Tasks))
            semantic.ApplicationSelectMap[kv.Key] = kv.Value;
        ConversionTelemetry.Log("SEMANTIC", $"application select map done count={semantic.ApplicationSelectMap.Count}");
        ConversionTelemetry.Log("SEMANTIC", "parent select maps start");
        var semanticTasksByOrdinal = semantic.Tasks
            .GroupBy(t => t.Ordinal)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var task in semantic.Tasks)
            semantic.ParentSelectMapByTaskOrdinal[task.Ordinal] = BuildParentSelectMap(task, semanticTasksByOrdinal, semantic.ApplicationSelectMap);
        ConversionTelemetry.Log("SEMANTIC", $"parent select maps done count={semantic.ParentSelectMapByTaskOrdinal.Count}");
        foreach (var rr in parsed.ComponentRightRefs)
            semantic.ComponentRightsByLiteral[$"{rr.ComponentId},{rr.RightId}"] = new ComponentRightSemantic(rr.ComponentName, ToRoleMemberIdentifier(rr.RightName));
        ConversionTelemetry.Log("SEMANTIC", "build done");
        return semantic;
    }

    private static void BuildTaskIndexes(IReadOnlyList<TaskDef> tasks)
    {
        _taskDefsByOrdinal = tasks
            .GroupBy(t => t.Ordinal)
            .ToDictionary(g => g.Key, g => g.First());
        _childTaskDefsByParentOrdinal = tasks
            .Where(t => t.ParentOrdinal.HasValue)
            .GroupBy(t => t.ParentOrdinal!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskDef>)g
                    .OrderBy(t => t.SubtaskIndex ?? int.MaxValue)
                    .ThenBy(t => t.Ordinal)
                    .ToList());
        _childTaskDefByParentAndSubtaskIndex = tasks
            .Where(t => t.ParentOrdinal.HasValue && t.SubtaskIndex.HasValue)
            .GroupBy(t => (t.ParentOrdinal!.Value, t.SubtaskIndex!.Value))
            .ToDictionary(g => g.Key, g => g.First());
        _topLevelTaskDefsByOrdinal = tasks
            .Where(t => t.ParentOrdinal is null)
            .OrderBy(t => t.Ordinal)
            .ToList();
        _topLevelTaskDefsByProgramIndex = _topLevelTaskDefsByOrdinal
            .Where(t => t.TopLevelProgramIndex.HasValue)
            .GroupBy(t => t.TopLevelProgramIndex!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        _applicationTask = tasks.FirstOrDefault(t => t.MainProgram) ?? _topLevelTaskDefsByOrdinal.FirstOrDefault();
    }

    private static TaskDef? GetTaskByOrdinal(int ordinal)
        => _taskDefsByOrdinal.TryGetValue(ordinal, out var task) ? task : null;

    private static IReadOnlyList<TaskDef> GetChildTasks(int parentOrdinal)
        => _childTaskDefsByParentOrdinal.TryGetValue(parentOrdinal, out var children)
            ? children
            : Array.Empty<TaskDef>();

    private static TaskDef? GetChildTaskBySubtaskIndex(int parentOrdinal, int subtaskIndex)
        => _childTaskDefByParentAndSubtaskIndex.TryGetValue((parentOrdinal, subtaskIndex), out var task)
            ? task
            : null;

    private static DataObjectDef? GetDataObjectByOrdinal(int ordinal)
        => _dataObjectsByOrdinal.TryGetValue(ordinal, out var dataObject) ? dataObject : null;

    private static void BuildViewIndexes(IReadOnlyList<TaskDef> tasks)
    {
        var shouldGenerate = new Dictionary<int, bool>();
        var baseNames = new Dictionary<int, string>();
        foreach (var task in tasks)
        {
            var generate = ShouldGenerateViewCore(task);
            shouldGenerate[task.Ordinal] = generate;
            baseNames[task.Ordinal] = ResolveViewClassBaseNameCore(task);
        }

        var duplicateIndexes = tasks
            .Where(t => shouldGenerate.GetValueOrDefault(t.Ordinal))
            .OrderBy(t => t.Ordinal)
            // Generated view files share a Windows directory. C# identifiers are
            // case-sensitive, but the filesystem is not, so case-only variants
            // must receive different generated names before files are written.
            .GroupBy(t => baseNames[t.Ordinal], StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Select((task, index) => new { task.Ordinal, Index = index }))
            .ToDictionary(x => x.Ordinal, x => x.Index);

        _shouldGenerateViewByTaskOrdinal = shouldGenerate;
        _viewClassBaseNameByTaskOrdinal = baseNames;
        _viewClassDuplicateIndexByTaskOrdinal = duplicateIndexes;
    }

    private static string QuoteTelemetry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static TaskSemantic BuildTaskSemantic(
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<ControlButtonModelDef> buttonModels)
    {
        var traceTask = ShouldTraceTask(task);
        if (traceTask)
            LogTaskStage(task, "expressions start");
        var expressions = BuildExpressionSemantic(task);
        if (traceTask)
            LogTaskStage(task, "events start");
        var events = BuildEventSemantic(task);
        if (traceTask)
            LogTaskStage(task, "io start");
        var io = BuildIoSemantic(task);
        if (traceTask)
            LogTaskStage(task, "report start");
        var report = BuildReportSemantic(task, io, events, expressions);
        if (traceTask)
            LogTaskStage(task, "merge start");
        var merge = BuildMergeSemantic(task);
        if (traceTask)
            LogTaskStage(task, "logic start");
        var logic = new LogicSemantic(task.StartLogics, task.StartRaises, task.RowLogics, task.EndLogics, task.EndRaises, task.SavingRowLogics, task.GroupLogics);
        if (traceTask)
            LogTaskStage(task, "dataview start");
        var dataView = BuildDataViewSemantic(task);
        if (traceTask)
            LogTaskStage(task, "selects start");
        var selectsSemantic = BuildSelectSemantic(task, dataObjects);
        if (traceTask)
            LogTaskStage(task, "resources start");
        var resourcesSemantic = BuildResourceSemantic(task);
        if (traceTask)
            LogTaskStage(task, "function overrides start");
        var functionOverrides = BuildFunctionOverridesSemantic(task);
        if (traceTask)
            LogTaskStage(task, "view start");
        var view = BuildViewSemantic(task, allTasks, dataObjects, buttonModels);
        if (traceTask)
            LogTaskStage(task, "layout start");
        var layout = BuildLayoutSemantic(task, allTasks, dataObjects);
        if (traceTask)
            LogTaskStage(task, "execution start");
        var activity = ResolveActivityByInitialMode(task.InitialMode);
        var rowLocking = task.LockingStrategy switch
        {
            "B" => "LockingStrategy.OnRowSaving",
            "I" => "LockingStrategy.OnRowLoading",
            "O" => "LockingStrategy.OnUserEdit",
            "U" => "LockingStrategy.OnUserEdit",
            _ => null
        };
        var transactionScope = task.TransactionBegin switch
        {
            "L" => "TransactionScopes.RowLocking",
            "U" => "TransactionScopes.SaveToDatabase",
            "T" => "TransactionScopes.Task",
            "R" => "TransactionScopes.Row",
            _ => null
        };
        transactionScope ??= task.TransactionMode switch
        {
            "P" => "TransactionScopes.SaveToDatabase",
            "W" => "TransactionScopes.Task",
            "R" => "TransactionScopes.Row",
            _ => null
        };
        if (string.Equals(task.TransactionMode, "W", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionBegin, "P", StringComparison.OrdinalIgnoreCase) &&
            task.InitialModeExpressionId.HasValue)
        {
            transactionScope = "TransactionScopes.Row";
        }
        if (string.Equals(task.TaskType, "O", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.InitialMode, "E", StringComparison.OrdinalIgnoreCase) &&
            !task.InitialModeExpressionId.HasValue &&
            string.Equals(task.TransactionMode, "W", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionBegin, "P", StringComparison.OrdinalIgnoreCase))
        {
            transactionScope = "TransactionScopes.Row";
        }
        if (string.Equals(task.TaskType, "O", StringComparison.OrdinalIgnoreCase) &&
            task.ResourceDbs.Count == 0 &&
            string.Equals(task.TransactionMode, "W", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(task.TransactionBegin, "P", StringComparison.OrdinalIgnoreCase))
        {
            transactionScope = "TransactionScopes.Row";
            rowLocking = null;
        }
        if (string.Equals(transactionScope, "TransactionScopes.SaveToDatabase", StringComparison.Ordinal))
            transactionScope = null;

        var switchToInsertWhenNoRows =
            task.SelectionTable ||
            (string.Equals(task.TaskType, "O", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(task.InitialMode, "M", StringComparison.OrdinalIgnoreCase) &&
             !task.InitialModeExpressionId.HasValue &&
             task.AllowEmptyDataview == false);
        var exitTiming = task.EvaluateEndCondition switch
        {
            "B" => "ExitTiming.BeforeRow",
            "A" => "ExitTiming.AfterRow",
            _ => null
        };

        var execution = new ExecutionSemantic(
            task.TaskType,
            task.TransactionMode,
            task.TransactionBegin,
            task.LockingStrategy,
            task.ErrorStrategy,
            task.ParallelExecution,
            task.EndTaskCondition,
            task.EndTaskConditionExpressionId,
            task.EvaluateEndCondition,
            activity,
            task.InitialModeExpressionId,
            rowLocking,
            transactionScope,
            task.ErrorStrategy == "R" && task.TaskType == "B",
            task.Resident,
            task.SelectionTable,
            switchToInsertWhenNoRows,
            task.AllowPrintingData || report.HasPrinterWriter,
            exitTiming);
        var valueChangedHandlers = task.Handlers
            .Where(IsValueChangedHandler)
            .ToList();
        var userCommandHandlers = task.Handlers
            .Where(h => h.EventType == "U")
            .ToList();
        var internalHandlers = task.Handlers
            .Where(h => h.EventType == "I")
            .ToList();
        var expressionHandlers = task.Handlers
            .Where(h => h.EventType == "E")
            .ToList();
        var timerHandlers = task.Handlers
            .Where(h => h.EventType == "T")
            .ToList();
        var systemHandlers = task.Handlers
            .Where(h => h.EventType == "S" && !IsControlEventHandler(h))
            .ToList();
        var recordHandlers = task.Handlers
            .Where(h => h.EventType == "R")
            .ToList();
        var unmappedHandlers = task.Handlers
            .Where(h => !IsValueChangedHandler(h) && !IsControlEventHandler(h) && h.EventType is not ("U" or "I" or "E" or "T" or "S" or "R"))
            .ToList();
        var bodies = task.Handlers
            .Select(h => BuildHandlerBodySemantic(task, h))
            .ToList();
        var handlers = new HandlerSemantic(
            task.Handlers,
            task.Handlers.Any(h => h.EventType == "U"),
            task.Handlers.Any(h => h.Raises.Count > 0),
            task.Handlers.Any(h => h.Invokes.Count > 0),
            task.Handlers.Any(h => h.Calls.Count > 0),
            task.Handlers.Any(h => h.Updates.Count > 0),
            task.Handlers.Any(h => h.Stops.Count > 0),
            valueChangedHandlers,
            userCommandHandlers,
            internalHandlers,
            expressionHandlers,
            timerHandlers,
            systemHandlers,
            recordHandlers,
            unmappedHandlers,
            bodies);

        if (traceTask)
            LogTaskStage(task, "record create");
        var semantic = new TaskSemantic(
            task.Ordinal,
            task.TopLevelProgramIndex,
            task.TopLevelProgramIndexLocal,
            task.ParentOrdinal,
            task.SubtaskIndex,
            task.Description,
            task.Folder,
            task.IsEmptyTask,
            task.PublicName,
            task.Resident,
            task.TaskType,
            task.DeclaredParameterCount,
            task.ReturnValue,
            task.ReturnValueExpressionId,
            task.ParallelExecution,
            task.CopyGlobalParameters,
            task.SingleInstance,
            task.TaskId,
            task.Icon,
            task.MainProgram,
            task.Cnfu,
            task.Clss,
            task.MgAttr,
            task.Pdod,
            task.Propagate,
            task.InitialMode,
            task.LocateDirection,
            task.RangeDirection,
            task.LocateExpressionId,
            task.RangeExpressionId,
            task.TotalVariabls,
            task.TotalVirtuals,
            task.EndTaskCondition,
            task.EndTaskConditionExpressionId,
            task.EvaluateEndCondition,
            task.LockingStrategy,
            task.TransactionMode,
            task.TransactionBegin,
            task.ErrorStrategy,
            task.SelectionTable,
            task.CacheStrategy,
            task.ForceRecordSuffix,
            task.AllowEmptyDataview,
            task.PreloadView,
            task.AllowActivitySwitch,
            task.AllowQuery,
            task.AllowModify,
            task.AllowCreate,
            task.AllowDelete,
            task.AllowEvents,
            task.OpenTaskWindow,
            task.CloseTaskWindow,
            task.AllowPrintingData,
            task.AllowCreateExpression,
            task.SqlWhere,
            task.VarRangeInfos,
            task.SqlForm,
            task.FormName,
            task.Form,
            task.ResourceDataObjects,
            task.ResourceDbs,
            task.ResourceColumns,
            task.InformationDbObj,
            task.InitialKeyIndexId,
            task.InitialKeyExpressionId,
            task.PrimaryDbObj,
            task.SortSegments,
            task.Selects,
            task.Links,
            task.TabCalls,
            task.StartLogics,
            task.StartRaises,
            task.EndLogics,
            task.EndRaises,
            task.HasStartLogicUnit,
            task.HasEndLogicUnit,
            task.RowLogics,
            task.SavingRowLogics,
            task.FlowValidations,
            task.Events,
            task.Expressions,
            task.FunctionOverrides,
            task.Handlers,
            task.GroupLogics,
            task.Gaps,
            task.DisplayExpressionId,
            task.FormEntries,
            task.FormIos,
            task.Io,
            task.Ios,
            task.SourceComponent,
            task.PublicName ?? task.Description,
            dataView,
            selectsSemantic,
            resourcesSemantic,
            execution,
            io,
            events,
            handlers,
            expressions,
            logic,
            functionOverrides,
            view,
            layout,
            merge,
            report,
            BuildUnhandled(task));
        if (traceTask)
            LogTaskStage(task, "done");
        return semantic;
    }

    private static bool ShouldTraceTask(TaskDef task)
        => task.Ordinal <= 5 || task.Ordinal % 100 == 0;

    private static void LogTaskStage(TaskDef task, string stage)
        => ConversionTelemetry.Log(
            "SEMANTIC",
            $"task stage ordinal={task.Ordinal} stage={stage} parent={task.ParentOrdinal?.ToString(CultureInfo.InvariantCulture) ?? ""} description={QuoteTelemetry(task.Description)}");

    private static DataViewSemantic BuildDataViewSemantic(TaskDef task)
    {
        var ranges = task.Selects.Where(s => s.HasRange).ToList();
        var locates = task.Selects.Where(s => s.Type.Contains("Locate", StringComparison.OrdinalIgnoreCase)).ToList();
        var mainDataViewSource = task.DataViewSources.FirstOrDefault(s =>
            string.Equals(s.Type, "M", StringComparison.OrdinalIgnoreCase));
        // A DATAVIEW_SRC M without IDX represents the synthetic/virtual record
        // section. The resource entity may still be cached or used by relations,
        // but it must not become the controller's From source.
        var hasVirtualOnlyMainSource = mainDataViewSource is not null && !mainDataViewSource.Index.HasValue;
        var hasEntityOnlyDataViewSource =
            string.Equals(task.TaskType, "B", StringComparison.OrdinalIgnoreCase) &&
            task.Selects.Count == 0 &&
            (task.DataViewSources.Any(s => string.Equals(s.Type, "D", StringComparison.OrdinalIgnoreCase)) ||
             task.ResourceDbs.Any(db => db.Cache == true));
        return new DataViewSemantic(
            task.PrimaryDbObj ?? task.InformationDbObj,
            task.ResourceDataObjects,
            task.Selects,
            task.Links,
            task.TabCalls,
            task.SortSegments,
            ranges,
            locates,
            !hasEntityOnlyDataViewSource &&
            !hasVirtualOnlyMainSource &&
            (task.ResourceDataObjects.Count > 0 || task.PrimaryDbObj.HasValue || task.InformationDbObj.HasValue),
            task.SortSegments.Count > 0);
    }

    private static IOSemantic BuildIoSemantic(TaskDef task)
    {
        var sources = new List<IOSourceSemantic>();
        foreach (var taskIo in task.Ios)
        {
            var kind = ResolveIoKind(task);
            sources.Add(new IOSourceSemantic(
                taskIo.Description ?? task.Description,
                kind,
                taskIo.PrintPreview == true,
                taskIo.OpenPrintDialog == true,
                taskIo.PrintingAllowed != false,
                taskIo.Media,
                taskIo.Access,
                taskIo.IoExpressionId,
                null,
                null));
        }

        foreach (var formIo in task.FormIos.Where(x => !string.Equals(x.Level, "H", StringComparison.OrdinalIgnoreCase)))
        {
            sources.Add(new IOSourceSemantic(
                formIo.Reference ?? formIo.OperationType,
                ResolveFormIoKind(formIo),
                false,
                false,
                true,
                null,
                null,
                null,
                formIo.Page,
                formIo.IoDeviceIndex));
        }

        return new IOSemantic(
            sources,
            sources.Any(s => s.Kind == "PrinterWriter"),
            sources.Any(s => s.Kind == "TextPrinterWriter"),
            sources.Any(s => s.Kind == "FileWriter"),
            sources.Count > 1);
    }

    private static EventSemantic BuildEventSemantic(TaskDef task)
    {
        var items = task.Events.ToList();
        var itemsByOrdinal = task.Events.ToDictionary(e => e.Ordinal);
        var descriptionByOrdinal = task.Events
            .Where(e => !string.IsNullOrWhiteSpace(e.Description))
            .ToDictionary(e => e.Ordinal, e => e.Description!);
        var commandByDescription = task.Events
            .Where(e => !string.IsNullOrWhiteSpace(e.Description))
            .GroupBy(e => e.Description!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => "Command." + ToPascalIdentifier(g.Key),
                StringComparer.OrdinalIgnoreCase);
        var commandByOrdinal = task.Events
            .Where(e => !string.IsNullOrWhiteSpace(e.Description))
            .ToDictionary(e => e.Ordinal, e => "Command." + ToPascalIdentifier(e.Description!));
        return new EventSemantic(
            HasEvent(task, "Page Header"),
            HasEvent(task, "Page Footer"),
            HasEvent(task, "Record Prefix"),
            HasEvent(task, "Record Suffix"),
            HasEvent(task, "Task Prefix"),
            HasEvent(task, "Task Suffix"),
            task.HasStartLogicUnit || task.StartLogics.Count > 0,
            task.HasEndLogicUnit || task.EndLogics.Count > 0,
            items,
            commandByDescription,
            itemsByOrdinal,
            descriptionByOrdinal,
            commandByOrdinal);
    }

    private static ExpressionSemantic BuildExpressionSemantic(TaskDef task)
    {
        var text = string.Join("\n", task.Expressions.Select(e => e.Syntax));
        var entries = task.Expressions
            .Select(e => new ExpressionEntrySemantic(
                e.Ordinal,
                e.Syntax,
                e.Syntax,
                e.Attribute,
                e.Syntax,
                IsStringLiteralSyntax(e.Syntax),
                ContainsToken(e.Syntax, "EOP"),
                ContainsToken(e.Syntax, "IOCurr"),
                ContainsToken(e.Syntax, "Page"),
                ContainsToken(e.Syntax, "Line"),
                ContainsToken(e.Syntax, "Str("),
                ResolveWholeExpressionSemanticKind(e.Syntax)))
            .ToList();
        var entriesByOrdinal = entries.ToDictionary(x => x.Ordinal);
        var invocationByOrdinal = entries.ToDictionary(x => x.Ordinal, x => $"Exp_{x.Ordinal}()");
        return new ExpressionSemantic(
            task.Expressions,
            text.Contains("EOP", StringComparison.OrdinalIgnoreCase),
            text.Contains("IOCurr", StringComparison.OrdinalIgnoreCase),
            text.Contains("Page", StringComparison.OrdinalIgnoreCase),
            text.Contains("Line", StringComparison.OrdinalIgnoreCase),
            text.Contains("Str(", StringComparison.OrdinalIgnoreCase),
            entries,
            entriesByOrdinal,
            invocationByOrdinal);
    }

    private static MergeSemantic BuildMergeSemantic(TaskDef task)
    {
        var mergeForm = task.FormEntries.FirstOrDefault(x => x.Model == "FORM_MERGE")?.Form ?? task.Form;
        var tagExpressionIds = mergeForm?.MergeTags
            .Where(x => x.ExpressionId.HasValue)
            .Select(x => x.ExpressionId!.Value)
            .ToList() ?? new List<int>();
        var templateExpr = mergeForm?.MergeFileNameExpressionId.HasValue == true
            ? mergeForm.MergeFileNameExpressionId.Value.ToString()
            : null;
        return new MergeSemantic(
            mergeForm?.MergeTags.Count > 0 || mergeForm?.MergeFileNameExpressionId.HasValue == true,
            templateExpr,
            mergeForm?.MergeTags ?? Array.Empty<TaskMergeTagDef>(),
            tagExpressionIds);
    }

    private static IReadOnlyList<FunctionOverrideSemantic> BuildFunctionOverridesSemantic(TaskDef task)
    {
        var result = new List<FunctionOverrideSemantic>();
        foreach (var fn in task.FunctionOverrides)
        {
            if (string.IsNullOrWhiteSpace(fn.Name))
                continue;

            var returnExpr = fn.ReturnExpressionId.HasValue
                ? task.Expressions.FirstOrDefault(e => e.Ordinal == fn.ReturnExpressionId.Value)
                : null;
            var returnType = returnExpr?.Attribute switch
            {
                "N" => "Number",
                "D" => "Date",
                "T" => "Time",
                "L" => "Bool",
                "B" => "Bool",
                _ => "Text"
            };

            var parameters = new List<FunctionParameterSemantic>();
            foreach (var p in fn.Parameters)
            {
                var rc = task.ResourceColumns.FirstOrDefault(c => c.Id == p.ColumnId);
                if (rc is null)
                    continue;

                var type = rc.AttrObj switch
                {
                    "FIELD_NUMERIC" => "Number",
                    "FIELD_DATE" => "Date",
                    "FIELD_TIME" => "Time",
                    "FIELD_BOOLEAN" => "Bool",
                    "FIELD_LOGICAL" => "Bool",
                    "FIELD_BLOB" => "byte[]",
                    _ => "Text"
                };
                var paramName = "p" + ToLegacyVariableName(rc.Name);
                parameters.Add(new FunctionParameterSemantic(p, p.SelectName, p.ColumnId, type, paramName, rc.Name));
            }

            var remarks = fn.Remarks
                .Select(r => r.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            var orderedActions = fn.Actions
                .OrderBy(a => ExtractLogicLineOrder(a.XmlTrace) ?? int.MaxValue)
                .ToList();
            var blocks = BuildActionBlocks(task, orderedActions);

            result.Add(new FunctionOverrideSemantic(
                fn,
                fn.Name,
                ToCodeIdentifierPreservingCase(fn.Name),
                returnType,
                fn.ReturnExpressionId,
                parameters,
                remarks,
                orderedActions,
                blocks));
        }

        return result;
    }

    private static LayoutSemantic BuildLayoutSemantic(TaskDef task, IReadOnlyList<TaskDef> allTasks, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var textIoFormIndexes = task.FormIos
            .Where(io => string.Equals(io.OperationType, "I", StringComparison.OrdinalIgnoreCase) && io.FormEntryIndex.HasValue)
            .Select(io => io.FormEntryIndex!.Value)
            .ToHashSet();

        var printForms = task.FormEntries
            .Where(x => string.Equals(x.Model, "FORM_GUI1", StringComparison.OrdinalIgnoreCase) && !textIoFormIndexes.Contains(x.Index))
            .OrderBy(x => x.Index)
            .ToList();
        var textForms = task.FormEntries
            .Where(x =>
                string.Equals(x.Model, "FORM_TEXT", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(x.Model, "FORM_GUI1", StringComparison.OrdinalIgnoreCase) && textIoFormIndexes.Contains(x.Index)))
            .OrderBy(x => x.Index)
            .ToList();
        var mergeForms = task.FormEntries
            .Where(x => string.Equals(x.Model, "FORM_MERGE", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Index)
            .ToList();
        var referencedFormIndexes = task.FormIos
            .Where(io => io.FormEntryIndex.HasValue)
            .Select(io => io.FormEntryIndex!.Value)
            .Distinct()
            .ToHashSet();
        var supportedPrintControlsByFormEntryIndex = printForms.ToDictionary(
            fe => fe.Index,
            fe => (IReadOnlyList<TaskFormControlDef>)fe.Form.Controls.Where(IsSupportedPrintControl).OrderBy(c => c.Id).ToList());
        var unsupportedPrintControlsByFormEntryIndex = printForms.ToDictionary(
            fe => fe.Index,
            fe => (IReadOnlyList<TaskFormControlDef>)fe.Form.Controls.Where(c => !IsSupportedPrintControl(c)).OrderBy(c => c.Id).ToList());
        var supportedTextIoControlsByFormEntryIndex = textForms.ToDictionary(
            fe => fe.Index,
            fe => (IReadOnlyList<TaskFormControlDef>)fe.Form.Controls.Where(IsSupportedTextIoControl).OrderBy(c => c.Id).ToList());
        var unsupportedTextIoControlsByFormEntryIndex = textForms.ToDictionary(
            fe => fe.Index,
            fe => (IReadOnlyList<TaskFormControlDef>)fe.Form.Controls.Where(c => !IsSupportedTextIoControl(c)).OrderBy(c => c.Id).ToList());
        var printControlTypeNameByFormEntryIndex = supportedPrintControlsByFormEntryIndex.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<int, string>)kv.Value.ToDictionary(c => c.Id, ResolvePrintControlTypeName));
        var textIoControlTypeNameByFormEntryIndex = supportedTextIoControlsByFormEntryIndex.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<int, string>)kv.Value.ToDictionary(c => c.Id, ResolveTextIoControlTypeName));
        var printTableAttachmentByLeafByFormEntryIndex = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        var printColumnAttachmentByLeafByFormEntryIndex = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        var printColumnChildIdsByColumnByFormEntryIndex = new Dictionary<int, IReadOnlyDictionary<int, IReadOnlyList<int>>>();
        var printTableChildIdsByTableByFormEntryIndex = new Dictionary<int, IReadOnlyDictionary<int, IReadOnlyList<int>>>();
        var printSectionRootControlIdsByFormEntryIndex = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var kv in supportedPrintControlsByFormEntryIndex)
        {
            var controls = kv.Value.ToList();
            var map = controls.ToDictionary(c => c.Id);
            var tables = controls.Where(IsPrintTableControl).ToList();
            var columns = controls.Where(IsPrintTableColumnControl).ToList();
            var leaves = controls.Where(IsPrintLeafControl).ToList();
            var tableAttachmentByLeaf = new Dictionary<int, int>();
            var columnAttachmentByLeaf = new Dictionary<int, int>();
            foreach (var table in tables)
            {
                var cols = columns.Where(c => c.ParentId == table.Id).OrderBy(c => c.ControlLayer ?? int.MaxValue).ThenBy(c => c.Id).ToList();
                var colByLayer = cols.Where(c => c.ControlLayer.HasValue).GroupBy(c => c.ControlLayer!.Value).ToDictionary(g => g.Key, g => g.First());
                foreach (var leaf in leaves.Where(x => x.ParentId == table.Id))
                {
                    if (leaf.ControlLayer.HasValue && colByLayer.TryGetValue(leaf.ControlLayer.Value, out var col))
                        columnAttachmentByLeaf[leaf.Id] = col.Id;
                    else
                        tableAttachmentByLeaf[leaf.Id] = table.Id;
                }
            }
            foreach (var leaf in leaves.Where(x => x.ParentId.HasValue && map.TryGetValue(x.ParentId.Value, out var parent) && IsPrintTableColumnControl(parent)))
                columnAttachmentByLeaf[leaf.Id] = leaf.ParentId!.Value;
            printTableAttachmentByLeafByFormEntryIndex[kv.Key] = tableAttachmentByLeaf;
            printColumnAttachmentByLeafByFormEntryIndex[kv.Key] = columnAttachmentByLeaf;

            var columnChildIdsByColumn = columns.ToDictionary(
                col => col.Id,
                col => (IReadOnlyList<int>)leaves
                    .Where(x => columnAttachmentByLeaf.TryGetValue(x.Id, out var parentId) && parentId == col.Id)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .ToList());
            printColumnChildIdsByColumnByFormEntryIndex[kv.Key] = columnChildIdsByColumn;

            var tableChildIdsByTable = tables.ToDictionary(
                table => table.Id,
                table =>
                {
                    var orderedIds = new List<int>();
                    orderedIds.AddRange(columns
                        .Where(x => x.ParentId == table.Id)
                        .OrderBy(x => x.ControlLayer ?? int.MaxValue)
                        .ThenBy(x => x.Id)
                        .Select(x => x.Id));
                    orderedIds.AddRange(leaves
                        .Where(x => tableAttachmentByLeaf.TryGetValue(x.Id, out var parentId) && parentId == table.Id)
                        .OrderBy(x => x.Id)
                        .Select(x => x.Id));
                    return (IReadOnlyList<int>)orderedIds;
                });
            printTableChildIdsByTableByFormEntryIndex[kv.Key] = tableChildIdsByTable;

            printSectionRootControlIdsByFormEntryIndex[kv.Key] = controls
                .Where(c => !IsPrintTableColumnControl(c) && !columnAttachmentByLeaf.ContainsKey(c.Id) && !tableAttachmentByLeaf.ContainsKey(c.Id))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .ToList();
        }

        var mergeTemplateVars = BuildUniqueFormEntryNameMap(mergeForms, (form, _) => ResolveMergeTemplateVariableName(form));
        var printSectionNames = BuildUniqueFormEntryNameMap(printForms, ResolvePrintSectionName);
        var textSectionNames = BuildUniqueFormEntryNameMap(textForms, ResolveTextIoSectionName);
        var textIoPageHeaderSectionNames = new HashSet<string>(StringComparer.Ordinal);
        if (task.Io?.PageHeaderFormEntryIndex is int explicitTextHeaderIndex &&
            textSectionNames.TryGetValue(explicitTextHeaderIndex, out var explicitTextHeaderSection))
        {
            textIoPageHeaderSectionNames.Add(explicitTextHeaderSection);
        }
        var printControlVariableNameByFormEntryIndex = supportedPrintControlsByFormEntryIndex.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<int, string>)kv.Value.ToDictionary(c => c.Id, c => ResolvePrintControlVariableName(c, kv.Key)));
        var textIoControlVariableNameByFormEntryIndex = BuildTextIoControlVariableNameMap(task, textForms, supportedTextIoControlsByFormEntryIndex, allTasks, dataObjects);
        var textIoSectionControlIdsByFormEntryIndex = supportedTextIoControlsByFormEntryIndex.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value.OrderBy(c => c.Id).Select(c => c.Id).ToList());
        var formIoWriteCalls = BuildFormIoWriteCalls(task, allTasks, printForms, textForms, mergeForms, printSectionNames, textSectionNames, mergeTemplateVars);
        var formIoReadCalls = BuildFormIoReadCalls(task, textForms, textSectionNames);
        var pageHeaderSectionName = ResolvePageHeaderSectionName(task, printForms, printSectionNames);
        var pageFooterSectionName = ResolvePageFooterSectionName(task, printForms, printSectionNames);
        var printOutputIos = task.FormIos
            .Where(x => x.OperationType == "O" && x.FormEntryIndex.HasValue && printSectionNames.ContainsKey(x.FormEntryIndex.Value))
            .ToList();
        var printGroupIos = printOutputIos
            .Where(x => x.Level == "G" && (x.Type == "P" || x.Type == "S"))
            .ToList();
        var startTaskOutputIos = task.FormIos
            .Where(x => x.OperationType == "O" && x.Level == "T" && x.Type == "P" && x.FormEntryIndex.HasValue)
            .ToList();
        var endTaskOutputIos = task.FormIos
            .Where(x => x.OperationType == "O" && x.Level == "T" && x.Type == "S" && x.FormEntryIndex.HasValue)
            .ToList();
        var printRowIos = task.FormIos
            .Where(x => x.OperationType == "O" && x.Level == "R" && x.Type == "S" && x.FormEntryIndex.HasValue)
            .ToList();
        var textIoReadRowIos = task.FormIos
            .Where(x => x.OperationType == "I" && x.Level == "R" && x.Type == "S" && x.FormEntryIndex.HasValue)
            .ToList();

        return new LayoutSemantic(
            printForms,
            textForms,
            mergeForms,
            referencedFormIndexes,
            supportedPrintControlsByFormEntryIndex,
            unsupportedPrintControlsByFormEntryIndex,
            supportedTextIoControlsByFormEntryIndex,
            unsupportedTextIoControlsByFormEntryIndex,
            printControlTypeNameByFormEntryIndex,
            textIoControlTypeNameByFormEntryIndex,
            printTableAttachmentByLeafByFormEntryIndex,
            printColumnAttachmentByLeafByFormEntryIndex,
            printColumnChildIdsByColumnByFormEntryIndex,
            printTableChildIdsByTableByFormEntryIndex,
            printSectionRootControlIdsByFormEntryIndex,
            BuildPrintLayoutClassName(task, allTasks),
            textForms.Count == 0 ? null : BuildTextIoLayoutClassName(task, allTasks, textForms.First()),
            textForms.Count == 0 ? null : ResolveTextIoNamespaceSegment(task, allTasks),
            mergeTemplateVars,
            printSectionNames,
            textSectionNames,
            textIoPageHeaderSectionNames,
            printControlVariableNameByFormEntryIndex,
            textIoControlVariableNameByFormEntryIndex,
            textIoSectionControlIdsByFormEntryIndex,
            formIoWriteCalls,
            formIoReadCalls,
            pageHeaderSectionName,
            pageFooterSectionName,
            printOutputIos,
            printGroupIos,
            startTaskOutputIos,
            endTaskOutputIos,
            printRowIos,
            textIoReadRowIos);
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> BuildTextIoControlVariableNameMap(
        TaskDef task,
        IReadOnlyList<TaskFormEntryDef> textForms,
        IReadOnlyDictionary<int, IReadOnlyList<TaskFormControlDef>> supportedTextIoControlsByFormEntryIndex,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var formEntry in textForms)
        {
            if (!supportedTextIoControlsByFormEntryIndex.TryGetValue(formEntry.Index, out var controls))
                continue;
            var map = new Dictionary<int, string>();
            foreach (var c in controls)
            {
                var varName = ResolveTextIoControlVariableName(c, formEntry.Index, task, allTasks, dataObjects);
                if (!used.Add(varName))
                {
                    varName = varName + "_" + c.Id;
                    while (!used.Add(varName))
                        varName += "_";
                }
                map[c.Id] = varName;
            }
            result[formEntry.Index] = map;
        }
        return result;
    }

    private static bool IsSupportedPrintControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI1_STATIC" or "CTRL_GUI1_EDIT" or "CTRL_GUI1_LINE" or "CTRL_GUI1_SHAPE" or "CTRL_GUI1_TABLE" or "CTRL_GUI1_COLUMN";
    }

    private static bool IsSupportedTextIoControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_TEXT_STATIC" or "CTRL_TEXT_EDIT" or "CTRL_TEXT_LINE" or "CTRL_TEXT_SHAPE";
    }

    private static bool IsPrintTableControl(TaskFormControlDef c) => c.Model == "CTRL_GUI1_TABLE";
    private static bool IsPrintTableColumnControl(TaskFormControlDef c) => c.Model == "CTRL_GUI1_COLUMN";
    private static bool IsPrintLeafControl(TaskFormControlDef c) => !IsPrintTableControl(c) && !IsPrintTableColumnControl(c);

    private static bool IsViewEditControl(TaskFormControlDef c)
    {
        return c.Model is "CTRL_GUI0_EDIT" or "CTRL_RICH_CLIENT_EDIT" or "CTRL_BROWSER_EDIT" or "CTRL_GUI0_RICH_EDIT";
    }

    private static string ResolveTextIoControlTypeName(TaskFormControlDef c)
    {
        return c.Model switch
        {
            "CTRL_TEXT_STATIC" => "Shared.Theme.TextIO.TextLabel",
            "CTRL_TEXT_EDIT" => "Shared.Theme.TextIO.TextBox",
            "CTRL_TEXT_LINE" => "Shared.Theme.TextIO.Line",
            "CTRL_TEXT_SHAPE" => "Shared.Theme.TextIO.Shape",
            _ => "Shared.Theme.TextIO.TextBox"
        };
    }

    private static string ResolvePrintControlTypeName(TaskFormControlDef c)
    {
        return c.Model switch
        {
            "CTRL_GUI1_STATIC" => "Shared.Theme.Printing.TextBox",
            "CTRL_GUI1_EDIT" => "Shared.Theme.Printing.TextBox",
            "CTRL_GUI1_LINE" => "Shared.Theme.Printing.Line",
            "CTRL_GUI1_SHAPE" => "Shared.Theme.Printing.Shape",
            "CTRL_GUI1_TABLE" => "Shared.Theme.Printing.Grid",
            "CTRL_GUI1_COLUMN" => "Shared.Theme.Printing.GridColumn",
            _ => "Shared.Theme.Printing.TextBox"
        };
    }

    private static string ResolvePrintControlVariableName(TaskFormControlDef c, int formEntryIndex)
    {
        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : !string.IsNullOrWhiteSpace(c.ColumnTitle)
                ? ToPascalIdentifier(c.ColumnTitle)
                : c.Id.ToString();
        var prefix = c.Model switch
        {
            "CTRL_GUI1_STATIC" => "lbl",
            "CTRL_GUI1_EDIT" => "txt",
            "CTRL_GUI1_LINE" => "lin",
            "CTRL_GUI1_SHAPE" => "shp",
            "CTRL_GUI1_TABLE" => "grd",
            "CTRL_GUI1_COLUMN" => "gcl",
            _ => "ctl"
        };
        return $"{prefix}S{formEntryIndex}_{suffix}";
    }

    private static string ResolveTextIoControlVariableName(
        TaskFormControlDef c,
        int formEntryIndex,
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects)
    {
        if (c.DataExpressionId.HasValue)
            return $"txtExp_{c.DataExpressionId.Value}";
        if (c.Model == "CTRL_TEXT_STATIC" && string.IsNullOrWhiteSpace(c.ControlName) && !string.IsNullOrWhiteSpace(c.Text))
        {
            var normalizedText = NormalizeIdentifierTokensWithUnderscore(c.Text);
            if (!string.IsNullOrWhiteSpace(normalizedText))
                return "lbl" + normalizedText;
        }
        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : c.Id.ToString();
        var prefix = c.Model switch
        {
            "CTRL_TEXT_STATIC" => "lbl",
            "CTRL_TEXT_EDIT" => "txt",
            "CTRL_TEXT_LINE" => "lin",
            "CTRL_TEXT_SHAPE" => "shp",
            _ => "ctl"
        };
        return $"{prefix}S{formEntryIndex}_{suffix}";
    }

    private static string NormalizeIdentifierTokensWithUnderscore(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var raw = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var tokens = raw
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Regex.IsMatch(t, @"^\d+$") ? t : ToPascalIdentifier(t))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (tokens.Count == 0)
            return "";
        var joined = string.Join("_", tokens);
        if (!Regex.IsMatch(joined, @"^[A-Za-z_]"))
            joined = "_" + joined;
        return joined;
    }

    private static ViewSemantic BuildViewSemantic(
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<ControlButtonModelDef> buttonModels)
    {
        var shouldGenerate = ShouldGenerateView(task);
        var baseClassName = ResolveViewClassBaseName(task, allTasks);
        var className = ResolveViewClassName(task, allTasks);
        var selectedFormEntry = task.FormEntries.FirstOrDefault(fe =>
            string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) &&
            task.Form is not null &&
            Equals(fe.Form, task.Form));
        selectedFormEntry ??= task.FormEntries.FirstOrDefault(fe =>
            string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(task.FormName) &&
            string.Equals(fe.Form.FormName, task.FormName, StringComparison.OrdinalIgnoreCase));
        selectedFormEntry ??= task.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase))
            .OrderBy(fe => fe.Index)
            .FirstOrDefault();
        var selectedFormEntryIndex = selectedFormEntry?.Index;
        var selectedForm = selectedFormEntry?.Form ?? task.Form;
        var selectedFormControls = (selectedForm?.Controls ?? new List<TaskFormControlDef>())
            .OrderBy(c => c.TabOrder ?? int.MaxValue)
            .ThenBy(c => c.Id)
            .ToList();
        var selectedSupportedControls = selectedFormControls
            .Where(IsSupportedViewControl)
            .ToList();
        var selectedUnsupportedControls = selectedFormControls
            .Where(c => !IsSupportedViewControl(c))
            .ToList();
        var supportedChildParentIds = selectedSupportedControls
            .Where(c => c.ParentId.HasValue)
            .Select(c => c.ParentId!.Value)
            .ToHashSet();
        var tableColumnControlIds = selectedSupportedControls
            .Where(IsTableColumnViewControl)
            .Select(c => c.Id)
            .ToHashSet();
        var staticContainerIds = selectedSupportedControls
            .Where(c => string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
            .Where(c => supportedChildParentIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToHashSet();
        var tableControls = selectedSupportedControls.Where(IsTableViewControl).ToList();
        var tableColumnControls = selectedSupportedControls.Where(IsTableColumnViewControl).ToList();
        var leafControls = selectedSupportedControls.Where(IsLeafViewControl).ToList();
        var containerControls = selectedSupportedControls
            .Where(c => IsStaticGroupBoxLike(c, staticContainerIds) || IsStaticShapeLike(c, staticContainerIds))
            .ToList();
        var controlTypeNameById = selectedSupportedControls.ToDictionary(
            c => c.Id,
            c => ResolveViewControlTypeName(c, task, buttonModels, staticContainerIds));
        var controlVariableNameById = BuildUniqueViewVariableNames(selectedSupportedControls, task, allTasks, dataObjects, staticContainerIds);
        var tableAttachmentByLeaf = new Dictionary<int, int>();
        var columnAttachmentByLeaf = new Dictionary<int, int>();
        var groupBoxBindingByControlId = new Dictionary<int, int>();
        var tableColumnStartXByTableId = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var table in tableControls)
        {
            var cols = tableColumnControls
                .Where(c => c.ParentId == table.Id)
                .OrderBy(c => c.ControlLayer ?? int.MaxValue)
                .ThenBy(c => c.Id)
                .ToList();
            var startsByColumn = new Dictionary<int, int>();
            var cursorX = table.X;
            foreach (var col in cols)
            {
                startsByColumn[col.Id] = cursorX;
                cursorX += Math.Max(10, col.Width);
            }
            tableColumnStartXByTableId[table.Id] = startsByColumn;

            var columnByLayer = cols
                .Where(c => c.ControlLayer.HasValue)
                .GroupBy(c => c.ControlLayer!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var leaf in leafControls.Where(x => x.ParentId == table.Id))
            {
                if (leaf.ControlLayer.HasValue && columnByLayer.TryGetValue(leaf.ControlLayer.Value, out var col))
                    columnAttachmentByLeaf[leaf.Id] = col.Id;
                else
                    tableAttachmentByLeaf[leaf.Id] = table.Id;
            }
        }

        foreach (var c in selectedSupportedControls.Where(x =>
                     IsLeafViewControl(x) &&
                     x.ParentId.HasValue &&
                     tableColumnControlIds.Contains(x.ParentId.Value)))
            columnAttachmentByLeaf[c.Id] = c.ParentId!.Value;

        var columnChildIdsByColumn = tableColumnControls.ToDictionary(
            col => col.Id,
            col => (IReadOnlyList<int>)leafControls
                .Where(x => columnAttachmentByLeaf.TryGetValue(x.Id, out var parentColId) && parentColId == col.Id)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToList());
        var tableChildIdsByTable = tableControls.ToDictionary(
            table => table.Id,
            table =>
            {
                var orderedIds = new List<int>();
                orderedIds.AddRange(tableColumnControls
                    .Where(x => x.ParentId == table.Id)
                    .OrderBy(x => x.ControlLayer ?? int.MaxValue)
                    .ThenBy(x => x.Id)
                    .Select(x => x.Id));
                orderedIds.AddRange(leafControls
                    .Where(x => tableAttachmentByLeaf.TryGetValue(x.Id, out var parentTableId) && parentTableId == table.Id)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id));
                return (IReadOnlyList<int>)orderedIds;
            });
        var rootControlIds = selectedSupportedControls
            .Where(c => !IsTableColumnViewControl(c) && !columnAttachmentByLeaf.ContainsKey(c.Id) && !tableAttachmentByLeaf.ContainsKey(c.Id))
            .OrderBy(c => c.TabOrder ?? c.TabbingOrder ?? int.MaxValue)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToList();

        foreach (var c in selectedSupportedControls)
        {
            if (IsStaticGroupBoxLike(c, staticContainerIds) || IsStaticShapeLike(c, staticContainerIds))
                continue;
            if (!string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tableAttachmentByLeaf.ContainsKey(c.Id) || columnAttachmentByLeaf.ContainsKey(c.Id))
                continue;

            TaskFormControlDef? best = null;
            var cRight = c.X + Math.Max(1, c.Width);
            var cBottom = c.Y + Math.Max(1, c.Height);
            foreach (var g in containerControls)
            {
                var gRight = g.X + Math.Max(1, g.Width);
                var gBottom = g.Y + Math.Max(1, g.Height);
                var inside = c.X >= g.X && c.Y >= g.Y && cRight <= gRight && cBottom <= gBottom;
                if (!inside)
                    continue;
                if (best is null)
                {
                    best = g;
                    continue;
                }
                var bestArea = Math.Max(1, best.Width) * Math.Max(1, best.Height);
                var currentArea = Math.Max(1, g.Width) * Math.Max(1, g.Height);
                if (currentArea < bestArea)
                    best = g;
            }
            if (best is not null)
                groupBoxBindingByControlId[c.Id] = best.Id;
        }
        var bindingExpressionIds = task.FormEntries
            .SelectMany(fe => fe.Form.Controls)
            .SelectMany(c =>
            {
                var list = new List<int>(8);
                if (c.DataExpressionId.HasValue)
                    list.Add(c.DataExpressionId.Value);
                if (c.VisibleExpressionId.HasValue)
                    list.Add(c.VisibleExpressionId.Value);
                if (c.EnabledExpressionId.HasValue)
                    list.Add(c.EnabledExpressionId.Value);
                if (c.ToolTipExpressionId.HasValue)
                    list.Add(c.ToolTipExpressionId.Value);
                list.AddRange(c.PropertyExpressionIds);
                return list;
            })
            .Concat(task.FormEntries
                .SelectMany(fe => new[] { fe.Form.XExpressionId, fe.Form.YExpressionId })
                .Where(x => x.HasValue)
                .Select(x => x!.Value))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var formTextExpressionIds = task.FormEntries
            .Select(fe => fe.Form.FormTextExpressionId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var selectedFormTextExpressionId = selectedForm?.FormTextExpressionId;
        if (!selectedFormTextExpressionId.HasValue && formTextExpressionIds.Count > 0)
            selectedFormTextExpressionId = formTextExpressionIds[0];
        var systemRaiseEnabledExpressionByKeyCombinationId = task.FormEntries
            .SelectMany(fe => fe.Form.Controls)
            .Where(control =>
                string.Equals(control.RaiseEventType, "S", StringComparison.OrdinalIgnoreCase) &&
                control.RaiseEventKeyCombinationId.HasValue &&
                control.EnabledExpressionId.HasValue)
            .GroupBy(control => control.RaiseEventKeyCombinationId!.Value)
            .ToDictionary(g => g.Key, g => g.First().EnabledExpressionId!.Value);
        var clickHandlers = BuildViewClickHandlers(task, buttonModels, controlVariableNameById);
        var booleanBindings = BuildViewBooleanBindings(task);
        var subformBindings = BuildViewSubformBindings(task, allTasks);
        var bindListHandlers = BuildViewBindListHandlers(task, dataObjects, controlVariableNameById);
        return new ViewSemantic(
            shouldGenerate,
            baseClassName,
            className,
            selectedFormEntryIndex,
            selectedFormEntry,
            selectedForm,
            selectedForm?.FormText,
            selectedFormTextExpressionId,
            selectedForm?.Width ?? 320,
            selectedForm?.Height ?? 200,
            selectedForm?.ColorSchemeId,
            selectedForm?.FontSchemeId,
            selectedFormControls,
            selectedSupportedControls,
            selectedUnsupportedControls,
            staticContainerIds,
            tableControls,
            tableColumnControls,
            leafControls,
            containerControls,
            controlTypeNameById,
            controlVariableNameById,
            tableAttachmentByLeaf,
            columnAttachmentByLeaf,
            columnChildIdsByColumn,
            tableChildIdsByTable,
            rootControlIds,
            groupBoxBindingByControlId,
            tableColumnStartXByTableId,
            bindingExpressionIds,
            formTextExpressionIds,
            systemRaiseEnabledExpressionByKeyCombinationId,
            clickHandlers,
            booleanBindings,
            subformBindings,
            bindListHandlers);
    }

    private static Dictionary<int, string> BuildUniqueViewVariableNames(
        IReadOnlyList<TaskFormControlDef> controls,
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlySet<int>? staticContainerIds = null)
    {
        var map = new Dictionary<int, string>();
        var baseNameByControlId = controls.ToDictionary(
            c => c.Id,
            c => ResolvePreferredViewControlVariableBaseName(c, task, allTasks, dataObjects, staticContainerIds));
        var baseGroups = controls
            .GroupBy(c => baseNameByControlId[c.Id], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sequenceByBase = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var c in controls)
        {
            var baseName = baseNameByControlId[c.Id];
            var hasCollision = baseGroups.TryGetValue(baseName, out var same) && same.Count > 1;
            var candidate = baseName;
            if (hasCollision)
            {
                var seq = sequenceByBase.TryGetValue(baseName, out var current) ? current + 1 : 1;
                sequenceByBase[baseName] = seq;
                candidate = seq switch
                {
                    1 => baseName,
                    2 => baseName + "_",
                    _ => baseName + "_" + (seq - 1).ToString()
                };
            }
            while (!used.Add(candidate))
                candidate += "_";
            map[c.Id] = candidate;
        }

        return map;
    }

    private static string ResolvePreferredViewControlVariableBaseName(
        TaskFormControlDef c,
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlySet<int>? staticContainerIds = null)
    {
        var fallback = ResolveViewControlVariableBaseName(c, staticContainerIds);
        if (IsStaticGroupBoxLike(c, staticContainerIds) || IsStaticShapeLike(c, staticContainerIds))
            return fallback;

        string PrefixFor(TaskFormControlDef ctrl) => ctrl.Model switch
        {
            "CTRL_GUI0_TABLE" => "grd",
            "CTRL_GUI0_COLUMN" => "gcl",
            "CTRL_GUI0_STATIC" => "lbl",
            "CTRL_GUI0_EDIT" => "txt",
            "CTRL_RICH_CLIENT_EDIT" => "txt",
            "CTRL_BROWSER_EDIT" => "txt",
            "CTRL_GUI0_COMBOBOX" => "cbo",
            "CTRL_RICH_CLIENT_COMBOBOX" => "cbo",
            "CTRL_BROWSER_COMBOBOX" => "cbo",
            "CTRL_GUI0_PUSH_BUTTON" => "btn",
            "CTRL_GUI0_SUBFORM" => "SubForm",
            "CTRL_GUI0_CHECKBOX" => "chk",
            "CTRL_RICH_CLIENT_CHECKBOX" => "chk",
            "CTRL_GUI0_TAB" => "tab",
            "CTRL_GUI0_TREE" => "tre",
            "CTRL_GUI0_RADIO" => "rad",
            "CTRL_GUI0_IMAGE" => "pic",
            "CTRL_GUI1_IMAGE" => "pic",
            "CTRL_GUI0_LISTBOX" => "lst",
            "CTRL_GUI0_RICH_EDIT" => "rtx",
            _ => "txt"
        };

        if (string.IsNullOrWhiteSpace(c.ControlName) && c.Model == "CTRL_GUI0_STATIC" && !string.IsNullOrWhiteSpace(c.Text))
        {
            var labelName = ToLabelIdentifier(c.Text.Trim(), c.Id);
            if (!string.IsNullOrWhiteSpace(labelName) && !labelName.Equals("Unnamed", StringComparison.OrdinalIgnoreCase))
                return "lbl" + labelName;
        }

        var generatedControlName = IsLikelyGeneratedControlName(c.ControlName);
        if (string.IsNullOrWhiteSpace(c.ControlName) || generatedControlName)
        {
            if (c.DataExpressionId.HasValue && IsViewEditControl(c))
                return PrefixFor(c) + "Exp_" + c.DataExpressionId.Value;

            var dataExpr = ResolveViewControlDataExpressionForNaming(c, task, allTasks);
            if (!string.IsNullOrWhiteSpace(dataExpr) && IsSimpleMemberAccess(dataExpr))
            {
                var member = dataExpr.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(member))
                    return PrefixFor(c) + ToPascalIdentifier(member);
            }
        }

        return fallback;
    }

    private static string ResolveViewControlVariableBaseName(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (IsStaticGroupBoxLike(c, staticContainerIds))
        {
            var grpSuffix = !string.IsNullOrWhiteSpace(c.ControlName)
                ? ToPascalIdentifier(c.ControlName)
                : !string.IsNullOrWhiteSpace(c.Text) && !c.Text.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase)
                    ? ToPascalIdentifier(c.Text)
                    : "";
            return "grp" + grpSuffix;
        }

        var suffix = !string.IsNullOrWhiteSpace(c.ControlName)
            ? ToPascalIdentifier(c.ControlName)
            : !string.IsNullOrWhiteSpace(c.ColumnTitle)
                ? ToPascalIdentifier(c.ColumnTitle)
                : c.Id.ToString();
        if (string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase) &&
            IsStaticShapeLike(c, staticContainerIds) &&
            string.IsNullOrWhiteSpace(c.ControlName))
            return "shp";
        return c.Model switch
        {
            "CTRL_GUI0_TABLE" => c.Id == 1 ? "grd" : "grd" + suffix,
            "CTRL_GUI0_COLUMN" => "gcl" + suffix,
            "CTRL_GUI0_STATIC" => IsStaticShapeLike(c, staticContainerIds) ? "shp" + suffix : "lbl" + suffix,
            "CTRL_GUI0_EDIT" => "txt" + suffix,
            "CTRL_RICH_CLIENT_EDIT" => "txt" + suffix,
            "CTRL_BROWSER_EDIT" => "txt" + suffix,
            "CTRL_GUI0_COMBOBOX" => "cbo" + suffix,
            "CTRL_RICH_CLIENT_COMBOBOX" => "cbo" + suffix,
            "CTRL_BROWSER_COMBOBOX" => "cbo" + suffix,
            "CTRL_GUI0_PUSH_BUTTON" => "btn" + suffix,
            "CTRL_GUI0_SUBFORM" => "SubForm" + suffix,
            "CTRL_GUI0_CHECKBOX" => "chk" + suffix,
            "CTRL_RICH_CLIENT_CHECKBOX" => "chk" + suffix,
            "CTRL_GUI0_TAB" => "tab" + suffix,
            "CTRL_GUI0_TREE" => "tre" + suffix,
            "CTRL_GUI0_RADIO" => "rad" + suffix,
            "CTRL_GUI0_IMAGE" => "pic" + suffix,
            "CTRL_GUI1_IMAGE" => "pic" + suffix,
            "CTRL_GUI0_LISTBOX" => "lst" + suffix,
            "CTRL_GUI0_RICH_EDIT" => "rtx" + suffix,
            _ => "txt" + suffix
        };
    }

    private static string ToLabelIdentifier(string raw, int controlId)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Static" + controlId;

        var source = raw.Trim();
        var isRtf = source.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase);
        if (isRtf)
            return "Rtf" + controlId;

        var normalized = Regex.Replace(source, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
            return "Static" + controlId;

        var tokens = normalized
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Take(4)
            .ToArray();
        if (tokens.Length == 0)
            return "Static" + controlId;

        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            if (Regex.IsMatch(token, @"^\d+$"))
                sb.Append('_').Append(token);
            else
                sb.Append(char.ToUpperInvariant(token[0])).Append(token[1..]);
        }

        var result = sb.ToString();
        if (result.Length > 40)
            result = result.Substring(0, 40).TrimEnd('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "Static" + controlId;
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    private static string ResolveViewControlDataExpressionForNaming(TaskFormControlDef c, TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            var sel = task.Selects.FirstOrDefault(s => string.Equals(s.Name, c.DataColumn, StringComparison.OrdinalIgnoreCase));
            if (sel is not null)
            {
                var resource = task.ResourceColumns.FirstOrDefault(r => r.Id == sel.ColumnId);
                if (resource is not null)
                    return "_controller." + ToLegacyVariableName(resource.Name);
            }

            var ordinalExpr = ResolveDataColumnOrdinalBindingForNaming(c.DataColumn, task, allTasks);
            if (!string.IsNullOrWhiteSpace(ordinalExpr))
                return ordinalExpr;
        }

        if (c.DataExpressionId.HasValue)
            return $"_controller.Exp_{c.DataExpressionId.Value}()";
        return "";
    }

    private static string ResolveDataColumnOrdinalBindingForNaming(string? dataColumn, TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        if (string.IsNullOrWhiteSpace(dataColumn))
            return "";
        var token = dataColumn.Trim();
        if (!int.TryParse(token, out var taskColumnIndex) || taskColumnIndex <= 0)
            return "";
        if (taskColumnIndex <= task.ResourceColumns.Count)
        {
            var rc = task.ResourceColumns[taskColumnIndex - 1];
            return "_controller." + ToLegacyVariableName(rc.Name);
        }
        var remaining = taskColumnIndex - task.ResourceColumns.Count;
        var parentTask = task.ParentOrdinal.HasValue ? GetTaskByOrdinal(task.ParentOrdinal.Value) : null;
        var visitedParents = new HashSet<int> { task.Ordinal };
        while (parentTask is not null)
        {
            if (!visitedParents.Add(parentTask.Ordinal))
            {
                ConversionTelemetry.Log("SEMANTIC", $"parent cycle detected context=data-column-binding taskOrdinal={task.Ordinal} parentOrdinal={parentTask.Ordinal}");
                break;
            }

            if (remaining <= parentTask.ResourceColumns.Count)
            {
                var prc = parentTask.ResourceColumns[remaining - 1];
                return "_controller." + ToLegacyVariableName(prc.Name);
            }
            remaining -= parentTask.ResourceColumns.Count;
            parentTask = parentTask.ParentOrdinal.HasValue ? GetTaskByOrdinal(parentTask.ParentOrdinal.Value) : null;
        }
        return "";
    }

    private static bool IsSimpleMemberAccess(string? expr)
    {
        return !string.IsNullOrWhiteSpace(expr) && Regex.IsMatch(expr, @"^[A-Za-z_][A-Za-z0-9_\.]*$");
    }

    private static bool IsLikelyGeneratedControlName(string? controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName))
            return false;
        return Regex.IsMatch(controlName, @"(_\d{3,}|\d{4,}|^Static\d*$|^Shape\d*$|^Label\d*$)", RegexOptions.IgnoreCase);
    }

    private static SelectSemantic BuildSelectSemantic(TaskDef task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var items = task.Selects.ToList();
        var nameToExpression = BuildSelectNameToExpressionMap(task, dataObjects);
        var itemsByName = task.Selects
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var parameterByColumnId = task.Selects
            .Where(s => s.IsParameter)
            .GroupBy(s => s.ColumnId)
            .ToDictionary(g => g.Key, g => g.First());
        return new SelectSemantic(items, nameToExpression, itemsByName, parameterByColumnId);
    }

    private static ResourceSemantic BuildResourceSemantic(TaskDef task)
    {
        var ordered = task.ResourceColumns.ToList();
        var byId = task.ResourceColumns
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var byName = task.ResourceColumns
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byLegacyName = task.ResourceColumns
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => ResolveTaskResourceMemberName(task, c), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var firstBlob = task.ResourceColumns
            .FirstOrDefault(c => string.Equals(c.AttrObj, "FIELD_BLOB", StringComparison.OrdinalIgnoreCase));
        return new ResourceSemantic(ordered, byId, byName, byLegacyName, firstBlob);
    }

    private static Dictionary<string, string> BuildApplicationSelectMap(IReadOnlyList<TaskSemantic> tasks)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appTask = tasks.FirstOrDefault(t => t.MainProgram) ?? tasks.FirstOrDefault(t => t.ParentOrdinal is null);
        if (appTask is null)
            return result;
        foreach (var kv in appTask.SelectsSemantic.NameToExpression)
        {
            if (IsCounterSelectBinding(kv.Key, kv.Value))
                continue;
            result[kv.Key] = $"Application.Instance.{kv.Value}";
        }
        return result;

        static bool IsCounterSelectBinding(string key, string value)
            => string.Equals(key, "Counter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "Counter_", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Counter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Counter_", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildParentSelectMap(
        TaskSemantic task,
        IReadOnlyDictionary<int, TaskSemantic> allTasksByOrdinal,
        IReadOnlyDictionary<string, string> applicationSelectMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var local = task.SelectsSemantic.NameToExpression;
        var parent = task.ParentOrdinal.HasValue
            ? allTasksByOrdinal.GetValueOrDefault(task.ParentOrdinal.Value)
            : null;
        var visited = new HashSet<int>();
        var depth = 1;
        while (parent is not null && visited.Add(parent.Ordinal))
        {
            var prefix = string.Concat(Enumerable.Repeat("_parent.", depth));
            foreach (var kv in parent.SelectsSemantic.NameToExpression.OrderByDescending(k => k.Key.Length))
            {
                if (local.ContainsKey(kv.Key))
                    continue;
                if (result.ContainsKey(kv.Key))
                    continue;
                var prefixed = kv.Value.StartsWith("Application.", StringComparison.Ordinal) ? kv.Value : prefix + kv.Value;
                result[kv.Key] = prefixed;
            }

            parent = parent.ParentOrdinal.HasValue
                ? allTasksByOrdinal.GetValueOrDefault(parent.ParentOrdinal.Value)
                : null;
            depth++;
        }
        return result;
    }

    private static string ToRoleMemberIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Role_";
        var sb = new StringBuilder();
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var id = sb.ToString();
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    private static ReportSemantic BuildReportSemantic(TaskDef task, IOSemantic io, EventSemantic events, ExpressionSemantic expressions)
    {
        return new ReportSemantic(
            io.HasPrinterWriter || io.HasTextPrinterWriter,
            events.HasPageHeader,
            events.HasPageFooter,
            task.FormIos.Any(x => x.Level == "R" || x.Type == "D"),
            task.Handlers.Any(h => h.Raises.Any(r => string.Equals(r.EventType, "Page", StringComparison.OrdinalIgnoreCase))),
            expressions.HasEop,
            expressions.HasIoCurr);
    }

    private static IReadOnlyList<UnhandledSemantic> BuildUnhandled(TaskDef task)
    {
        var result = new List<UnhandledSemantic>();

        foreach (var gap in task.Gaps)
            result.Add(new UnhandledSemantic("Gap", BuildRaw(gap.Scope, gap.Message, gap.XmlTrace)));

        var handledFunctionParameterSelectNames = task.FunctionOverrides
            .SelectMany(f => f.Parameters)
            .Select(p => p.SelectName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Range/Locate/parameter selects are handled by the data-view writer paths.
        // When a specific shape is still unsupported, the writer emits a focused GAP
        // comment near the generated Where/StartOnRowWhere code. Emitting a generic
        // "Unhandled Select" here creates noisy false positives in the diff report.
        foreach (var select in task.Selects.Where(s =>
                     !s.IsFunctionSelect &&
                     !string.IsNullOrWhiteSpace(s.RealVarName) &&
                     !handledFunctionParameterSelectNames.Contains(s.Name) &&
                     HasUnhandledSelectSemantic(s)))
            result.Add(new UnhandledSemantic("Select", BuildRaw(select.Name, $"Type={select.Type}; Column={select.ColumnId}; Range={select.HasRange}; Locate={select.HasLocate}; Parameter={select.IsParameter}; Function={select.IsFunctionSelect}", select.XmlTrace)));

        foreach (var expr in task.Expressions.Where(e => ContainsSemanticSignalBeyondCoverage(e.Syntax)))
            result.Add(new UnhandledSemantic("Expression", BuildRaw(expr.Ordinal.ToString(), expr.Syntax, null)));

        return result;
    }

    private static bool ContainsSemanticSignalBeyondCoverage(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;

        var trimmed = syntax.Trim();
        if (TryParseWholeXpaSingleQuotedLiteral(trimmed, out _))
            return false;

        return Regex.IsMatch(trimmed, @"\bEOP\s*\(", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmed, @"\bIOCurr\s*\(", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmed, @"\bPage\s*\(", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmed, @"\bLine\s*\(", RegexOptions.IgnoreCase)
            || trimmed.Contains("u.", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(trimmed, @"\bMerge\s*\(", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmed, @"\bPrint\s*\(", RegexOptions.IgnoreCase);
    }

    private static bool HasUnhandledSelectSemantic(TaskLogicSelectDef select)
    {
        if (select.IsParameter || select.HasRange || select.HasLocate)
            return false;

        return false;
    }

    private static bool TryParseWholeXpaSingleQuotedLiteral(string expr, out string literal)
    {
        literal = string.Empty;
        if (expr.Length < 2 || expr[0] != '\'' || expr[^1] != '\'')
            return false;

        var sb = new StringBuilder();
        for (var i = 1; i < expr.Length - 1; i++)
        {
            var ch = expr[i];
            if (ch == '\'')
            {
                if (i + 1 < expr.Length - 1 && expr[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }

                return false;
            }

            sb.Append(ch);
        }

        literal = sb.ToString();
        return true;
    }

    private static bool HasEvent(TaskDef task, string description)
    {
        return task.Events.Any(e => string.Equals(e.Description, description, StringComparison.OrdinalIgnoreCase))
            || task.Handlers.Any(h => string.Equals(h.EventPublicObject, description, StringComparison.OrdinalIgnoreCase));
    }

    private static HandlerBodySemantic BuildHandlerBodySemantic(TaskDef task, TaskHandlerDef handler)
    {
        var orderedActions = handler.Actions.Count > 0
            ? handler.Actions
                .OrderBy(a => ExtractLogicLineOrder(a.XmlTrace) ?? int.MaxValue)
                .ToList()
            : BuildSyntheticHandlerActions(handler)
                .OrderBy(a => ExtractLogicLineOrder(a.XmlTrace) ?? int.MaxValue)
                .ToList();
        var blocks = BuildActionBlocks(task, orderedActions);
        return new HandlerBodySemantic(handler, orderedActions, blocks);
    }

    private static IReadOnlyList<TaskRowActionDef> BuildSyntheticHandlerActions(TaskHandlerDef handler)
    {
        var actions = new List<TaskRowActionDef>();

        actions.AddRange(handler.Calls.Select(c =>
            new TaskRowActionDef("Call", c, null, null, null, null, null, c.ConditionExpressionId, null, null, c.XmlTrace, null)));
        actions.AddRange(handler.Updates.Select(u =>
            new TaskRowActionDef("Update", null, u, null, null, null, null, u.ConditionExpressionId, null, null, u.XmlTrace, null)));
        actions.AddRange(handler.Stops.Select(s =>
            new TaskRowActionDef("Stop", null, null, s, null, null, null, s.ConditionExpressionId, null, null, s.XmlTrace, null)));
        actions.AddRange(handler.Invokes.Select(i =>
            new TaskRowActionDef("Invoke", null, null, null, i, null, null, i.ConditionExpressionId, null, null, i.XmlTrace, null)));
        actions.AddRange(handler.Remarks.Select(r =>
            new TaskRowActionDef("Remark", null, null, null, null, null, null, null, null, null, r.XmlTrace, r.Text)));

        return actions;
    }

    private static IReadOnlyList<HandlerActionBlockSemantic> BuildActionBlocks(TaskDef task, IReadOnlyList<TaskRowActionDef> orderedActions)
    {
        var blocks = new List<HandlerActionBlockSemantic>();

        for (var i = 0; i < orderedActions.Count; i++)
        {
            var action = orderedActions[i];
            if (action.LoopConditionExpressionId.HasValue)
            {
                var loopCondKey = NormalizeConditionKey(task.Expressions.FirstOrDefault(e => e.Ordinal == action.LoopConditionExpressionId.Value)?.Syntax ?? "");
                var length = 1;
                for (var j = i + 1; j < orderedActions.Count; j++)
                {
                    if (orderedActions[j].LoopConditionExpressionId != action.LoopConditionExpressionId)
                        break;
                    length++;
                }
                blocks.Add(new HandlerActionBlockSemantic("Loop", i, length, loopCondKey));
                i += length - 1;
                continue;
            }
            var conditionKey = ResolveActionConditionKey(task, action);
            var isLoop = !string.IsNullOrWhiteSpace(conditionKey) && conditionKey.Contains("LoopCounter(", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(conditionKey) && !isLoop && i + 1 < orderedActions.Count)
            {
                var length = 1;
                for (var j = i + 1; j < orderedActions.Count; j++)
                {
                    var nextConditionKey = ResolveActionConditionKey(task, orderedActions[j]);
                    if (!string.Equals(conditionKey, nextConditionKey, StringComparison.Ordinal))
                        break;
                    length++;
                }

                if (length > 1)
                {
                    blocks.Add(new HandlerActionBlockSemantic("GroupedCondition", i, length, conditionKey));
                    i += length - 1;
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(conditionKey) && !isLoop && i + 1 < orderedActions.Count)
            {
                var nextConditionKey = ResolveActionConditionKey(task, orderedActions[i + 1]);
                if (IsComplementaryConditionKey(conditionKey, nextConditionKey))
                {
                    blocks.Add(new HandlerActionBlockSemantic("ComplementaryPair", i, 2, conditionKey));
                    i++;
                    continue;
                }
            }

            blocks.Add(new HandlerActionBlockSemantic("Single", i, 1, conditionKey));
        }

        return blocks;
    }

    private static bool IsValueChangedHandler(TaskHandlerDef handler)
    {
        return string.Equals(handler.Level, "V", StringComparison.OrdinalIgnoreCase)
            && string.Equals(handler.Type, "C", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(handler.Reference);
    }

    private static bool IsControlEventHandler(TaskHandlerDef handler)
    {
        return string.Equals(handler.Level, "C", StringComparison.OrdinalIgnoreCase)
            && handler.Type is "P" or "S" or "V"
            && !string.IsNullOrWhiteSpace(handler.Reference);
    }

    private static int? ExtractLogicLineOrder(string? xmlTrace)
    {
        if (string.IsNullOrWhiteSpace(xmlTrace))
            return null;
        var marker = "llLine=";
        var start = xmlTrace.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = start;
        while (end < xmlTrace.Length && char.IsDigit(xmlTrace[end]))
            end++;
        return int.TryParse(xmlTrace[start..end], out var line) ? line : null;
    }

    private static string ResolveActionConditionKey(TaskDef task, TaskRowActionDef action)
    {
        var condId = action.ConditionExpressionId
            ?? action.Call?.ConditionExpressionId
            ?? action.Update?.ConditionExpressionId
            ?? action.Stop?.ConditionExpressionId
            ?? action.Invoke?.ConditionExpressionId;
        if (!condId.HasValue)
            return "";
        var syntax = task.Expressions.FirstOrDefault(e => e.Ordinal == condId.Value)?.Syntax ?? "";
        return NormalizeConditionKey(syntax);
    }

    private static bool IsComplementaryConditionKey(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;
        return second == $"Not({first})" || first == $"Not({second})";
    }

    private static string NormalizeConditionKey(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return "";
        var compact = new string(condition.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return compact.Replace("u.Not(", "Not(", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStringLiteralSyntax(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return false;
        var trimmed = syntax.Trim();
        if (trimmed.Length < 2)
            return false;
        if (trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
            return true;
        return TryParseWholeXpaSingleQuotedLiteral(trimmed, out _);
    }

    private static bool ContainsToken(string syntax, string token)
    {
        return !string.IsNullOrWhiteSpace(syntax) &&
               syntax.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLegacyVariableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";
        var trimmed = value.Trim();
        var id = new StringBuilder();
        var capitalizeNext = false;
        var pendingUnderscore = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingUnderscore)
                {
                    if (id.Length > 0 && id[^1] != '_')
                        id.Append('_');
                    pendingUnderscore = false;
                }

                if (capitalizeNext && char.IsLetter(ch))
                    id.Append(char.ToUpperInvariant(ch));
                else
                    id.Append(ch);

                capitalizeNext = false;
                continue;
            }

            if (ch == '.')
            {
                pendingUnderscore = id.Length > 0;
                capitalizeNext = false;
                continue;
            }

            if (ch == '_')
            {
                if (id.Length > 0 && id[^1] != '_')
                    id.Append('_');
                pendingUnderscore = false;
                capitalizeNext = false;
                continue;
            }

            capitalizeNext = id.Length > 0;
        }

        var result = id.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "_";
        if (trimmed.Length > 0 && !char.IsLetterOrDigit(trimmed[0]))
            result = "_" + result;
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    private static string ResolveTaskResourceMemberName(TaskDef task, TaskResourceColumnDef resource)
    {
        var baseName = ToLegacyVariableName(resource.Name);
        if (SemanticTaskResourceReservedNames.Contains(baseName))
            baseName += "_";
        var ordered = task.ResourceColumns
            .Where(r =>
            {
                var candidate = ToLegacyVariableName(r.Name);
                if (SemanticTaskResourceReservedNames.Contains(candidate))
                    candidate += "_";
                return string.Equals(candidate, baseName, StringComparison.Ordinal);
            })
            .ToList();
        if (ordered.Count <= 1)
            return baseName;

        var index = ordered.FindIndex(r => r.Id == resource.Id);
        if (index <= 0)
            return baseName;
        if (index == 1)
            return baseName + "_";
        return $"{baseName}_{index}";
    }

    private static readonly HashSet<string> SemanticTaskResourceReservedNames = new(StringComparer.Ordinal)
    {
        "TaskID",
        "Title",
        "From",
        "View",
        "OrderBy",
        "Activity",
        "Counter"
    };

    private static string ToCodeIdentifierPreservingCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";
        var sanitized = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        sanitized = Regex.Replace(sanitized, "_{2,}", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "_";
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;
        return sanitized;
    }

    private static string ToEntityTypeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unnamed";
        var trimmed = raw.Trim();
        var isUpperUnderscoreStyle = trimmed.ToUpperInvariant() == trimmed && Regex.IsMatch(trimmed, "[A-Z]");
        if (!isUpperUnderscoreStyle)
            return ToPascalIdentifier(trimmed);
        var id = Regex.Replace(trimmed, "[^A-Za-z0-9_]+", "_");
        if (string.IsNullOrWhiteSpace(id))
            id = "Unnamed";
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    private static string ResolveDataObjectTypeName(DataObjectDef d, IReadOnlyList<DataObjectDef> allDataObjects)
    {
        if (!string.IsNullOrWhiteSpace(d.TypeNameOverride))
            return d.TypeNameOverride!;

        var baseName = ToEntityTypeName(d.Name);
        var hasCollision = allDataObjects.Any(other =>
            other.Ordinal != d.Ordinal &&
            string.Equals(other.Name, d.Name, StringComparison.OrdinalIgnoreCase));
        if (!hasCollision)
            return baseName;

        var physicalStem = Path.GetFileNameWithoutExtension(d.PhysicalName ?? "");
        var suffix = ToEntityTypeName(physicalStem);
        if (string.IsNullOrWhiteSpace(suffix) || string.Equals(suffix, baseName, StringComparison.OrdinalIgnoreCase))
            suffix = "Obj" + d.Ordinal.ToString(CultureInfo.InvariantCulture);
        return baseName + "_" + suffix;
    }

    private static string NormalizeKey(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static string? ResolveWholeExpressionSemanticKind(string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
            return null;

        var compactSource = new string(syntax.Where(ch => !char.IsWhiteSpace(ch)).ToArray())
            .ToLowerInvariant();

        return compactSource switch
        {
            "eop()" => "EOP",
            "u.eop()" => "EOP",
            "iocurr()" => "IOCurr",
            "u.iocurr()" => "IOCurr",
            "page()" => "Page",
            "u.page()" => "Page",
            "line()" => "Line",
            "u.line()" => "Line",
            _ => null
        };
    }

    private static string ResolveIoKind(TaskDef task)
    {
        if (task.FormEntries.Any(x => x.Model == "FORM_MERGE"))
            return "FileWriter";
        if (string.Equals(task.Io?.Access, "R", StringComparison.OrdinalIgnoreCase))
            return "FileReader";
        if (string.Equals(task.Io?.Access, "W", StringComparison.OrdinalIgnoreCase))
            return "FileWriter";
        if (task.FormIos.Any(x => string.Equals(x.OperationType, "R", StringComparison.OrdinalIgnoreCase)))
            return "FileWriter";
        if (task.Io?.Media?.Contains("PDF", StringComparison.OrdinalIgnoreCase) == true)
            return "PrinterWriter";
        if (task.AllowPrintingData)
            return "PrinterWriter";
        return "TextPrinterWriter";
    }

    private static string? ResolveActivityByInitialMode(string initialMode)
    {
        return initialMode switch
        {
            "M" => null,
            "C" => "Activities.Insert",
            "D" => "Activities.Delete",
            "E" => "Activities.Browse",
            _ => null
        };
    }

    private static string BuildPrintLayoutClassName(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        var printForms = task.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_GUI1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(fe => fe.Index)
            .ToList();
        var printForm = printForms.FirstOrDefault();
        var componentIndex = task.ParentOrdinal.HasValue && task.SubtaskIndex.HasValue
            ? task.SubtaskIndex.Value + 1
            : printForm?.ClassIndex ?? printForm?.Index ?? task.SubtaskIndex.GetValueOrDefault(1);
        if (componentIndex < 1)
            componentIndex = 1;

        if (!task.ParentOrdinal.HasValue)
            return ToTaskClassName(task.Description) + $"C{componentIndex}";

        var root = task;
        var visitedParents = new HashSet<int> { root.Ordinal };
        while (root.ParentOrdinal.HasValue)
        {
            if (!visitedParents.Add(root.ParentOrdinal.Value))
            {
                ConversionTelemetry.Log("SEMANTIC", $"parent cycle detected context=print-layout-class taskOrdinal={task.Ordinal} parentOrdinal={root.ParentOrdinal.Value}");
                break;
            }

            var parent = GetTaskByOrdinal(root.ParentOrdinal.Value);
            if (parent is null)
                break;
            root = parent;
        }

        var suffix = ToTaskClassName(task.Description).TrimStart('_');
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = "Task";
        return $"{ToTaskClassName(root.Description)}C{componentIndex}_{suffix}";
    }

    private static string BuildTextIoLayoutClassName(TaskDef task, IReadOnlyList<TaskDef> allTasks, TaskFormEntryDef? preferredTextForm = null)
    {
        var textForms = task.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_TEXT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(fe => fe.Index)
            .ToList();
        var referencedFormIndexes = task.FormIos
            .Where(io => io.FormEntryIndex.HasValue)
            .Select(io => io.FormEntryIndex!.Value)
            .Distinct()
            .ToHashSet();
        var textForm = preferredTextForm
            ?? textForms.FirstOrDefault(fe => referencedFormIndexes.Contains(fe.Index))
            ?? textForms.FirstOrDefault();
        var componentIndex = textForm?.ClassIndex ?? textForm?.Index ?? task.SubtaskIndex.GetValueOrDefault(1);
        if (componentIndex < 1)
            componentIndex = 1;

        if (!task.ParentOrdinal.HasValue)
            return ToTextIoTaskNameToken(task) + $"C{componentIndex}";

        var root = task;
        var visitedParents = new HashSet<int> { root.Ordinal };
        while (root.ParentOrdinal.HasValue)
        {
            if (!visitedParents.Add(root.ParentOrdinal.Value))
            {
                ConversionTelemetry.Log("SEMANTIC", $"parent cycle detected context=text-io-layout-class taskOrdinal={task.Ordinal} parentOrdinal={root.ParentOrdinal.Value}");
                break;
            }

            var parent = GetTaskByOrdinal(root.ParentOrdinal.Value);
            if (parent is null)
                break;
            root = parent;
        }
        var suffix = ToTextIoTaskNameToken(task).TrimStart('_');
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = "Task";
        return $"{ToTextIoTaskNameToken(root)}C{componentIndex}_{suffix}";
    }

    private static string ToTextIoTaskNameToken(TaskDef task)
    {
        var descToken = ToTaskClassName(task.Description);
        var usePublicName = !string.IsNullOrWhiteSpace(task.PublicName);
        if (usePublicName && Regex.IsMatch(task.PublicName!.Trim(), @"^[A-Za-z]+\d+$"))
            usePublicName = false;
        if (usePublicName)
        {
            var publicTrimmed = task.PublicName!.Trim();
            var publicLooksLikeShortCode = Regex.IsMatch(publicTrimmed, @"^[A-Za-z]{1,6}\d+[A-Za-z]?$", RegexOptions.IgnoreCase);
            var descHasMoreMeaning = descToken.StartsWith(publicTrimmed, StringComparison.OrdinalIgnoreCase)
                                     && descToken.Length > publicTrimmed.Length;
            if (publicLooksLikeShortCode && descHasMoreMeaning)
                usePublicName = false;
        }
        var raw = usePublicName ? task.PublicName!.Trim() : descToken;
        var token = Regex.Replace(raw, @"[^A-Za-z0-9_]+", "_");
        token = Regex.Replace(token, "_{2,}", "_");
        token = token.Trim('_');
        if (string.IsNullOrWhiteSpace(token))
            token = "UnnamedTask";
        if (char.IsDigit(token[0]))
            token = "_" + token;
        return token;
    }

    private static string ResolveTextIoNamespaceSegment(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        var folder = ResolveEffectiveTaskOutputFolder(task, allTasks);
        return string.IsNullOrWhiteSpace(folder) ? "TextIO" : $"{folder}.TextIO";
    }

    private static string ResolveEffectiveTaskOutputFolder(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        TaskDef? current = task;
        var visitedParents = new HashSet<int>();
        while (current is not null)
        {
            if (!visitedParents.Add(current.Ordinal))
            {
                ConversionTelemetry.Log("SEMANTIC", $"parent cycle detected context=effective-output-folder taskOrdinal={task.Ordinal} parentOrdinal={current.Ordinal}");
                break;
            }

            var folder = ResolveTaskOutputFolder(current.Folder);
            if (!string.IsNullOrWhiteSpace(folder))
                return folder;
            if (!current.ParentOrdinal.HasValue)
                break;
            current = GetTaskByOrdinal(current.ParentOrdinal.Value);
        }
        return string.Empty;
    }

    private static string ResolveTaskOutputFolder(string? rawFolder)
    {
        if (string.IsNullOrWhiteSpace(rawFolder))
            return string.Empty;

        var folder = rawFolder.Trim();
        var numbered = Regex.Match(folder, @"^\s*(\d+)\s*\.\s*(.+)$");
        if (numbered.Success)
            return "_" + numbered.Groups[1].Value + "_" + ToPascalIdentifier(numbered.Groups[2].Value);

        return ToPascalIdentifier(folder);
    }

    private static bool ShouldGenerateView(TaskDef task)
        => _shouldGenerateViewByTaskOrdinal.TryGetValue(task.Ordinal, out var shouldGenerate)
            ? shouldGenerate
            : ShouldGenerateViewCore(task);

    private static bool ShouldGenerateViewCore(TaskDef task)
    {
        if (task.IsEmptyTask)
            return false;
        if (string.Equals(task.TaskType, "B", StringComparison.OrdinalIgnoreCase) && !task.OpenTaskWindow)
            return false;
        var form = task.FormEntries
            .Where(fe => string.Equals(fe.Model, "FORM_GUI0", StringComparison.OrdinalIgnoreCase))
            .OrderBy(fe => fe.Index)
            .Select(fe => fe.Form)
            .FirstOrDefault() ?? task.Form;
        if (form is null)
            return false;
        if (form.Controls.Count == 0)
            return true;

        return form.Controls.Any(IsSupportedViewControl);
    }

    private static string ResolveViewClassName(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        var baseName = ResolveViewClassBaseName(task, allTasks);
        if (!_viewClassDuplicateIndexByTaskOrdinal.TryGetValue(task.Ordinal, out var idx))
            return baseName;
        if (idx <= 0)
            return baseName;
        if (idx == 1)
            return $"{baseName}_";
        return $"{baseName}_{idx - 1}";
    }

    private static string ResolveViewClassBaseName(TaskDef task, IReadOnlyList<TaskDef> allTasks)
        => _viewClassBaseNameByTaskOrdinal.TryGetValue(task.Ordinal, out var baseName)
            ? baseName
            : ResolveViewClassBaseNameCore(task);

    private static string ResolveViewClassBaseNameCore(TaskDef task)
    {
        var ownerTask = ResolveViewOwnerTask(task, _taskDefs);
        var ownerTaskName = ToTaskClassName(ownerTask.Description);
        var effectiveFormName = !string.IsNullOrWhiteSpace(task.FormName)
            ? task.FormName
            : task.Form?.FormName;
        if (!string.IsNullOrWhiteSpace(effectiveFormName))
        {
            var baseName = Regex.IsMatch(effectiveFormName, @"^[A-Za-z]+\d+\s*[_\-\s]+", RegexOptions.CultureInvariant)
                ? ToTaskClassName(effectiveFormName)
                : ToPascalIdentifier(effectiveFormName);
            var taskName = ToTaskClassName(task.Description);
            if (task.ParentOrdinal.HasValue)
            {
                if (baseName.Equals(taskName, StringComparison.OrdinalIgnoreCase) || baseName.Length <= 20 || IsGenericViewName(baseName))
                    return ownerTaskName + baseName;
                if (!NormalizedIdentifierStartsWith(baseName, ownerTaskName))
                    return ownerTaskName + baseName;
                return EnsureViewSuffix(baseName);
            }
            if (IsGenericViewName(baseName))
                return taskName + baseName;
            if (NormalizedIdentifierStartsWith(baseName, taskName))
                return taskName + "View";
            if (Regex.IsMatch(taskName, @"^[A-Za-z]+\d+_", RegexOptions.CultureInvariant))
                return taskName + baseName;
            if (!baseName.EndsWith("View", StringComparison.Ordinal))
                return baseName + "View";
            return baseName;
        }
        return ownerTaskName + "View";
    }

    private static TaskDef ResolveViewOwnerTask(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        var root = task;
        var visitedParents = new HashSet<int> { root.Ordinal };
        while (root.ParentOrdinal.HasValue)
        {
            if (!visitedParents.Add(root.ParentOrdinal.Value))
            {
                ConversionTelemetry.Log("SEMANTIC", $"parent cycle detected context=view-owner taskOrdinal={task.Ordinal} parentOrdinal={root.ParentOrdinal.Value}");
                break;
            }

            var parent = GetTaskByOrdinal(root.ParentOrdinal.Value);
            if (parent is null)
                break;
            root = parent;
        }
        return root;
    }

    private static bool NormalizedIdentifierStartsWith(string id, string prefix)
    {
        var a = Regex.Replace(id ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        var b = Regex.Replace(prefix ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && a.StartsWith(b, StringComparison.Ordinal);
    }

    private static string EnsureViewSuffix(string baseName)
    {
        return baseName.EndsWith("View", StringComparison.Ordinal) ? baseName : baseName + "View";
    }

    private static bool IsGenericViewName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return true;

        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Title",
            "Display",
            "View",
            "List",
            "Query",
            "Edit",
            "Data",
            "Details"
        };
        if (generic.Contains(baseName))
            return true;
        return baseName.EndsWith("Title", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveApplicationViewClassName(TaskDef? mainTask)
    {
        if (mainTask is null || string.IsNullOrWhiteSpace(mainTask.FormName))
            return "ApplicationView";
        var baseName = ToPascalIdentifier(mainTask.FormName);
        if (baseName.Equals("MainProgram", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("Application", StringComparison.OrdinalIgnoreCase))
            return "ApplicationView";
        if (baseName.StartsWith("Application", StringComparison.Ordinal))
            return baseName;
        return "Application" + baseName;
    }

    private static IReadOnlyList<ViewClickHandler> BuildViewClickHandlers(
        TaskDef task,
        IReadOnlyList<ControlButtonModelDef> buttonModels,
        IReadOnlyDictionary<int, string> controlVariableNameById)
    {
        var result = new List<ViewClickHandler>();
        var controls = task.Form?.Controls ?? Array.Empty<TaskFormControlDef>();
        foreach (var c in controls.Where(x => x.Model == "CTRL_GUI0_PUSH_BUTTON"))
        {
            var raiseExpr = ResolveViewControlRaiseExpression(c, task, buttonModels);
            if (string.IsNullOrWhiteSpace(raiseExpr))
                continue;
            var controlVar = controlVariableNameById.TryGetValue(c.Id, out var mappedVar)
                ? mappedVar
                : ResolveViewControlVariableName(c);
            var handlerName = $"On{controlVar}Click";
            result.Add(new ViewClickHandler(
                c.Id,
                handlerName,
                raiseExpr,
                c.SubformArguments,
                c.SubformArgumentDefs));
        }
        return result;
    }

    private static IReadOnlyList<ViewBooleanBinding> BuildViewBooleanBindings(TaskDef task)
    {
        var result = new List<ViewBooleanBinding>();
        var controls = task.Form?.Controls ?? Array.Empty<TaskFormControlDef>();
        foreach (var c in controls)
        {
            if (c.VisibleExpressionId.HasValue)
                result.Add(new ViewBooleanBinding(c.Id, c.VisibleExpressionId.Value));
            if (c.EnabledExpressionId.HasValue)
                result.Add(new ViewBooleanBinding(c.Id, c.EnabledExpressionId.Value));
        }
        return result
            .DistinctBy(x => (x.ControlId, x.ExpressionId))
            .ToList();
    }

    private static IReadOnlyList<ViewSubformBinding> BuildViewSubformBindings(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        var result = new List<ViewSubformBinding>();
        var controls = task.Form?.Controls
            .Where(c => c.Model == "CTRL_GUI0_SUBFORM" && c.SubformTaskNumber.HasValue)
            .OrderBy(c => c.TabOrder ?? int.MaxValue)
            .ThenBy(c => c.Id)
            .ToList() ?? new List<TaskFormControlDef>();
        var usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
        var usedMethodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in allTasks.Where(x => x.ParentOrdinal == task.Ordinal))
        {
            var childClassName = ToTaskClassName(child.Description);
            if (!string.IsNullOrWhiteSpace(childClassName))
            {
                usedFieldNames.Add(childClassName);
                usedFieldNames.Add(childClassName + "_");
                usedFieldNames.Add(childClassName + "__");
            }
        }
        foreach (var control in controls)
        {
            var taskNumber = control.SubformTaskNumber!.Value;
            if (control.SubformComponentId.HasValue)
            {
                var externalName =
                    !string.IsNullOrWhiteSpace(control.SubformTargetPublicName) ? control.SubformTargetPublicName! :
                    !string.IsNullOrWhiteSpace(control.ControlName) ? control.ControlName! :
                    $"SubformProgram{taskNumber}";
                var externalBaseName = ToTaskClassName(externalName);
                var externalMethodName = externalBaseName + "_SubForm";
                var externalMethodIndex = 0;
                while (!usedMethodNames.Add(externalMethodName))
                {
                    externalMethodName = externalMethodIndex == 0
                        ? externalBaseName + "_SubForm_"
                        : $"{externalBaseName}_SubForm_{externalMethodIndex}";
                    externalMethodIndex++;
                }

                result.Add(new ViewSubformBinding(
                    control.Id,
                    ViewSubformBindingKind.ExternalProgram,
                    TargetTaskOrdinal: null,
                    TargetTaskClassName: externalBaseName,
                    FieldName: "",
                    MethodName: externalMethodName,
                    RawArguments: control.SubformArguments.ToList(),
                    TargetComponentId: control.SubformComponentId,
                    TargetComponentName: control.SubformTargetComponentName,
                    TargetObjectId: taskNumber,
                    TargetPublicName: control.SubformTargetPublicName));
                continue;
            }

            var targetTask = GetChildTaskBySubtaskIndex(task.Ordinal, taskNumber)
                             ?? ResolveTaskByXpaId(taskNumber, allTasks);
            if (targetTask is null)
                continue;

            var baseName = ToTaskClassName(targetTask.Description);
            var fieldName = baseName + "_";
            var i = 1;
            while (!usedFieldNames.Add(fieldName))
            {
                fieldName = $"{baseName}_{i}";
                i++;
            }

            var methodName = baseName + "_SubForm";
            var j = 0;
            while (!usedMethodNames.Add(methodName))
            {
                methodName = j == 0
                    ? baseName + "_SubForm_"
                    : $"{baseName}_SubForm_{j}";
                j++;
            }

            result.Add(new ViewSubformBinding(
                control.Id,
                ViewSubformBindingKind.Task,
                targetTask.Ordinal,
                baseName,
                fieldName,
                methodName,
                control.SubformArguments.ToList(),
                TargetComponentId: null,
                TargetComponentName: null,
                TargetObjectId: null,
                TargetPublicName: null));
        }
        return result;
    }

    private static TaskDef? ResolveTaskByXpaId(int xpaId, IReadOnlyList<TaskDef> allTasks)
    {
        var byProgramIndex = _topLevelTaskDefsByProgramIndex.TryGetValue(xpaId, out var programTask) ? programTask : null;
        if (byProgramIndex is not null)
            return byProgramIndex;

        var byOrdinal = GetTaskByOrdinal(xpaId);
        if (byOrdinal is not null)
            return byOrdinal;

        if (xpaId > 0 && xpaId <= _topLevelTaskDefsByOrdinal.Count)
            return _topLevelTaskDefsByOrdinal[xpaId - 1];

        return null;
    }

    private static IReadOnlyList<ViewBindListHandler> BuildViewBindListHandlers(
        TaskDef task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyDictionary<int, string> controlVariableNameById)
    {
        var result = new List<ViewBindListHandler>();
        var controls = task.Form?.Controls ?? Array.Empty<TaskFormControlDef>();
        foreach (var c in controls.Where(x =>
                     (x.Model == "CTRL_GUI0_COMBOBOX" || x.Model == "CTRL_RICH_CLIENT_COMBOBOX" || x.Model == "CTRL_BROWSER_COMBOBOX")
                     && x.SourceTableObj.HasValue))
        {
            var source = GetDataObjectByOrdinal(c.SourceTableObj!.Value);
            if (source is null)
                continue;

            var comboVar = controlVariableNameById.TryGetValue(c.Id, out var mappedVar)
                ? mappedVar
                : ResolveViewControlVariableName(c);
            var handlerName = comboVar + "_BindListSource";
            var entityType = ToPascalIdentifier(source.Name);
            var entityVar = entityType;
            var valueCol = source.Columns.FirstOrDefault(col => c.LinkFieldObj.HasValue && col.Id == c.LinkFieldObj.Value)
                           ?? source.Columns.FirstOrDefault();
            var displayCol = source.Columns.FirstOrDefault(col => c.DisplayFieldObj.HasValue && col.Id == c.DisplayFieldObj.Value)
                             ?? valueCol;
            var orderBy = source.Indexes.FirstOrDefault(i => c.IndexObj.HasValue && i.Id == c.IndexObj.Value);
            if (valueCol is null || displayCol is null)
                continue;

            result.Add(new ViewBindListHandler(
                c.Id,
                handlerName,
                comboVar,
                source.Ordinal,
                entityType,
                entityVar,
                ToPascalIdentifier(valueCol.Name),
                ToPascalIdentifier(displayCol.Name),
                orderBy is null ? null : ToPascalIdentifier("SortBy" + orderBy.Name)));
        }
        return result;
    }

    private static string ResolveViewControlRaiseExpression(TaskFormControlDef c, TaskDef task, IReadOnlyList<ControlButtonModelDef> buttonModels)
    {
        var raiseType = c.RaiseEventType?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(raiseType))
            return "";

        if (c.ModelRefObj.HasValue)
        {
            var model = buttonModels.FirstOrDefault(m => m.Obj == c.ModelRefObj.Value);
            if (model?.InternalEventId is int iid)
            {
                var mapped = ResolveCommandByInternalEventId(iid);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped!;
            }
        }

        if (raiseType == "U")
        {
            var referenceCandidates = new[]
            {
                c.ControlName?.Trim(),
                c.Text?.Trim()
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            var matchedByReference = task.Handlers
                .Where(h => h.EventType == "U" && !string.IsNullOrWhiteSpace(h.Reference))
                .FirstOrDefault(h => referenceCandidates.Any(rc =>
                    string.Equals(h.Reference, rc, StringComparison.OrdinalIgnoreCase)));
            if (matchedByReference is not null)
            {
                var matchedCommand = ResolveHandlerCommandName(matchedByReference, task);
                if (IsApplicationEventReference(matchedByReference.EventParent, matchedByReference.EventPublicComponentId))
                    return $"Application.{matchedCommand}";
                if (matchedByReference.EventParent.HasValue)
                    return $"_controller._parent.{matchedCommand}";
                return $"_controller.{matchedCommand}";
            }

            if (!string.IsNullOrWhiteSpace(c.RaiseEventObject))
            {
                if (IsApplicationEventReference(c.RaiseEventParent, c.RaiseEventPublicComponentId))
                {
                    var applicationCommandName = ResolveCommandNameByEventObject(
                        c.RaiseEventObject!,
                        c.RaiseEventPublicComponentId,
                        c.RaiseEventParent,
                        task);
                    if (!string.IsNullOrWhiteSpace(applicationCommandName))
                        return applicationCommandName;
                }

                var parentCommandName = ResolveParentCommandNameByEventObject(c.RaiseEventObject!, task);
                if (!string.IsNullOrWhiteSpace(parentCommandName))
                    return $"_controller._parent.{parentCommandName}";
                var commandName = ResolveCommandNameByEventObject(
                    c.RaiseEventObject!,
                    c.RaiseEventPublicComponentId,
                    c.RaiseEventParent,
                    task);
                if (!string.IsNullOrWhiteSpace(commandName))
                    return commandName.StartsWith("Application.", StringComparison.Ordinal)
                        ? commandName
                        : $"_controller.{commandName}";
                var handlerByObj = task.Handlers.FirstOrDefault(h => h.EventType == "U" && string.Equals(h.EventPublicObject, c.RaiseEventObject, StringComparison.OrdinalIgnoreCase));
                if (handlerByObj is not null)
                {
                    var handlerCommand = ResolveHandlerCommandName(handlerByObj, task);
                    if (IsApplicationEventReference(handlerByObj.EventParent, handlerByObj.EventPublicComponentId))
                        return $"Application.{handlerCommand}";
                    if (handlerByObj.EventParent.HasValue)
                        return $"_controller._parent.{handlerCommand}";
                    return $"_controller.{handlerCommand}";
                }
            }
            var uHandlers = task.Handlers.Where(h => h.EventType == "U").ToList();
            if (uHandlers.Count == 1)
            {
                var handlerCommand = ResolveHandlerCommandName(uHandlers[0], task);
                if (IsApplicationEventReference(uHandlers[0].EventParent, uHandlers[0].EventPublicComponentId))
                    return $"Application.{handlerCommand}";
                if (uHandlers[0].EventParent.HasValue)
                    return $"_controller._parent.{handlerCommand}";
                return $"_controller.{handlerCommand}";
            }
            var hint = ResolveRaiseCommandByControlHint(c);
            if (!string.IsNullOrWhiteSpace(hint))
                return hint;
            return "";
        }

        if (raiseType == "I")
        {
            if (c.RaiseEventInternalEventId is int raiseInternalId)
            {
                var mapped = ResolveCommandByInternalEventId(raiseInternalId);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped!;
            }
            var candidates = task.Handlers.Where(h => h.EventType == "I").ToList();
            if (!string.IsNullOrWhiteSpace(c.RaiseEventObject))
                candidates = candidates.Where(h => string.Equals(h.EventPublicObject, c.RaiseEventObject, StringComparison.OrdinalIgnoreCase)).ToList();
            var candidateCommands = candidates
                .Select(h => ResolveInternalHandlerCommand(h, task))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidateCommands.Count == 1)
                return candidateCommands[0]!;
            if (candidates.Count == 1)
            {
                var cmd = ResolveInternalHandlerCommand(candidates[0], task);
                if (!string.IsNullOrWhiteSpace(cmd))
                    return cmd;
            }
            var hint = ResolveRaiseCommandByControlHint(c);
            if (!string.IsNullOrWhiteSpace(hint))
                return hint;
            return "";
        }

        if (raiseType == "S")
        {
            if (c.RaiseEventKeyCombinationId is int raiseKeyId)
            {
                var keys = ResolveKeyCombination(raiseKeyId);
                if (!string.IsNullOrWhiteSpace(keys))
                    return keys!;
            }
            var candidates = task.Handlers.Where(h => h.EventType == "S").ToList();
            if (!string.IsNullOrWhiteSpace(c.RaiseEventObject))
                candidates = candidates.Where(h => string.Equals(h.EventPublicObject, c.RaiseEventObject, StringComparison.OrdinalIgnoreCase)).ToList();
            var candidateKeys = candidates
                .Select(h => ResolveKeyCombination(h.EventKeyCombinationId))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidateKeys.Count == 1)
                return candidateKeys[0]!;
            if (candidates.Count == 1)
            {
                var keysExpr = ResolveKeyCombination(candidates[0].EventKeyCombinationId);
                if (!string.IsNullOrWhiteSpace(keysExpr))
                    return keysExpr;
            }
            var hint = ResolveRaiseCommandByControlHint(c);
            if (!string.IsNullOrWhiteSpace(hint))
                return hint;
            return "";
        }

        return "";
    }

    private static string ResolveRaiseCommandByControlHint(TaskFormControlDef c)
    {
        var tokens = SplitAlphaNumericTokens($"{c.Text} {c.ControlName}").ToHashSet(StringComparer.Ordinal);
        if (tokens.Count == 0)
            return "";

        if (tokens.Contains("shelltoos"))
            return "ENV.Commands.ShellToOS";
        if (tokens.Contains("screen") && tokens.Contains("refresh"))
            return "Command.ReloadData";
        if (tokens.Contains("create") && tokens.Contains("child"))
            return "Command.InsertChildNode";
        if (tokens.Contains("close") && tokens.Contains("application"))
            return "Command.ExitApplication";
        if (tokens.Contains("print"))
            return "ENV.Commands.ExportData";
        if (tokens.Contains("backtab") || (tokens.Contains("back") && tokens.Contains("tab")))
            return "(Keys.Shift|Keys.Tab)";
        if (tokens.Contains("tab"))
            return "Keys.Tab";
        if (tokens.Contains("help"))
            return "Command.Help";
        return "";
    }

    private static IEnumerable<string> SplitAlphaNumericTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string ResolveHandlerCommandName(TaskHandlerDef h, TaskDef task)
    {
        if (IsApplicationEventReference(h.EventParent, h.EventPublicComponentId) &&
            int.TryParse(h.EventPublicObject, out var appEventObj))
        {
            var appTask = _applicationTask;
            var appEvent = appTask?.Events.FirstOrDefault(e => e.Ordinal == appEventObj);
            if (appEvent is not null)
                return ResolveTaskCommandIdentifier(appTask!, appEvent.Description, preserveCase: true);
        }

        if (h.EventParent.HasValue && task.ParentOrdinal.HasValue)
        {
            var parentTask = GetTaskByOrdinal(task.ParentOrdinal.Value);
            if (parentTask is not null &&
                int.TryParse(h.EventPublicObject, out var parentEventObj))
            {
                var parentEvt = parentTask.Events.FirstOrDefault(e => e.Ordinal == parentEventObj);
                if (parentEvt is not null)
                    return ResolveTaskCommandIdentifier(task, parentEvt.Description, preserveCase: true);
            }
        }

        if (int.TryParse(h.EventPublicObject, out var eventObj))
        {
            var evt = task.Events.FirstOrDefault(e => e.Ordinal == eventObj);
            if (evt is not null)
                return ResolveTaskCommandIdentifier(task, evt.Description, preserveCase: true);
        }
        return "Event" + (h.EventPublicObject ?? "X");
    }

    private static bool IsApplicationEventReference(int? eventParent, int? eventPublicComponentId)
        => eventParent == 32768 || (eventPublicComponentId == -1 && eventParent == 32768);

    private static string ResolveCommandNameByEventObject(string eventPublicObject, int? eventPublicComponentId, int? eventParent, TaskDef task)
    {
        if (!int.TryParse(eventPublicObject, out var eventObj))
            return "";
        if (IsApplicationEventReference(eventParent, eventPublicComponentId))
        {
            var appTask = _applicationTask;
            var appEvent = appTask?.Events.FirstOrDefault(e => e.Ordinal == eventObj);
            if (appEvent is not null)
                return "Application." + ResolveTaskCommandIdentifier(appTask!, appEvent.Description, preserveCase: true);
            return "";
        }

        var evt = task.Events.FirstOrDefault(e => e.Ordinal == eventObj);
        if (evt is null)
            return "";
        return ResolveTaskCommandIdentifier(task, evt.Description, preserveCase: true);
    }

    private static string ResolveParentCommandNameByEventObject(string eventPublicObject, TaskDef task)
    {
        if (!task.ParentOrdinal.HasValue || !int.TryParse(eventPublicObject, out var eventObj))
            return "";
        var parentTask = GetTaskByOrdinal(task.ParentOrdinal.Value);
        if (parentTask is null)
            return "";
        var evt = parentTask.Events.FirstOrDefault(e => e.Ordinal == eventObj);
        if (evt is null)
            return "";
        return ResolveTaskCommandIdentifier(task, evt.Description, preserveCase: true);
    }

    private static HashSet<string> CollectReservedCommandNames(TaskDef task)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        reserved.Add(ToTaskClassName(task.Description));
        foreach (var rc in task.ResourceColumns)
            reserved.Add(ResolveTaskResourceMemberName(task, rc));
        foreach (var child in GetChildTasks(task.Ordinal))
            reserved.Add(ToTaskClassName(child.Description));
        foreach (var fn in task.FunctionOverrides)
        {
            var methodName = string.IsNullOrWhiteSpace(fn.Name) ? "" : ToCodeIdentifierPreservingCase(fn.Name);
            if (!string.IsNullOrWhiteSpace(methodName))
                reserved.Add(methodName);
        }
        return reserved;
    }

    private static string ResolveTaskCommandIdentifier(TaskDef task, string raw, bool preserveCase = false)
    {
        var id = ToCustomCommandIdentifier(raw, preserveCase);
        var reserved = CollectReservedCommandNames(task);
        if (!reserved.Contains(id))
            return id;

        var candidate = id + "Command";
        var suffix = 2;
        while (reserved.Contains(candidate))
        {
            candidate = id + "Command" + suffix;
            suffix++;
        }
        return candidate;
    }

    private static string ToCustomCommandIdentifier(string raw, bool preserveCase = false)
    {
        var id = preserveCase ? ToCodeIdentifierPreservingCase(raw) : ToPascalIdentifier(raw);
        if (string.Equals(id, "Start", StringComparison.Ordinal))
            return "Start_";
        return id;
    }

    private static string ResolveKeyCombination(int? keyCombinationId)
    {
        return keyCombinationId switch
        {
            8 => "(Keys.Control|Keys.Space)",
            11 => "(Keys.Control|Keys.D)",
            12 => "(Keys.Control|Keys.E)",
            21 => "(Keys.Control|Keys.N)",
            23 => "(Keys.Control|Keys.P)",
            82 => "Keys.F3",
            83 => "Keys.F4",
            84 => "Keys.F5",
            86 => "Keys.F7",
            88 => "Keys.F9",
            104 => "(Keys.Control|Keys.F5)",
            109 => "(Keys.Control|Keys.F10)",
            165 => "(Keys.Control|Keys.A)",
            189 => "Keys.F12",
            _ => ""
        };
    }

    private static string ResolveInternalHandlerCommand(TaskHandlerDef h, TaskDef task)
    {
        if (h.EventInternalEventId.HasValue)
        {
            var mapped = ResolveCommandByInternalEventId(h.EventInternalEventId.Value);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped!;
        }
        if (int.TryParse(h.EventPublicObject, out var eventObj))
        {
            var evt = task.Events.FirstOrDefault(e => e.Ordinal == eventObj);
            if (evt?.InternalEventId is int internalId)
            {
                var mapped = ResolveCommandByInternalEventId(internalId);
                if (!string.IsNullOrWhiteSpace(mapped))
                    return mapped!;
            }
        }
        return "";
    }

    private static string? ResolveCommandByInternalEventId(int internalEventId)
    {
        return internalEventId switch
        {
            13 => "Command.CloseForm",
            14 => "Command.Exit",
            23 => "Commands.SelectApplicationFromList",
            24 => "Command.ExitApplication",
            27 => "ENV.Commands.ShellToOS",
            28 => "Command.ExitApplication",
            30 => "Command.SwitchToUpdateActivity",
            32 => "Command.SwitchToBrowseActivity",
            33 => "Command.UndoChangesInRow",
            42 => "Command.Select",
            34 => "Command.Expand",
            35 => "Command.ExpandTextBox",
            37 => "Command.InsertRow",
            36 => "Command.DeleteRow",
            242 => "Command.BeforeControlClick",
            245 => "Command.BeforeWindowClick",
            63 => "Command.GoToNextControl",
            64 => "Command.GoToPreviousRow",
            65 => "Command.GoToNextRow",
            66 => "Command.GoToPreviousPage",
            67 => "Command.GoToNextPage",
            72 => "Command.GoToFirstRow",
            73 => "Command.GoToLastRow",
            301 => "Command.DoubleClick",
            249 => "Command.WindowResize",
            250 => "Command.WindowMove",
            251 => "Command.WindowResize",
            243 => "Commands.PageHeader",
            244 => "Commands.PageFooter",
            293 => "Command.SaveCurrentRow",
            294 => "Command.ReloadData",
            295 => "Command.ReloadData",
            160 => "ENV.Commands.ExportData",
            302 => "Command.Click",
            303 => "Command.MouseLeave",
            304 => "Command.MouseEnter",
            378 => "Command.ExpandTreeNode",
            379 => "Command.CollapseTreeNode",
            380 => "Command.InsertChildNode",
            313 => "Command.ToggleCurrentRowMultiSelection",
            384 => "Command.GoToFirstChildNode",
            395 => "Command.GoToFirstRowWhileMultiSelecting",
            396 => "Command.GoToLastRowWhileMultiSelecting",
            409 => "Command.DragStart",
            410 => "Command.DragDrop",
            439 => "Command.ShowContextMenu",
            544 => "Command.ControlValueChanged",
            555 => "Command.GridColumnClick",
            370 => "Commands.CloseAllWindows",
            462 => "Commands.NextWindow",
            463 => "Commands.PreviousWindow",
            477 => "Commands.ContextLostFocus",
            478 => "Commands.ContextGotFocus",
            155 => "Command.Help",
            156 => "Command.Help",
            _ => null
        };
    }

    private static Dictionary<string, string> BuildSelectNameToExpressionMap(TaskDef task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
        var modelMembers = BuildModelMembers(task, dataObjects);
        var primaryMember = modelMembers.FirstOrDefault(m => m.DbObj == primaryObj).MemberName;
        if (string.IsNullOrWhiteSpace(primaryMember))
        {
            var d = primaryObj.HasValue ? GetDataObjectByOrdinal(primaryObj.Value) : null;
            if (d is not null)
                primaryMember = ResolveDataObjectTypeName(d, dataObjects);
        }

        foreach (var s in task.Selects)
        {
            var expr = ResolveSelectExpression(s, task, dataObjects, primaryMember ?? "");
            if (!string.IsNullOrWhiteSpace(expr) && !map.ContainsKey(s.Name))
                map[s.Name] = expr;
        }
        return map;
    }

    private static string ResolveSelectExpression(TaskLogicSelectDef sel, TaskDef task, IReadOnlyList<DataObjectDef> dataObjects, string primaryMember)
    {
        if (sel.Type == "R" && sel.SourceDbObj.HasValue)
        {
            var d = GetDataObjectByOrdinal(sel.SourceDbObj.Value);
            var col = d?.Columns.FirstOrDefault(c => c.Id == sel.ColumnId);
            if (d is not null && col is null && sel.ColumnId > 0 && sel.ColumnId <= d.Columns.Count)
                col = d.Columns[sel.ColumnId - 1];
            if (d is not null && col is not null)
            {
                var sourceMember = ResolveSelectSourceMember(sel, task, dataObjects);
                if (string.IsNullOrWhiteSpace(sourceMember))
                    sourceMember = primaryMember;
                return $"{sourceMember}.{ToPascalIdentifier(col.Name)}";
            }
        }

        var rc = task.ResourceColumns.FirstOrDefault(c => c.Id == sel.ColumnId);
        if (rc is not null)
            return ResolveTaskResourceMemberName(task, rc);

        if (sel.Type == "V")
        {
            var virtuals = task.Selects.Where(s => s.Type == "V").ToList();
            var pos = virtuals.FindIndex(s => string.Equals(s.Name, sel.Name, StringComparison.OrdinalIgnoreCase));
            if (pos >= 0)
            {
                var byOrder = task.ResourceColumns.ToList();
                if (pos < byOrder.Count)
                    return ResolveTaskResourceMemberName(task, byOrder[pos]);
            }
        }

        return "";
    }

    private static string ResolveSelectSourceMember(TaskLogicSelectDef sel, TaskDef task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var modelMembers = BuildModelMembers(task, dataObjects);
        if (sel.SourceLinkSequence.HasValue)
        {
            var primaryObj = task.PrimaryDbObj ?? task.InformationDbObj;
            var linkMembers = BuildLinkMembers(task, dataObjects, modelMembers, primaryObj);
            var idx = sel.SourceLinkSequence.Value - 1;
            if (idx >= 0 && idx < linkMembers.Count)
                return linkMembers[idx].MemberName;
        }
        return modelMembers.FirstOrDefault(m => m.DbObj == sel.SourceDbObj).MemberName;
    }

    private static List<(int DbObj, string ModelType, string MemberName)> BuildModelMembers(TaskDef task, IReadOnlyList<DataObjectDef> dataObjects)
    {
        var result = new List<(int DbObj, string ModelType, string MemberName)>();
        foreach (var dbObj in task.ResourceDataObjects.Distinct())
        {
            var d = GetDataObjectByOrdinal(dbObj);
            if (d is null)
                continue;
            var modelType = ResolveDataObjectTypeName(d, dataObjects);
            var baseMember = modelType;
            var member = baseMember;
            var suffix = 2;
            while (result.Any(x => x.MemberName == member))
            {
                member = baseMember + suffix;
                suffix++;
            }
            result.Add((dbObj, modelType, member));
        }
        return result;
    }

    private static bool IsSupportedViewControl(TaskFormControlDef c)
    {
        return c.Model is
            "CTRL_GUI0_TABLE" or
            "CTRL_GUI0_COLUMN" or
            "CTRL_GUI0_STATIC" or
            "CTRL_GUI0_EDIT" or
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_GUI0_PUSH_BUTTON" or
            "CTRL_GUI0_SUBFORM" or
            "CTRL_GUI0_CHECKBOX" or
            "CTRL_GUI0_TAB" or
            "CTRL_GUI0_TREE" or
            "CTRL_GUI0_RADIO" or
            "CTRL_GUI0_IMAGE" or
            "CTRL_GUI1_IMAGE" or
            "CTRL_GUI0_LINE" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RICH_EDIT" or
            "CTRL_GUI0_DOTNET" or
            "CTRL_GUI0_BROWSER" or
            "CTRL_RICH_CLIENT_EDIT" or
            "CTRL_BROWSER_EDIT" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_RICH_CLIENT_CHECKBOX";
    }

    private static bool IsTableViewControl(TaskFormControlDef c)
        => string.Equals(c.Model, "CTRL_GUI0_TABLE", StringComparison.OrdinalIgnoreCase);

    private static bool IsTableColumnViewControl(TaskFormControlDef c)
        => string.Equals(c.Model, "CTRL_GUI0_COLUMN", StringComparison.OrdinalIgnoreCase);

    private static bool IsLeafViewControl(TaskFormControlDef c)
        => c.Model is
            "CTRL_GUI0_STATIC" or
            "CTRL_GUI0_EDIT" or
            "CTRL_GUI0_COMBOBOX" or
            "CTRL_GUI0_PUSH_BUTTON" or
            "CTRL_GUI0_SUBFORM" or
            "CTRL_GUI0_CHECKBOX" or
            "CTRL_GUI0_TAB" or
            "CTRL_GUI0_TREE" or
            "CTRL_GUI0_RADIO" or
            "CTRL_GUI0_IMAGE" or
            "CTRL_GUI1_IMAGE" or
            "CTRL_GUI0_LINE" or
            "CTRL_GUI0_LISTBOX" or
            "CTRL_GUI0_RICH_EDIT" or
            "CTRL_GUI0_DOTNET" or
            "CTRL_GUI0_BROWSER" or
            "CTRL_RICH_CLIENT_EDIT" or
            "CTRL_BROWSER_EDIT" or
            "CTRL_RICH_CLIENT_COMBOBOX" or
            "CTRL_BROWSER_COMBOBOX" or
            "CTRL_RICH_CLIENT_CHECKBOX";

    private static bool IsStaticGroupBoxLike(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (!string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (staticContainerIds is not null && staticContainerIds.Contains(c.Id))
            return true;
        var style = c.StyleValue ?? c.InternalStyleValue;
        return style == 6 || style == 8 || style == 15;
    }

    private static bool IsStaticShapeLike(TaskFormControlDef c, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (IsStaticGroupBoxLike(c, staticContainerIds))
            return false;
        if (!string.Equals(c.Model, "CTRL_GUI0_STATIC", StringComparison.OrdinalIgnoreCase))
            return false;
        var style = c.StyleValue ?? c.InternalStyleValue;
        return style == 11 || style == 12 || style == 13 || style == 14;
    }

    private static string ResolveViewControlTypeName(TaskFormControlDef c, TaskDef task, IReadOnlyList<ControlButtonModelDef> buttonModels, IReadOnlySet<int>? staticContainerIds = null)
    {
        if (c.Model == "CTRL_GUI0_PUSH_BUTTON" && c.ModelRefObj.HasValue)
        {
            var model = buttonModels.FirstOrDefault(m => m.Obj == c.ModelRefObj.Value);
            if (model is not null)
                return $"Controls.{ToPascalIdentifier(model.Name)}";
        }
        if (string.Equals(c.Model, "CTRL_GUI0_BROWSER", StringComparison.OrdinalIgnoreCase))
            return "Shared.Theme.Controls.WebBrowser";
        if (string.Equals(c.Model, "CTRL_GUI0_DOTNET", StringComparison.OrdinalIgnoreCase))
            return ResolveViewDotNetControlTypeName(c, task);
        return c.Model switch
        {
            "CTRL_GUI0_TABLE" => "Controls.V9CompatibleDefaultTable",
            "CTRL_GUI0_COLUMN" => "Shared.Theme.Controls.CompatibleGridColumn",
            "CTRL_GUI0_STATIC" => IsStaticGroupBoxLike(c, staticContainerIds)
                ? "Shared.Theme.Controls.GroupBox"
                : IsStaticShapeLike(c, staticContainerIds)
                    ? "Shared.Theme.Controls.Shape"
                    : "Shared.Theme.Controls.CompatibleLabel",
            "CTRL_GUI0_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_RICH_CLIENT_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_BROWSER_EDIT" => "Shared.Theme.Controls.CompatibleTextBox",
            "CTRL_GUI0_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_RICH_CLIENT_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_BROWSER_COMBOBOX" => "Shared.Theme.Controls.ComboBox",
            "CTRL_GUI0_PUSH_BUTTON" => "Shared.Theme.Controls.Button",
            "CTRL_GUI0_SUBFORM" => "Shared.Theme.Controls.SubForm",
            "CTRL_GUI0_CHECKBOX" => "ENV.UI.CheckBox",
            "CTRL_RICH_CLIENT_CHECKBOX" => "ENV.UI.CheckBox",
            "CTRL_GUI0_TAB" => "ENV.UI.TabControl",
            "CTRL_GUI0_TREE" => "ENV.UI.TreeView",
            "CTRL_GUI0_RADIO" => "ENV.UI.RadioButton",
            "CTRL_GUI0_IMAGE" => "ENV.UI.PictureBox",
            "CTRL_GUI1_IMAGE" => "ENV.UI.PictureBox",
            "CTRL_GUI0_LINE" => "Shared.Theme.Controls.Line",
            "CTRL_GUI0_LISTBOX" => "ENV.UI.ListBox",
            "CTRL_GUI0_RICH_EDIT" => "ENV.UI.RichTextBox",
            _ => "Shared.Theme.Controls.CompatibleTextBox"
        };
    }

    private static string ResolveViewDotNetControlTypeName(TaskFormControlDef c, TaskDef task)
    {
        var resource = ResolveViewHostResource(c, task);
        if (resource is not null && !string.IsNullOrWhiteSpace(resource.ObjectType))
            return NormalizeDotNetObjectType(resource.ObjectType);
        return "System.Windows.Forms.Panel";
    }

    private static string NormalizeDotNetObjectType(string objectType)
        => objectType.Trim() switch
        {
            "String[]" => "System.String[]",
            "string[]" => "System.String[]",
            _ => objectType.Trim()
        };

    private static TaskResourceColumnDef? ResolveViewHostResource(TaskFormControlDef c, TaskDef task)
    {
        if (!string.IsNullOrWhiteSpace(c.ControlName))
        {
            var byName = task.ResourceColumns.FirstOrDefault(r => string.Equals(r.Name, c.ControlName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        if (!string.IsNullOrWhiteSpace(c.DataColumn))
        {
            var byName = task.ResourceColumns.FirstOrDefault(r => string.Equals(r.Name, c.DataColumn, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        return null;
    }

    private static List<(TaskLogicLinkDef Link, string MemberName)> BuildLinkMembers(
        TaskDef task,
        IReadOnlyList<DataObjectDef> dataObjects,
        IReadOnlyList<(int DbObj, string ModelType, string MemberName)> modelMembers,
        int? primaryObj)
    {
        var result = new List<(TaskLogicLinkDef Link, string MemberName)>();
        var seqByObj = new Dictionary<int, int>();
        foreach (var link in task.Links)
        {
            if (!seqByObj.ContainsKey(link.DbObj))
                seqByObj[link.DbObj] = 0;
            seqByObj[link.DbObj]++;
            var n = seqByObj[link.DbObj];

            var existing = modelMembers.FirstOrDefault(m => m.DbObj == link.DbObj).MemberName;
            var member = existing;
            if (link.DbObj == primaryObj && n == 1 && !string.IsNullOrWhiteSpace(existing))
            {
                member = existing + "_";
            }
            else if (link.DbObj == primaryObj || n > 1)
            {
                var d = GetDataObjectByOrdinal(link.DbObj);
                var baseName = d is null ? "Link" + link.DbObj : ResolveDataObjectTypeName(d, dataObjects);
                member = n == 1 ? baseName : baseName + n;
            }
            if (string.IsNullOrWhiteSpace(member))
                member = "Link" + link.DbObj;
            result.Add((link, member));
        }
        return result;
    }

    private static string ResolveViewControlVariableName(TaskFormControlDef c)
    {
        if (!string.IsNullOrWhiteSpace(c.ControlName))
            return ToCodeIdentifierPreservingCase(c.ControlName);
        if (string.Equals(c.Model, "CTRL_GUI0_BROWSER", StringComparison.OrdinalIgnoreCase))
            return "webBrowser" + c.Id;
        if (string.Equals(c.Model, "CTRL_GUI0_DOTNET", StringComparison.OrdinalIgnoreCase))
            return "dotNet" + c.Id;
        if (!string.IsNullOrWhiteSpace(c.Text))
            return ToCodeIdentifierPreservingCase(c.Text);
        return "ctrl" + c.Id;
    }

    private static string ResolveMergeTemplateVariableName(TaskFormEntryDef formEntry)
    {
        var suffix = ToPascalIdentifier(string.IsNullOrWhiteSpace(formEntry.Form.FormName) ? $"Merge{formEntry.Index}" : formEntry.Form.FormName!);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = $"Merge{formEntry.Index}";
        return "_view" + suffix;
    }

    private static Dictionary<int, string> BuildUniqueFormEntryNameMap(
        IReadOnlyList<TaskFormEntryDef> forms,
        Func<TaskFormEntryDef, int, string> resolveBaseName)
    {
        var map = new Dictionary<int, string>(forms.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < forms.Count; i++)
        {
            var form = forms[i];
            var baseName = resolveBaseName(form, i + 1);
            map[form.Index] = MakeUniqueIdentifier(baseName, used, "Entry" + form.Index);
        }
        return map;
    }

    private static string MakeUniqueIdentifier(string baseName, HashSet<string> used, string fallback)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = fallback;
        if (used.Add(baseName))
            return baseName;

        var suffix = 2;
        while (true)
        {
            var candidate = baseName + suffix;
            if (used.Add(candidate))
                return candidate;
            suffix++;
        }
    }

    private static Dictionary<int, string> BuildFormIoWriteCalls(
        TaskDef task,
        IReadOnlyList<TaskDef> allTasks,
        IReadOnlyList<TaskFormEntryDef> printForms,
        IReadOnlyList<TaskFormEntryDef> textForms,
        IReadOnlyList<TaskFormEntryDef> mergeForms,
        IReadOnlyDictionary<int, string> printSectionNames,
        IReadOnlyDictionary<int, string> textSectionNames,
        IReadOnlyDictionary<int, string> mergeTemplateVariableNames)
    {
        var map = new Dictionary<int, string>();
        foreach (var fe in printForms)
            if (printSectionNames.TryGetValue(fe.Index, out var sectionName))
                map[fe.Index] = $"_layout.{sectionName}.WriteTo(_ioPrint)";

        foreach (var fe in textForms)
            if (textSectionNames.TryGetValue(fe.Index, out var sectionName))
                map[fe.Index] = $"_layout.{sectionName}.WriteTo(_ioReport)";

        var streamVar = ResolveMergeStreamExpression(task, allTasks);
        foreach (var fe in mergeForms)
            if (mergeTemplateVariableNames.TryGetValue(fe.Index, out var variableName))
                map[fe.Index] = $"{variableName}.WriteTo({streamVar})";

        return map;
    }

    private static Dictionary<int, string> BuildFormIoReadCalls(
        TaskDef task,
        IReadOnlyList<TaskFormEntryDef> textForms,
        IReadOnlyDictionary<int, string> textSectionNames)
    {
        var map = new Dictionary<int, string>();
        foreach (var io in task.FormIos.Where(x => x.OperationType == "I" && x.FormEntryIndex.HasValue))
        {
            var fe = textForms.FirstOrDefault(x => x.Index == io.FormEntryIndex!.Value);
            if (fe is null)
                continue;

            var sectionName = !string.IsNullOrWhiteSpace(io.Reference)
                ? ToPascalIdentifier(io.Reference!)
                : textSectionNames.TryGetValue(fe.Index, out var mapped) ? mapped : ResolveTextIoSectionName(fe, fe.Index);

            if (string.IsNullOrWhiteSpace(sectionName))
                sectionName = ResolveTextIoSectionName(fe, fe.Index);

            var readCall = string.Equals(io.Delimiter, "S", StringComparison.OrdinalIgnoreCase)
                ? $"_layout.{sectionName}.ReadSeparated(_ioReport, {ResolveDelimiterCharLiteral(io.DelimiterChar)})"
                : $"_layout.{sectionName}.ReadFrom(_ioReport)";
            map[io.FormEntryIndex!.Value] = readCall;
        }

        return map;
    }

    private static string ResolvePrintSectionName(TaskFormEntryDef formEntry, int sequence)
    {
        var raw = formEntry.Form.FormName;
        var candidate = string.IsNullOrWhiteSpace(raw)
            ? (sequence == 1 ? "Body" : $"Section{sequence}")
            : raw!;
        return ToPascalIdentifier(candidate);
    }

    private static string ResolveTextIoSectionName(TaskFormEntryDef formEntry, int sequence)
    {
        var raw = formEntry.Form.FormName;
        var candidate = string.IsNullOrWhiteSpace(raw)
            ? (sequence == 1 ? "Line" : $"Line{sequence}")
            : raw!;
        return ToPascalIdentifier(candidate);
    }

    private static string ResolveDelimiterCharLiteral(int? delimiterChar)
    {
        if (!delimiterChar.HasValue)
            return "' '";
        var c = (char)delimiterChar.Value;
        return c switch
        {
            '\'' => "'\\''",
            '\\' => "'\\\\'",
            _ => $"'{c}'"
        };
    }

    private static string ResolveMergeStreamVariableName(TaskDef task)
    {
        var suffix = ToPascalIdentifier(string.IsNullOrWhiteSpace(task.Io?.Description) ? "Merge" : task.Io.Description!);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = "Merge";
        var candidate = "_io" + suffix;
        var used = ResolveTextIoStreamVariableNames(task);
        return MakeUniqueIdentifier(candidate, used, "_ioMerge");
    }

    private static HashSet<string> ResolveTextIoStreamVariableNames(TaskDef task)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ios = task.Ios.Count > 0 ? task.Ios : (task.Io is null ? Array.Empty<TaskIoDef>() : new[] { task.Io });
        if (ios.Count <= 1)
        {
            used.Add("_ioReport");
            return used;
        }

        for (var index = 0; index < ios.Count; index++)
        {
            var baseName = ResolveTextIoStreamVariableName(ios[index], index);
            MakeUniqueIdentifier(baseName, used, index == 0 ? "_ioReport" : $"_ioReport{index + 1}");
        }
        return used;
    }

    private static string ResolveTextIoStreamVariableName(TaskIoDef? io, int ioIndex)
    {
        var desc = io?.Description;
        if (!string.IsNullOrWhiteSpace(desc))
        {
            var suffix = ToPascalIdentifier(desc);
            if (!string.IsNullOrWhiteSpace(suffix))
                return "_io" + suffix;
        }
        return ioIndex == 0 ? "_ioReport" : $"_ioReport{ioIndex + 1}";
    }

    private static string ResolveMergeStreamExpression(TaskDef task, IReadOnlyList<TaskDef> allTasks)
    {
        if (task.Io is not null)
            return ResolveMergeStreamVariableName(task);
        if (task.ParentOrdinal.HasValue)
        {
            var parent = GetTaskByOrdinal(task.ParentOrdinal.Value);
            if (parent is not null)
                return "_parent." + ResolveMergeStreamExpression(parent, allTasks);
        }
        return ResolveMergeStreamVariableName(task);
    }

    private static string? ResolvePageHeaderSectionName(
        TaskDef task,
        IReadOnlyList<TaskFormEntryDef> printForms,
        IReadOnlyDictionary<int, string> printSectionNames)
    {
        if (task.Io?.PageHeaderFormEntryIndex is int explicitHeaderIndex)
        {
            if (printSectionNames.TryGetValue(explicitHeaderIndex, out var explicitHeaderName))
                return explicitHeaderName;
            var bySequence = printForms.OrderBy(x => x.Index).ElementAtOrDefault(explicitHeaderIndex - 1);
            if (bySequence is not null && printSectionNames.TryGetValue(bySequence.Index, out var explicitHeaderBySequence))
                return explicitHeaderBySequence;
        }

        var headerByName = printSectionNames.Values.FirstOrDefault(x => x.Equals("Header", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(headerByName))
            return headerByName;

        var headerIo = task.FormIos.FirstOrDefault(x => x.OperationType == "O" && x.Level == "G" && x.Type == "P" && x.FormEntryIndex.HasValue);
        if (headerIo?.FormEntryIndex is int idx && printSectionNames.TryGetValue(idx, out var name))
            return name;
        return null;
    }

    private static string? ResolvePageFooterSectionName(
        TaskDef task,
        IReadOnlyList<TaskFormEntryDef> printForms,
        IReadOnlyDictionary<int, string> printSectionNames)
    {
        if (task.Io?.PageFooterFormEntryIndex is int explicitFooterIndex)
        {
            if (printSectionNames.TryGetValue(explicitFooterIndex, out var explicitFooterName))
                return explicitFooterName;
            var bySequence = printForms.OrderBy(x => x.Index).ElementAtOrDefault(explicitFooterIndex - 1);
            if (bySequence is not null && printSectionNames.TryGetValue(bySequence.Index, out var explicitFooterBySequence))
                return explicitFooterBySequence;
        }

        var footerByName = printSectionNames.Values.FirstOrDefault(x => x.Equals("Footer", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(footerByName))
            return footerByName;

        var footerIo = task.FormIos.FirstOrDefault(x => x.OperationType == "O" && x.Level == "G" && x.Type == "S" && x.FormEntryIndex.HasValue);
        if (footerIo?.FormEntryIndex is int idx && printSectionNames.TryGetValue(idx, out var name))
            return name;
        return null;
    }

    private static string ToTaskClassName(string rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription))
            return "UnnamedTask";
        var leadingMarker = Regex.Match(rawDescription, @"^\s*[-=<>\.]+\s*(.+)$");
        if (leadingMarker.Success)
        {
            var markerRaw = leadingMarker.Groups[1].Value;
            string markerName;
            if (markerRaw.Contains("=", StringComparison.Ordinal))
            {
                var parts = markerRaw.Split('=', 2, StringSplitOptions.TrimEntries);
                var left = ToPascalIdentifier(parts[0]);
                var right = parts.Length > 1 ? ToPascalIdentifier(parts[1]) : "";
                markerName = string.IsNullOrWhiteSpace(right) ? left : left + "_" + right;
            }
            else
            {
                markerName = ToPascalIdentifier(markerRaw);
            }

            if (string.IsNullOrWhiteSpace(markerName))
                return "_Unnamed";
            return "_" + markerName;
        }

        if (rawDescription.StartsWith(".", StringComparison.Ordinal))
        {
            var baseName = ToCodeIdentifierPreservingCase(rawDescription[1..]);
            return "_" + baseName;
        }

        var prefixed = Regex.Match(rawDescription.Trim(), @"^([A-Za-z]+\d+)\s*[_\-\s]+\s*(.+)$");
        if (prefixed.Success)
        {
            var prefix = ToCodeIdentifierPreservingCase(prefixed.Groups[1].Value);
            var tail = ToTaskTailIdentifier(prefixed.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(tail))
                return prefix;
            return prefix + "_" + tail;
        }

        if (Regex.IsMatch(rawDescription, @"[-_()/\[\]]"))
        {
            var id = ToTaskTailIdentifier(rawDescription);
            if (string.IsNullOrWhiteSpace(id))
                return "UnnamedTask";
            return id;
        }

        return ToPascalIdentifier(rawDescription);
    }

    private static string ToTaskTailIdentifier(string rawTail)
    {
        if (string.IsNullOrWhiteSpace(rawTail))
            return "";

        var normalized = rawTail.Trim();
        var segments = new List<(string Separator, string Value)>();
        var current = new StringBuilder();
        var pendingSeparator = "";

        void FlushCurrent()
        {
            var value = current.ToString().Trim();
            if (value.Length == 0)
                return;
            segments.Add((pendingSeparator, value));
            current.Clear();
            pendingSeparator = "";
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                current.Append(ch);
                continue;
            }

            FlushCurrent();

            if (ch is '-' or '_' or '(' or ')' or '[' or ']' or '/')
                pendingSeparator = "_";
        }

        FlushCurrent();
        if (segments.Count == 0)
            return "";

        var formattedSegments = new List<string>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var useCamel = i > 0 &&
                           segments[i].Separator == "_" &&
                           SegmentLooksLikeShortPrefix(segments[i - 1].Value) &&
                           SegmentStartsLowercase(segments[i].Value);
            var formatted = BuildSoftTaskSegmentIdentifier(segments[i].Value, useCamel);
            if (!string.IsNullOrWhiteSpace(formatted))
                formattedSegments.Add(formatted);
        }

        if (formattedSegments.Count == 0)
            return "";

        var id = string.Join("_", formattedSegments);
        if (char.IsDigit(id[0]))
            id = "_" + id;
        return id;
    }

    private static string BuildSoftTaskSegmentIdentifier(string rawSegment, bool camelCaseFirstToken = false)
    {
        if (string.IsNullOrWhiteSpace(rawSegment))
            return "";

        var tokens = Regex.Matches(rawSegment, "[A-Za-z0-9]+")
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0)
            return "";

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var piece = char.ToUpperInvariant(token[0]) + token[1..];
            if (i == 0)
            {
                if (camelCaseFirstToken && token.Length > 0)
                    piece = char.ToLowerInvariant(piece[0]) + piece[1..];
                sb.Append(piece);
                continue;
            }

            var previous = tokens[i - 1];
            var hasSingleLetterBridge = previous.Length == 1 && token.Length > 1;
            if (hasSingleLetterBridge)
                sb.Append('_');
            sb.Append(piece);
        }

        return sb.ToString();
    }

    private static bool SegmentLooksLikeShortPrefix(string value)
    {
        var token = Regex.Match(value ?? "", "[A-Za-z0-9]+").Value;
        return token.Length is > 0 and <= 2;
    }

    private static bool SegmentStartsLowercase(string value)
    {
        var token = Regex.Match(value ?? "", "[A-Za-z0-9]+").Value;
        return token.Length > 0 && char.IsLower(token[0]);
    }

    private static string ToPascalIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unnamed";
        var parts = Regex.Split(value, @"[^A-Za-z0-9]+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (parts.Length == 0)
            return "Unnamed";
        var result = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    private static string ResolveFormIoKind(TaskFormIoDef formIo)
    {
        if (string.Equals(formIo.OperationType, "I", StringComparison.OrdinalIgnoreCase))
            return "FileReader";
        if (string.Equals(formIo.OperationType, "O", StringComparison.OrdinalIgnoreCase))
            return "FileWriter";
        if (string.Equals(formIo.OperationType, "W", StringComparison.OrdinalIgnoreCase))
            return "TextPrinterWriter";
        return "PrinterWriter";
    }

    private static string BuildRaw(string? left, string right, string? trace)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(left))
            sb.Append(left.Trim());
        if (sb.Length > 0)
            sb.Append(" | ");
        sb.Append(right.Trim());
        if (!string.IsNullOrWhiteSpace(trace))
        {
            sb.Append(" | ");
            sb.Append(trace.Trim());
        }
        return sb.ToString();
    }
}
