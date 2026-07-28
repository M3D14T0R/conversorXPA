using System.Text;
using System.Xml.Linq;

namespace XpaConverterMvp;

internal sealed record FieldModelDef(
    int Ordinal,
    int LocalObjectIndex,
    string Name,
    string AttrObj,
    string Picture,
    string? InputRange,
    string? NullDisplayText,
    string? DefaultValue,
    string? RaiseEventType,
    int? RaiseEventInternalEventId,
    int? RaiseEventKeyCombinationId,
    string? Dec,
    string? Whole,
    bool? Negative,
    bool? Flip,
    int? FieldStyle,
    string? Comment,
    int? DitIndexForToolkit,
    string? SelectProgramObj,
    string? PublicName,
    string? SourceComponent
);

internal sealed record ControlButtonModelDef(
    int Obj,
    string Name,
    string? Format,
    int? InternalEventId,
    int? FontSchemeId
);

internal sealed record DataColumnDef(
    int Id,
    string Name,
    string AttrObj,
    string Picture,
    string? FieldPhysicalName,
    string? FieldPhysicalPicture,
    int? FieldPhysicalSize,
    bool? AllowedNull,
    string? Attribute,
    string? ContextCookies,
    string? DatabaseDefinition,
    string? Storage,
    string? Translate,
    string? DbColumnName,
    string? DbType,
    string? ModelRefObj,
    string? InputRange
);

internal sealed record IndexSegmentDef(int ColumnId, string Order);

internal sealed record DataIndexDef(
    int Id,
    string Name,
    bool Unique,
    bool Primary,
    IReadOnlyList<IndexSegmentDef> Segments
);

internal sealed record DataObjectDef(
    int Ordinal,
    string Name,
    string PhysicalName,
    string? Comment,
    bool? Resident,
    string? PublicName,
    string? SourceComponent,
    string? Owner,
    string DataSource,
    IReadOnlyList<DataColumnDef> Columns,
    IReadOnlyList<DataIndexDef> Indexes,
    string? TypeNameOverride = null
);

internal sealed record TaskSqlWhereArgumentDef(
    int ParentLevel,
    int VariableOrdinal,
    bool ContentAsIs
);

internal sealed record TaskSqlWhereDef(
    string Format,
    IReadOnlyList<TaskSqlWhereArgumentDef> Arguments
);

internal sealed record TaskVarRangeInfoDef(
    string Mode,
    int VarRangeVeeIsn
);

internal sealed record TaskSqlFormInputArgumentDef(
    int? ExpressionId,
    string? Variable
);

internal sealed record TaskSqlFormDef(
    string? DatabaseName,
    string? Restab,
    string Statement,
    IReadOnlyList<TaskSqlFormInputArgumentDef> InputArguments,
    IReadOnlyList<string> OutputVariables
);

internal sealed record TaskDef(
    int Ordinal,
    int? TopLevelProgramIndex,
    int? TopLevelProgramIndexLocal,
    int? ParentOrdinal,
    int? SubtaskIndex,
    string Description,
    string? Folder,
    bool IsEmptyTask,
    string? PublicName,
    bool Resident,
    string TaskType,
    int? DeclaredParameterCount,
    TaskReturnValueDef? ReturnValue,
    int? ReturnValueExpressionId,
    bool ParallelExecution,
    bool CopyGlobalParameters,
    bool SingleInstance,
    string? TaskId,
    string? Icon,
    bool MainProgram,
    string? Cnfu,
    string? Clss,
    string? MgAttr,
    string? Pdod,
    string? Propagate,
    string InitialMode,
    string LocateDirection,
    string RangeDirection,
    int? LocateExpressionId,
    int? RangeExpressionId,
    int? InitialModeExpressionId,
    int? TotalVariabls,
    int? TotalVirtuals,
    bool EndTaskCondition,
    int? EndTaskConditionExpressionId,
    string EvaluateEndCondition,
    string LockingStrategy,
    string TransactionMode,
    string TransactionBegin,
    string ErrorStrategy,
    bool SelectionTable,
    string CacheStrategy,
    bool ForceRecordSuffix,
    bool? AllowEmptyDataview,
    bool? PreloadView,
    bool? AllowActivitySwitch,
    bool? AllowQuery,
    bool? AllowModify,
    bool? AllowCreate,
    bool? AllowDelete,
    bool AllowEvents,
    bool OpenTaskWindow,
    bool CloseTaskWindow,
    bool AllowPrintingData,
    string? AllowCreateExpression,
    TaskSqlWhereDef? SqlWhere,
    IReadOnlyList<TaskVarRangeInfoDef> VarRangeInfos,
    TaskSqlFormDef? SqlForm,
    string? FormName,
    TaskFormDef? Form,
    IReadOnlyList<int> ResourceDataObjects,
    IReadOnlyList<TaskResourceDbDef> ResourceDbs,
    IReadOnlyList<TaskResourceColumnDef> ResourceColumns,
    int? InformationDbObj,
    int? InitialKeyIndexId,
    int? InitialKeyExpressionId,
    int? PrimaryDbObj,
    IReadOnlyList<TaskSortSegmentDef> SortSegments,
    IReadOnlyList<TaskLogicSelectDef> Selects,
    IReadOnlyList<TaskLogicLinkDef> Links,
    IReadOnlyList<TaskDataViewSourceDef> DataViewSources,
    IReadOnlyList<TaskBlockDef> Blocks,
    IReadOnlyList<TaskEndBlockDef> EndBlocks,
    IReadOnlyList<TaskEndLinkDef> EndLinks,
    IReadOnlyList<TaskCallDef> TabCalls,
    IReadOnlyList<TaskRowLogicDef> StartLogics,
    IReadOnlyList<TaskRaiseEventDef> StartRaises,
    IReadOnlyList<TaskRowLogicDef> EndLogics,
    IReadOnlyList<TaskRaiseEventDef> EndRaises,
    bool HasStartLogicUnit,
    bool HasEndLogicUnit,
    IReadOnlyList<TaskRowLogicDef> RowLogics,
    IReadOnlyList<TaskRowLogicDef> SavingRowLogics,
    IReadOnlyList<TaskValidationDef> FlowValidations,
    IReadOnlyList<TaskEventDef> Events,
    IReadOnlyList<TaskExpressionDef> Expressions,
    IReadOnlyList<TaskFunctionOverrideDef> FunctionOverrides,
    IReadOnlyList<TaskHandlerDef> Handlers,
    IReadOnlyList<TaskGroupLogicDef> GroupLogics,
    IReadOnlyList<TaskGapDef> Gaps,
    int? DisplayExpressionId,
    IReadOnlyList<TaskFormEntryDef> FormEntries,
    IReadOnlyList<TaskFormIoDef> FormIos,
    TaskIoDef? Io,
    IReadOnlyList<TaskIoDef> Ios,
    string? SourceComponent
);

internal sealed record TaskDataViewSourceDef(
    int? Index,
    string? Type,
    bool? Enabled,
    int? ConditionExpressionId,
    string? XmlTrace
);

internal sealed record TaskBlockDef(
    string Type,
    int? ConditionExpressionId,
    bool? ConditionLiteral,
    int? LogicLineIndex,
    int? EndBlockLine,
    int? EndBlockSegmentLine,
    string? XmlTrace
);

internal sealed record TaskEndBlockDef(
    int? LogicLineIndex,
    string? XmlTrace
);

internal sealed record TaskEndLinkDef(
    string? XmlTrace
);

internal sealed record TaskFunctionOverrideDef(
    string Name,
    string? Scope,
    int? ReturnExpressionId,
    string? Comment,
    IReadOnlyList<TaskFunctionParameterDef> Parameters,
    IReadOnlyList<TaskRemarkDef> Remarks,
    IReadOnlyList<TaskRowActionDef> Actions,
    IReadOnlyList<TaskBlockDef> Blocks,
    IReadOnlyList<TaskEndBlockDef> EndBlocks,
    string? XmlTrace
);

internal sealed record TaskFunctionParameterDef(
    string SelectName,
    int ColumnId,
    int? AssignmentExpressionId,
    string? XmlTrace
);

internal sealed record TaskGapDef(
    string Scope,
    string Message,
    string? XmlTrace
);

internal sealed record TaskResourceDbDef(
    int DataObject,
    string? Access,
    bool? Cache,
    string? EntityNameExpressionRef
);

internal sealed record TaskSortSegmentDef(
    int FieldId,
    string Direction
);

internal sealed record TaskFormEntryDef(
    int Index,
    int? ClassIndex,
    string Model,
    TaskFormDef Form
);

internal sealed record TaskFormDef(
    int Width,
    int Height,
    int X,
    int Y,
    string? FormName,
    string? FormText,
    int? FormTextExpressionId,
    int? XExpressionId,
    int? YExpressionId,
    int? ColorSchemeId,
    int? FontSchemeId,
    int? PulldownMenuObj,
    bool? SystemMenu,
    bool? MinimizeButton,
    bool? MaximizeButton,
    string? FormUnits,
    int? VerticalFactor,
    int? HorizontalFactor,
    string? WindowType,
    string? StartupMode,
    string? StartupPosition,
    string? PersistentFormState,
    string? Placement,
    int? PlacementTop,
    int? PlacementBottom,
    int? PlacementLeft,
    int? PlacementRight,
    bool? TitleBar,
    IReadOnlyList<TaskFormControlDef> Controls,
    string? MergeFileName,
    int? MergeFileNameExpressionId,
    IReadOnlyList<TaskMergeTagDef> MergeTags
);

internal sealed record TaskMergeTagDef(
    int Id,
    string Name,
    string? Picture,
    int? ExpressionId,
    string? Column
);

internal sealed record MenuDef(
    int Obj,
    string Name,
    string? MenuType,
    int? ToolNumber,
    IReadOnlyList<MenuEntryDef> Entries
);

internal sealed record MenuEntryDef(
    string MenuType,
    string? Description,
    int? ProgramObj,
    string? ProgramDescription,
    MenuEventDef? Event,
    int? ToolNumber,
    int? ToolGroup,
    IReadOnlyList<MenuEntryDef> Children
);

internal sealed record MenuEventDef(
    string EventType,
    int? InternalEventId,
    string? PublicObject
);

internal sealed record ProjectMenuSettingsDef(
    int? SystemPulldownMenuObj,
    int? SystemContextMenuObj
);

internal sealed record TaskFormControlDef(
    int FormEntryIndex,
    int Id,
    int? ParentId,
    string Model,
    int X,
    int Y,
    int Width,
    int Height,
    int? PlacementX,
    int? PlacementWidth,
    int? PlacementY,
    int? PlacementHeight,
    int? ControlLayer,
    string? BorderStyle,
    int? TitleHeight,
    int? RowHeight,
    int? TabOrder,
    int? HorizontalAlignment,
    string? Text,
    string? DefaultImageFile,
    string? ItemsList,
    int? GridX,
    int? GridY,
    bool? Modifiable,
    int? ModifiableExpressionId,
    bool? ModifyInQuery,
    bool? MultiLineEdit,
    bool? AllowCrInData,
    bool? EnableRtf,
    bool? Line3D,
    int? LineWidth,
    bool? AutoExpand,
    string? ExpansionWindow,
    bool? ParkOnClick,
    bool? LineDivider,
    bool? ColumnDivider,
    string? SetTableColorBy,
    int? AlternatingBgColor,
    string? GradientStyle,
    string? GradientColor,
    string? ImageStyle,
    string? RowHighlightStyle,
    string? WallpaperStyle,
    string? CheckboxMainStyle,
    string? UpdateStyle,
    bool? ShowGrid,
    bool? ShowButtons,
    bool? ShowLines,
    bool? ShowRoot,
    bool? ShowScrollBars,
    bool? ShowInWindowMenu,
    bool? TopBorder,
    int? TopBorderMargin,
    string? SelectionMode,
    string? TabControlSide,
    string? ColumnTitle,
    bool? Sortable,
    string? DataColumn,
    int? DataExpressionId,
    int? SelectProgramObj,
    int? SelectProgramComponentId,
    string? SelectMode,
    string? ToolTipText,
    int? ToolTipExpressionId,
    int? SourceTableObj,
    int? DisplayFieldObj,
    int? LinkFieldObj,
    int? IndexObj,
    int? ModelRefObj,
    int? ModelVarColumn,
    string? ControlName,
    int? ColorSchemeId,
    int? HoveringColorSchemeId,
    int? VisitedColorSchemeId,
    int? FontSchemeId,
    int? StyleValue,
    int? ButtonStyleValue,
    int? InternalStyleValue,
    int? InternalLineStyleValue,
    string? Orientation,
    bool? TabInto,
    bool? AllowParking,
    bool? VerticalScroll,
    int? TabbingOrder,
    int? Bottom,
    string? StaticType,
    string? WindowSort,
    string? WindowSortBy,
    int? WindowWidth,
    bool? VisibleValue,
    int? VisibleExpressionId,
    bool? EnabledValue,
    int? EnabledExpressionId,
    string? RaiseEventType,
    string? RaiseEventObject,
    int? RaiseEventParent,
    int? RaiseEventPublicComponentId,
    int? RaiseEventInternalEventId,
    int? RaiseEventKeyCombinationId,
    int? SubformComponentId,
    string? SubformTargetComponentName,
    string? SubformTargetPublicName,
    int? SubformTaskNumber,
    int? SubformConnectTo,
    IReadOnlyList<string> SubformArguments,
    IReadOnlyList<TaskArgumentDef> SubformArgumentDefs,
    string? TreeDescriptionColumn,
    int? TreeDescriptionExpressionId,
    string? TreeNodeIdColumn,
    int? TreeNodeIdExpressionId,
    string? TreeParentIdColumn,
    int? TreeParentIdExpressionId,
    int? TreeRootExpressionId,
    IReadOnlyList<int> PropertyExpressionIds
);

internal sealed record TaskArgumentDef(
    int? Id,
    string? Variable,
    bool? Skip,
    int? ExpressionId,
    int? Parent,
    string? Name,
    string? Type,
    string? VtType,
    string? TypeLibrary,
    string? ObjectName,
    string? DotNetType,
    int? Exp
);

internal sealed record TaskReturnValueDef(
    string? Value,
    string? MgAttr,
    IReadOnlyList<string> ParameterAttributes,
    int? TaskParameters,
    int? ParametersCount
);

internal sealed record TaskResourceColumnDef(
    int Id,
    string Name,
    string AttrObj,
    string? ModelRefObj,
    string? ObjectType,
    string? Picture,
    string? InputRange,
    bool? AllowNull,
    string? NullDisplayText,
    string? DefaultValue,
    string? RaiseEventType,
    int? RaiseEventInternalEventId,
    int? RaiseEventKeyCombinationId,
    bool HasControlModelOverride,
    bool ClearsInheritedExpandEvent,
    int? DefinitionId,
    string? CellModelAttrObj,
    int? CellModelObj
);

internal sealed record TaskLogicSelectDef(
    string Name,
    int ColumnId,
    string Type,
    string? OriginLevel,
    string? OriginType,
    bool IsParameter,
    bool IsFunctionSelect,
    int? SourceDbObj,
    int? SourceLinkSequence,
    int? AssignmentExpressionId,
    int? OleSubformInfo,
    bool HasRange,
    int? RangeMin,
    int? RangeMax,
    bool HasLocate,
    int? LocateMin,
    int? LocateMax,
    string? RealVarName,
    bool? PartOfDataview,
    string? ExposedToRoute,
    string? DisplayName,
    IReadOnlyList<int> InternalCompareInfo,
    IReadOnlyList<int> InternalDitInfo,
    string? XmlTrace
);

internal sealed record TaskLogicLinkDef(
    int DbObj,
    string Direction,
    int? KeyIndexId,
    string? SortType,
    string? Mode,
    string? ReturnValueName,
    int? ConditionExpressionId,
    string? EvaluateConditionMode,
    string? View,
    string? Views,
    int? FieldId,
    bool Expanded,
    string? XmlTrace
);

internal sealed record TaskHandlerDef(
    string Level,
    string Type,
    string Scope,
    string? Propagate,
    string? Reference,
    int? ConditionExpressionId,
    string EventType,
    int? EventTime,
    int? EventInternalEventId,
    int? EventKeyCombinationId,
    int? EventParent,
    int? EventPublicComponentId,
    string? EventPublicObject,
    string? EventExpression,
    IReadOnlyList<int> ParameterColumnIds,
    IReadOnlyList<TaskRaiseEventDef> Raises,
    IReadOnlyList<TaskInvokeDef> Invokes,
    IReadOnlyList<TaskCallDef> Calls,
    IReadOnlyList<TaskUpdateDef> Updates,
    IReadOnlyList<TaskStopDef> Stops,
    IReadOnlyList<TaskRemarkDef> Remarks,
    IReadOnlyList<TaskFormIoDef> FormIos,
    IReadOnlyList<TaskRowActionDef> Actions,
    IReadOnlyList<TaskBlockDef> Blocks,
    IReadOnlyList<TaskEndBlockDef> EndBlocks,
    bool IsRmCompatibleControlHandler,
    string? XmlTrace
);

internal sealed record TaskUpdateDef(
    string Variable,
    string WithValue,
    int? ConditionExpressionId,
    bool ForcedUpdate,
    bool Incremental,
    string? Parent,
    string? Modifier,
    string? Direction,
    bool Disabled,
    string? XmlTrace
);
internal sealed record TaskCallDef(
    int? TaskId,
    int? TargetComponentId,
    int? TargetObjectId,
    string? TargetComponentName,
    string? TargetPublicName,
    string OperationType,
    IReadOnlyList<string> ArgumentVariables,
    IReadOnlyList<TaskArgumentDef> ArgumentDefs,
    string? ReturnVariable,
    TaskReturnValueDef? ReturnValue,
    int? ConditionExpressionId,
    string? Direction,
    string? Modifier,
    string? Page,
    int? IoDeviceIndex,
    int? FormEntryIndex,
    bool? WaitForCompletion,
    string? Lock,
    string? SyncData,
    bool? RetainFocus,
    string? EventType,
    int? EventInternalEventId,
    string? DestSubformName,
    bool Disabled,
    string? FunctionName,
    string? SnippetCode,
    string? CompiledCode,
    bool? IsRoute,
    string? RoutePath,
    string? XmlTrace
);
internal sealed record TaskStopDef(
    string Mode,
    string? Text,
    int? ExpressionId,
    string? TitleText,
    string? Buttons,
    string? Image,
    string? VisualDisplay,
    int? DefaultButton,
    bool? AppendToErrorLog,
    int? ConditionExpressionId,
    string? XmlTrace
);
internal sealed record TaskRowLogicDef(
    IReadOnlyList<TaskRowActionDef> Actions,
    IReadOnlyList<TaskRaiseEventDef> Raises,
    IReadOnlyList<TaskBlockDef> Blocks,
    IReadOnlyList<TaskEndBlockDef> EndBlocks
);

internal sealed record TaskRowActionDef(
    string Kind,
    TaskCallDef? Call,
    TaskUpdateDef? Update,
    TaskStopDef? Stop,
    TaskInvokeDef? Invoke,
    int? EvaluateExpressionId,
    string? EvaluateReturnVariable,
    int? ConditionExpressionId,
    bool? ConditionLiteral,
    int? LoopConditionExpressionId,
    string? XmlTrace,
    string? RemarkText
);

internal sealed record TaskRemarkDef(
    string Text,
    string? XmlTrace
);

internal sealed record TaskInvokeDef(
    string OperationType,
    string? EventType,
    int? TaskIdExpressionId,
    int? CabinetNameExpressionId,
    int? ProgramNameExpressionId,
    int? CommandExpressionId,
    string? ReturnVariable,
    string? FunctionName,
    string? SnippetCode,
    string? SnippetLanguage,
    IReadOnlyList<string> ArgumentVariables,
    IReadOnlyList<TaskArgumentDef> ArgumentDefs,
    TaskReturnValueDef? ReturnValue,
    int? ConditionExpressionId,
    bool? WaitForCompletion,
    string? Show,
    string? Lock,
    string? SyncData,
    bool? RetainFocus,
    string? MethodName,
    string? Option,
    string? PropertyName,
    string? Convention,
    string? ServiceName,
    string? SoapAction,
    string? OperationName,
    string? Namespace,
    int? FieldId1,
    int? FieldId2,
    int? FieldId3,
    string? XmlTrace
);

internal sealed record TaskGroupLogicDef(
    string? Reference,
    string Type,
    IReadOnlyList<TaskRowActionDef> Actions
);

internal sealed record TaskRaiseEventDef(
    string EventType,
    int? EventInternalEventId,
    int? EventKeyCombinationId,
    int? EventParent,
    int? EventPublicComponentId,
    string? EventPublicObject,
    string? DestinationContext,
    int? ConditionExpressionId,
    string? Modifier,
    string? Direction,
    bool? WaitForCompletion,
    bool Disabled,
    IReadOnlyList<string> ArgumentExpressionIds,
    IReadOnlyList<TaskArgumentDef> ArgumentDefs,
    string? XmlTrace
);

internal sealed record TaskValidationDef(
    int? ConditionExpressionId,
    int? MessageExpressionId
);

internal sealed record TaskFormIoDef(
    string Level,
    string Type,
    string? Reference,
    string OperationType,
    int? FormEntryIndex,
    string? Page,
    string? Delimiter,
    int? DelimiterChar,
    int? IoDeviceIndex,
    int? ConditionExpressionId,
    int? LoopConditionExpressionId,
    string? Modifier,
    string? Direction,
    int? IoDeviceParent,
    string? XmlTrace
);

internal sealed record TaskIoDef(
    string? Description,
    string? Machine,
    bool? PrintPreview,
    bool? OpenPrintDialog,
    bool? PrintingAllowed,
    string? PaperSize,
    int? Copies,
    bool? Pdf,
    bool? ContentCopyingAllowed,
    bool? ChangesAllowed,
    bool? PageLayoutAllowed,
    string? Vis2LogTranslation,
    bool? FlipLines,
    int? PageHeaderFormEntryIndex,
    int? PageFooterFormEntryIndex,
    int? IoExpressionId,
    int? IoToUseColumnId,
    string? Media,
    string? Access,
    int? Bottom,
    string? Charset,
    string? ColumnRef
);

internal sealed record TaskEventDef(
    int Ordinal,
    string Description,
    string? EventType,
    int? InternalEventId,
    int? EventKeyCombinationId,
    int? FieldId,
    string? ForceExit,
    string? PublicName,
    string? EventText,
    string? ErrorTrigger,
    IReadOnlyList<TaskEventParameterDef> Parameters
);

internal sealed record TaskEventParameterDef(
    string Name,
    string? Attr,
    string? Picture
);

internal sealed record TaskExpressionDef(int Ordinal, string Syntax, string Attribute);

internal sealed record RightDef(
    string Name,
    string Key,
    string? PublicName,
    string? SourceComponent
);

internal sealed record ComponentRightRefDef(
    int ComponentId,
    int RightId,
    string RightName,
    string ComponentName
);

internal sealed record DotNetComponentReferenceDef(
    string Name,
    string? Description,
    string? Folder,
    string? AssemblyName,
    string? AssemblyPath,
    string? UseSpecificVersion
);

internal sealed record ComponentFunctionDef(
    string Name,
    string ComponentName
);

internal sealed record ExternalMagicComponentDef(
    string Name,
    string? Description,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> DataObjects,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> Rights,
    bool HasTablesXml
);

internal sealed class ParsedXpa
{
    public List<FieldModelDef> FieldModels { get; } = new();
    public List<ControlButtonModelDef> ControlButtonModels { get; } = new();
    public List<DataObjectDef> DataObjects { get; } = new();
    public List<TaskDef> Tasks { get; } = new();
    public List<RightDef> Rights { get; } = new();
    public List<ComponentRightRefDef> ComponentRightRefs { get; } = new();
    public Dictionary<string, int> ComponentDataSourcesByLiteral { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Components { get; } = new();
    public List<ExternalMagicComponentDef> ExternalMagicComponents { get; } = new();
    public List<DotNetComponentReferenceDef> DotNetComponentReferences { get; } = new();
    public List<ComponentFunctionDef> ComponentFunctions { get; } = new();
    public List<MenuDef> Menus { get; } = new();
    public ProjectMenuSettingsDef ProjectMenuSettings { get; set; } = new(null, null);
    public bool HasRepositoryProperties { get; set; }
    public bool HasDataRepositoryProperties { get; set; }
    public bool HasHelpRepository { get; set; }
    public bool HasXsdUnderViewCompound { get; set; }
}

internal sealed class ProjectSemantic
{
    public List<FieldModelDef> FieldModels { get; } = new();
    public List<ControlButtonModelDef> ControlButtonModels { get; } = new();
    public List<DataObjectDef> DataObjects { get; } = new();
    public List<TaskSemantic> Tasks { get; } = new();
    public List<RightDef> Rights { get; } = new();
    public List<ComponentRightRefDef> ComponentRightRefs { get; } = new();
    public List<string> Components { get; } = new();
    public List<ExternalMagicComponentDef> ExternalMagicComponents { get; } = new();
    public List<DotNetComponentReferenceDef> DotNetComponentReferences { get; } = new();
    public List<ComponentFunctionDef> ComponentFunctions { get; } = new();
    public List<MenuDef> Menus { get; } = new();
    public ProjectMenuSettingsDef ProjectMenuSettings { get; set; } = new(null, null);
    public string ApplicationViewClassName { get; set; } = "ApplicationView";
    public Dictionary<string, string> ApplicationSelectMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, Dictionary<string, string>> ParentSelectMapByTaskOrdinal { get; } = new();
    public Dictionary<string, ComponentRightSemantic> ComponentRightsByLiteral { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ComponentDataSourcesByLiteral { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> DataSourceTypeByObjectOrdinal { get; } = new();
    public HashSet<int> UsedButtonModelObjectIds { get; } = new();
    public bool HasRepositoryProperties { get; set; }
    public bool HasDataRepositoryProperties { get; set; }
    public bool HasHelpRepository { get; set; }
    public bool HasXsdUnderViewCompound { get; set; }
}

internal sealed record ComponentRightSemantic(
    string? ComponentName,
    string RoleMemberName
);

internal sealed record TaskSemantic(
    int Ordinal,
    int? TopLevelProgramIndex,
    int? TopLevelProgramIndexLocal,
    int? ParentOrdinal,
    int? SubtaskIndex,
    string Description,
    string? Folder,
    bool IsEmptyTask,
    string? PublicName,
    bool Resident,
    string TaskType,
    int? DeclaredParameterCount,
    TaskReturnValueDef? ReturnValue,
    int? ReturnValueExpressionId,
    bool ParallelExecution,
    bool CopyGlobalParameters,
    bool SingleInstance,
    string? TaskId,
    string? Icon,
    bool MainProgram,
    string? Cnfu,
    string? Clss,
    string? MgAttr,
    string? Pdod,
    string? Propagate,
    string InitialMode,
    string LocateDirection,
    string RangeDirection,
    int? LocateExpressionId,
    int? RangeExpressionId,
    int? TotalVariabls,
    int? TotalVirtuals,
    bool EndTaskCondition,
    int? EndTaskConditionExpressionId,
    string EvaluateEndCondition,
    string LockingStrategy,
    string TransactionMode,
    string TransactionBegin,
    string ErrorStrategy,
    bool SelectionTable,
    string CacheStrategy,
    bool ForceRecordSuffix,
    bool? AllowEmptyDataview,
    bool? PreloadView,
    bool? AllowActivitySwitch,
    bool? AllowQuery,
    bool? AllowModify,
    bool? AllowCreate,
    bool? AllowDelete,
    bool AllowEvents,
    bool OpenTaskWindow,
    bool CloseTaskWindow,
    bool AllowPrintingData,
    string? AllowCreateExpression,
    TaskSqlWhereDef? SqlWhere,
    IReadOnlyList<TaskVarRangeInfoDef> VarRangeInfos,
    TaskSqlFormDef? SqlForm,
    string? FormName,
    TaskFormDef? Form,
    IReadOnlyList<int> ResourceDataObjects,
    IReadOnlyList<TaskResourceDbDef> ResourceDbs,
    IReadOnlyList<TaskResourceColumnDef> ResourceColumns,
    int? InformationDbObj,
    int? InitialKeyIndexId,
    int? InitialKeyExpressionId,
    int? PrimaryDbObj,
    IReadOnlyList<TaskSortSegmentDef> SortSegments,
    IReadOnlyList<TaskLogicSelectDef> Selects,
    IReadOnlyList<TaskLogicLinkDef> Links,
    IReadOnlyList<TaskCallDef> TabCalls,
    IReadOnlyList<TaskRowLogicDef> StartLogics,
    IReadOnlyList<TaskRaiseEventDef> StartRaises,
    IReadOnlyList<TaskRowLogicDef> EndLogics,
    IReadOnlyList<TaskRaiseEventDef> EndRaises,
    bool HasStartLogicUnit,
    bool HasEndLogicUnit,
    IReadOnlyList<TaskRowLogicDef> RowLogics,
    IReadOnlyList<TaskRowLogicDef> SavingRowLogics,
    IReadOnlyList<TaskValidationDef> FlowValidations,
    IReadOnlyList<TaskEventDef> Events,
    IReadOnlyList<TaskExpressionDef> Expressions,
    IReadOnlyList<TaskFunctionOverrideDef> FunctionOverrides,
    IReadOnlyList<TaskHandlerDef> Handlers,
    IReadOnlyList<TaskGroupLogicDef> GroupLogics,
    IReadOnlyList<TaskGapDef> Gaps,
    int? DisplayExpressionId,
    IReadOnlyList<TaskFormEntryDef> FormEntries,
    IReadOnlyList<TaskFormIoDef> FormIos,
    TaskIoDef? Io,
    IReadOnlyList<TaskIoDef> Ios,
    string? SourceComponent,
    string Name,
    DataViewSemantic DataView,
    SelectSemantic SelectsSemantic,
    ResourceSemantic ResourcesSemantic,
    ExecutionSemantic Execution,
    IOSemantic IO,
    EventSemantic EventsSemantic,
    HandlerSemantic HandlersSemantic,
    ExpressionSemantic ExpressionsSemantic,
    LogicSemantic Logic,
    IReadOnlyList<FunctionOverrideSemantic> FunctionOverridesSemantic,
    ViewSemantic View,
    LayoutSemantic Layout,
    MergeSemantic Merge,
    ReportSemantic Report,
    IReadOnlyList<UnhandledSemantic> Unhandled
);

internal sealed record DataViewSemantic(
    int? PrimaryDataObject,
    IReadOnlyList<int> ResourceDataObjects,
    IReadOnlyList<TaskLogicSelectDef> Columns,
    IReadOnlyList<TaskLogicLinkDef> Links,
    IReadOnlyList<TaskCallDef> TabCalls,
    IReadOnlyList<TaskSortSegmentDef> OrderBySegments,
    IReadOnlyList<TaskLogicSelectDef> Ranges,
    IReadOnlyList<TaskLogicSelectDef> Locates,
    bool HasFrom,
    bool HasOrderBy
);

internal sealed record SelectSemantic(
    IReadOnlyList<TaskLogicSelectDef> Items,
    IReadOnlyDictionary<string, string> NameToExpression,
    IReadOnlyDictionary<string, TaskLogicSelectDef> ItemsByName,
    IReadOnlyDictionary<int, TaskLogicSelectDef> ParameterByColumnId
);

internal sealed record ResourceSemantic(
    IReadOnlyList<TaskResourceColumnDef> Ordered,
    IReadOnlyDictionary<int, TaskResourceColumnDef> ById,
    IReadOnlyDictionary<string, TaskResourceColumnDef> ByName,
    IReadOnlyDictionary<string, TaskResourceColumnDef> ByLegacyName,
    TaskResourceColumnDef? FirstBlob
);

internal sealed record ExecutionSemantic(
    string TaskType,
    string TransactionMode,
    string TransactionBegin,
    string LockingStrategy,
    string ErrorStrategy,
    bool ParallelExecution,
    bool EndTaskCondition,
    int? EndTaskConditionExpressionId,
    string EvaluateEndCondition,
    string? Activity,
    int? ActivityExpressionId,
    string? RowLocking,
    string? TransactionScope,
    bool RetryOnDatabaseError,
    bool KeepChildRelationCacheAlive,
    bool EnableSelectionTableFlow,
    bool SwitchToInsertWhenNoRows,
    bool AllowExportData,
    string? ExitTiming
);

internal sealed record IOSemantic(
    IReadOnlyList<IOSourceSemantic> Sources,
    bool HasPrinterWriter,
    bool HasTextPrinterWriter,
    bool HasFileWriter,
    bool HasMultipleSources
);

internal sealed record IOSourceSemantic(
    string Name,
    string Kind,
    bool PrintPreview,
    bool OpenPrintDialog,
    bool PrintingAllowed,
    string? Media,
    string? Access,
    int? IoExpressionId,
    string? Page,
    int? IoDeviceIndex
);

internal sealed record EventSemantic(
    bool HasPageHeader,
    bool HasPageFooter,
    bool HasRecordPrefix,
    bool HasRecordSuffix,
    bool HasTaskPrefix,
    bool HasTaskSuffix,
    bool HasStart,
    bool HasEnd,
    IReadOnlyList<TaskEventDef> Items,
    IReadOnlyDictionary<string, string> CommandByDescription,
    IReadOnlyDictionary<int, TaskEventDef> ItemsByOrdinal,
    IReadOnlyDictionary<int, string> DescriptionByOrdinal,
    IReadOnlyDictionary<int, string> CommandByOrdinal
);

internal sealed record HandlerSemantic(
    IReadOnlyList<TaskHandlerDef> Items,
    bool HasCustomCommands,
    bool HasRaise,
    bool HasInvokes,
    bool HasCalls,
    bool HasUpdates,
    bool HasStops,
    IReadOnlyList<TaskHandlerDef> ValueChangedHandlers,
    IReadOnlyList<TaskHandlerDef> UserCommandHandlers,
    IReadOnlyList<TaskHandlerDef> InternalHandlers,
    IReadOnlyList<TaskHandlerDef> ExpressionHandlers,
    IReadOnlyList<TaskHandlerDef> TimerHandlers,
    IReadOnlyList<TaskHandlerDef> SystemHandlers,
    IReadOnlyList<TaskHandlerDef> RecordHandlers,
    IReadOnlyList<TaskHandlerDef> UnmappedHandlers,
    IReadOnlyList<HandlerBodySemantic> Bodies
);

internal sealed record HandlerBodySemantic(
    TaskHandlerDef Handler,
    IReadOnlyList<TaskRowActionDef> OrderedActions,
    IReadOnlyList<HandlerActionBlockSemantic> Blocks
);

internal sealed record HandlerActionBlockSemantic(
    string Kind,
    int StartIndex,
    int Length,
    string? ConditionKey
);

internal sealed record ExpressionSemantic(
    IReadOnlyList<TaskExpressionDef> Items,
    bool HasEop,
    bool HasIoCurr,
    bool HasPage,
    bool HasLine,
    bool HasStr,
    IReadOnlyList<ExpressionEntrySemantic> Entries,
    IReadOnlyDictionary<int, ExpressionEntrySemantic> EntriesByOrdinal,
    IReadOnlyDictionary<int, string> InvocationByOrdinal
);

internal sealed record ExpressionEntrySemantic(
    int Ordinal,
    string Syntax,
    string LiteralSourceSyntax,
    string Attribute,
    string SourceSyntax,
    bool IsStringLiteral,
    bool HasEop,
    bool HasIoCurr,
    bool HasPage,
    bool HasLine,
    bool HasStr,
    string? WholeExpressionSemanticKind
);

internal sealed record LogicSemantic(
    IReadOnlyList<TaskRowLogicDef> StartLogics,
    IReadOnlyList<TaskRaiseEventDef> StartRaises,
    IReadOnlyList<TaskRowLogicDef> RowLogics,
    IReadOnlyList<TaskRowLogicDef> EndLogics,
    IReadOnlyList<TaskRaiseEventDef> EndRaises,
    IReadOnlyList<TaskRowLogicDef> SavingRowLogics,
    IReadOnlyList<TaskGroupLogicDef> GroupLogics
);

internal sealed record FunctionOverrideSemantic(
    TaskFunctionOverrideDef Definition,
    string Name,
    string MethodName,
    string ReturnType,
    int? ReturnExpressionId,
    IReadOnlyList<FunctionParameterSemantic> Parameters,
    IReadOnlyList<string> Remarks,
    IReadOnlyList<TaskRowActionDef> OrderedActions,
    IReadOnlyList<HandlerActionBlockSemantic> Blocks
);

internal sealed record FunctionParameterSemantic(
    TaskFunctionParameterDef Definition,
    string SelectName,
    int ColumnId,
    string ParameterType,
    string ParameterName,
    string? ResourceColumnName
);

internal sealed record ViewSemantic(
    bool ShouldGenerate,
    string BaseClassName,
    string ClassName,
    int? SelectedFormEntryIndex,
    TaskFormEntryDef? SelectedFormEntry,
    TaskFormDef? SelectedForm,
    string? SelectedFormText,
    int? SelectedFormTextExpressionId,
    int SelectedFormWidth,
    int SelectedFormHeight,
    int? SelectedFormColorSchemeId,
    int? SelectedFormFontSchemeId,
    IReadOnlyList<TaskFormControlDef> SelectedFormControls,
    IReadOnlyList<TaskFormControlDef> SelectedSupportedControls,
    IReadOnlyList<TaskFormControlDef> SelectedUnsupportedControls,
    IReadOnlySet<int> StaticContainerIds,
    IReadOnlyList<TaskFormControlDef> TableControls,
    IReadOnlyList<TaskFormControlDef> TableColumnControls,
    IReadOnlyList<TaskFormControlDef> LeafControls,
    IReadOnlyList<TaskFormControlDef> ContainerControls,
    IReadOnlyDictionary<int, string> ControlTypeNameById,
    IReadOnlyDictionary<int, string> ControlVariableNameById,
    IReadOnlyDictionary<int, int> TableAttachmentByLeaf,
    IReadOnlyDictionary<int, int> ColumnAttachmentByLeaf,
    IReadOnlyDictionary<int, IReadOnlyList<int>> ColumnChildIdsByColumn,
    IReadOnlyDictionary<int, IReadOnlyList<int>> TableChildIdsByTable,
    IReadOnlyList<int> RootControlIds,
    IReadOnlyDictionary<int, int> GroupBoxBindingByControlId,
    IReadOnlyDictionary<int, (int TabControlId, int TabIndex)> TabBindingByControlId,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> TableColumnStartXByTableId,
    IReadOnlyList<int> BindingExpressionIds,
    IReadOnlyList<int> FormTextExpressionIds,
    IReadOnlyDictionary<int, int> SystemRaiseEnabledExpressionByKeyCombinationId,
    IReadOnlyList<ViewClickHandler> ClickHandlers,
    IReadOnlyList<ViewBooleanBinding> BooleanBindings,
    IReadOnlyList<ViewSubformBinding> SubformBindings,
    IReadOnlyList<ViewBindListHandler> BindListHandlers
);

internal sealed record ViewClickHandler(
    int ControlId,
    string HandlerName,
    string RaiseExpression,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<TaskArgumentDef> ArgumentDefs
);

internal enum ViewSubformBindingKind
{
    Task,
    ExternalProgram
}

internal sealed record ViewSubformBinding(
    int ControlId,
    ViewSubformBindingKind Kind,
    int? TargetTaskOrdinal,
    string TargetTaskClassName,
    string FieldName,
    string MethodName,
    IReadOnlyList<string> RawArguments,
    int? TargetComponentId,
    string? TargetComponentName,
    int? TargetObjectId,
    string? TargetPublicName
);

internal sealed record ViewBooleanBinding(
    int ControlId,
    int ExpressionId
);

internal sealed record ViewBindListHandler(
    int ControlId,
    string HandlerName,
    string ComboVarName,
    int DataObjectOrdinal,
    string EntityTypeName,
    string EntityVarName,
    string ValueColumnName,
    string DisplayColumnName,
    string? OrderByName
);

internal sealed record LayoutSemantic(
    IReadOnlyList<TaskFormEntryDef> PrintForms,
    IReadOnlyList<TaskFormEntryDef> TextForms,
    IReadOnlyList<TaskFormEntryDef> MergeForms,
    IReadOnlySet<int> ReferencedFormIndexes,
    IReadOnlyDictionary<int, IReadOnlyList<TaskFormControlDef>> SupportedPrintControlsByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyList<TaskFormControlDef>> UnsupportedPrintControlsByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyList<TaskFormControlDef>> SupportedTextIoControlsByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyList<TaskFormControlDef>> UnsupportedTextIoControlsByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> PrintControlTypeNameByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> TextIoControlTypeNameByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> PrintTableAttachmentByLeafByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> PrintColumnAttachmentByLeafByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, IReadOnlyList<int>>> PrintColumnChildIdsByColumnByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, IReadOnlyList<int>>> PrintTableChildIdsByTableByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyList<int>> PrintSectionRootControlIdsByFormEntryIndex,
    string PrintLayoutClassName,
    string? TextIoLayoutClassName,
    string? TextIoNamespaceSegment,
    IReadOnlyDictionary<int, string> MergeTemplateVariableNamesByFormEntryIndex,
    IReadOnlyDictionary<int, string> PrintSectionNamesByFormEntryIndex,
    IReadOnlyDictionary<int, string> TextSectionNamesByFormEntryIndex,
    IReadOnlySet<string> TextIoPageHeaderSectionNames,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> PrintControlVariableNameByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> TextIoControlVariableNameByFormEntryIndex,
    IReadOnlyDictionary<int, IReadOnlyList<int>> TextIoSectionControlIdsByFormEntryIndex,
    IReadOnlyDictionary<int, string> FormIoWriteCallsByFormEntryIndex,
    IReadOnlyDictionary<int, string> FormIoReadCallsByFormEntryIndex,
    string? PageHeaderSectionName,
    string? PageFooterSectionName,
    IReadOnlyList<TaskFormIoDef> PrintOutputIos,
    IReadOnlyList<TaskFormIoDef> PrintGroupIos,
    IReadOnlyList<TaskFormIoDef> StartTaskOutputIos,
    IReadOnlyList<TaskFormIoDef> EndTaskOutputIos,
    IReadOnlyList<TaskFormIoDef> PrintRowIos,
    IReadOnlyList<TaskFormIoDef> TextIoReadRowIos
);

internal sealed record MergeSemantic(
    bool HasTemplate,
    string? TemplatePathExpression,
    IReadOnlyList<TaskMergeTagDef> Tags,
    IReadOnlyList<int> TagExpressionIds
);

internal sealed record ReportSemantic(
    bool HasPrinterWriter,
    bool HasPageHeader,
    bool HasPageFooter,
    bool HasDetailLine,
    bool HasNewPage,
    bool HasEop,
    bool HasIoCurr
);

internal sealed record UnhandledSemantic(
    string Source,
    string Raw
);

internal static class XmlHelpers
{
    private static readonly (string Broken, string Fixed)[] MojibakeReplacements =
    {
        ("\u00E2\u20AC\u2122", "\u2019"),
        ("\u00E2\u20AC\u02DC", "\u2018"),
        ("\u00E2\u20AC\u0153", "\u201C"),
        ("\u00E2\u20AC\u009D", "\u201D"),
        ("\u00E2\u20AC\u201C", "\u2013"),
        ("\u00E2\u20AC\u201D", "\u2014"),
        ("\u00E2\u20AC\u00A6", "\u2026"),
        ("\u00C3\u00A1", "\u00E1"),
        ("\u00C3\u00A2", "\u00E2"),
        ("\u00C3\u00A3", "\u00E3"),
        ("\u00C3\u00A0", "\u00E0"),
        ("\u00C3\u00A4", "\u00E4"),
        ("\u00C3\u00A9", "\u00E9"),
        ("\u00C3\u00AA", "\u00EA"),
        ("\u00C3\u00AD", "\u00ED"),
        ("\u00C3\u00B3", "\u00F3"),
        ("\u00C3\u00B4", "\u00F4"),
        ("\u00C3\u00B5", "\u00F5"),
        ("\u00C3\u00B6", "\u00F6"),
        ("\u00C3\u00BA", "\u00FA"),
        ("\u00C3\u00BC", "\u00FC"),
        ("\u00C3\u00A7", "\u00E7"),
        ("\u00C3\u0081", "\u00C1"),
        ("\u00C3\u0082", "\u00C2"),
        ("\u00C3\u0083", "\u00C3"),
        ("\u00C3\u0080", "\u00C0"),
        ("\u00C3\u0089", "\u00C9"),
        ("\u00C3\u008A", "\u00CA"),
        ("\u00C3\u008D", "\u00CD"),
        ("\u00C3\u0093", "\u00D3"),
        ("\u00C3\u0094", "\u00D4"),
        ("\u00C3\u0095", "\u00D5"),
        ("\u00C3\u009A", "\u00DA"),
        ("\u00C3\u0087", "\u00C7"),
        ("\u00C2\u00B0", "\u00B0"),
        ("\u00C2\u00BA", "\u00BA"),
        ("\u00C2\u00AA", "\u00AA"),
    };

    public static string Attr(XElement e, string name, string fallback = "")
        => NormalizeText(e.Attribute(name)?.Value) ?? fallback;

    public static string AttrUnicodeOrVal(XElement e, string fallback = "")
        => NormalizeText(
            e.Attribute("valUnicode")?.Value
            ?? e.Attribute("val")?.Value
            ?? fallback) ?? fallback;

    public static string? NormalizeText(string? value)
        => value is null ? null : NormalizePossiblyMojibakedText(value);

    private static string NormalizePossiblyMojibakedText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // The source XMLs sometimes contain UTF-8 text previously persisted as
        // Windows-1252 mojibake, producing sequences like \u00E2\u20AC\u2122 or \u00C3\u00A7.
        if (!value.Contains('\u00E2') && !value.Contains('\u00C3') && !value.Contains('\u00C2'))
            return value;

        var repaired = value;
        foreach (var pair in MojibakeReplacements)
            repaired = repaired.Replace(pair.Broken, pair.Fixed, StringComparison.Ordinal);

        if (LooksLessMojibaked(repaired, value))
            return repaired;

        try
        {
            var bytes = Encoding.GetEncoding(1252).GetBytes(value);
            repaired = new UTF8Encoding(false, true).GetString(bytes);
            return LooksLessMojibaked(repaired, value) ? repaired : value;
        }
        catch
        {
            return value;
        }
    }

    private static bool LooksLessMojibaked(string candidate, string original)
        => MojibakeScore(candidate) < MojibakeScore(original);

    private static int MojibakeScore(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var score = 0;
        foreach (var ch in value)
        {
            if (ch is '\u00E2' or '\u00C3' or '\u00C2' or '\uFFFD')
                score++;
        }

        return score;
    }
}

