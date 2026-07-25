using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace XpaConverterMvp;

internal sealed class IdentifierBindingPreparation
{
    public required Dictionary<string, string> LocalMap { get; init; }
    public required Dictionary<string, string> ParentMap { get; init; }
    public required Dictionary<string, string> ApplicationMap { get; init; }
    public required HashSet<string> LocalFunctionNames { get; init; }
}

internal sealed class ProjectGenerationState
{
    public Dictionary<string, string> ApplicationSelectMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, Dictionary<string, string>> ParentSelectMapByTaskOrdinal { get; set; } = new();
    public Dictionary<int, string> DataSourceTypeByObjectOrdinal { get; set; } = new();
    public Dictionary<int, DataObjectDef> DataObjectsByOrdinal { get; set; } = new();
    public IReadOnlyList<FieldModelDef> AllFieldModels { get; set; } = Array.Empty<FieldModelDef>();
    public IReadOnlyList<TaskSemantic> AllTasks { get; set; } = Array.Empty<TaskSemantic>();
    public Dictionary<string, FunctionOverrideSemantic?> UniqueFunctionContractByLeaf { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TaskSemantic? ApplicationTask { get; set; }
    public Dictionary<int, TaskSemantic> TasksByOrdinal { get; set; } = new();
    public Dictionary<string, TaskSemantic> TasksByPublicName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> EventCommandByDescription { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, string> ExpressionInvocationByOrdinal { get; set; } = new();
    public Dictionary<TaskHandlerDef, byte> HandlerKindByReference { get; set; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<TaskHandlerDef, HandlerBodySemantic> HandlerBodyByReference { get; set; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<TaskResourceColumnDef, TaskSemantic> ResourceOwnerByReference { get; set; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<string, string?> UniqueResourceReturnTypeByMemberName { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, TaskSemantic> TasksByDeclaredTaskId { get; set; } = new();
    public Dictionary<int, TaskSemantic> TopLevelTasksByProgramIndex { get; set; } = new();
    public Dictionary<int, IReadOnlyList<TaskSemantic>> ChildTasksByParentOrdinal { get; set; } = new();
    public IReadOnlyList<TaskSemantic> TopLevelTasks { get; set; } = Array.Empty<TaskSemantic>();
    public HashSet<string> TopLevelAccessibleResourceKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> SubformTargetTaskOrdinals { get; set; } = new();
    public Dictionary<string, int> MultiFormCandidateCounts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ViewClassNameCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ReservedViewClassNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, string> FieldModelTypeNameByOrdinal { get; set; } = new();
    public Dictionary<int, string> TaskClassBaseNameByOrdinal { get; set; } = new();
    public Dictionary<int, int> TaskClassSiblingCollisionIndexByOrdinal { get; set; } = new();
    public Dictionary<int, string> TaskClassNameByOrdinal { get; set; } = new();
    public Dictionary<string, int> TaskClassNameAssignedRegistry { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> TaskClassNameResolutionInProgress { get; set; } = new();
    public Dictionary<int, string> TaskTypeReferenceByOrdinal { get; set; } = new();
    // Written lazily during parallel task emission (cross-task resource
    // resolution), so it must be thread-safe: all workers share this instance.
    public ConcurrentDictionary<string, string> ResourceMemberNameCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, Dictionary<int, string>> ResourceMemberNameByTaskOrdinal { get; set; } = new();
    public HashSet<int> ResourceMemberNameCacheBuiltTaskOrdinals { get; set; } = new();
    public Dictionary<int, Dictionary<string, TaskResourceColumnDef>> TaskResourceByMemberNameCache { get; set; } = new();
    public Dictionary<int, HashSet<string>> ReservedTaskMemberNameCache { get; set; } = new();
    public Dictionary<int, HashSet<string>> ReservedTaskCommandNameCache { get; set; } = new();
    public HashSet<string> LoadedSourceComponents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ResolvedCallTargetOrdinalCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> ExternalTaskTypeReferenceCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExpressionCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RawExpressionCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SharedExpressionEntryCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SharedRawExpressionEntryCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProjectGenerator.EmittedExpression> TypedExpressionEntryCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TypedExpressionReturnTypeByCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SourceExpressionReturnTypeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProjectGenerator.SourceFunctionCallTypeInfo> SourceFunctionCallTypeInfoCache { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SourceExpressionReturnTypeResolutionInProgress { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ExpressionCodeResolutionInProgress { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, Dictionary<string, string>> SelectNameToExpressionMapCache { get; set; } = new();
    public HashSet<int> SelectNameToExpressionMapResolutionInProgress { get; set; } = new();
    public Dictionary<int, List<(int DbObj, string ModelType, string MemberName)>> ModelMembersCache { get; set; } = new();
    public Dictionary<int, Dictionary<int, string>> DataObjectColumnMemberNamesByObjectOrdinal { get; set; } = new();
    public HashSet<int> ModelMembersResolutionInProgress { get; set; } = new();
    public Dictionary<string, List<(TaskLogicLinkDef Link, string MemberName)>> LinkMembersCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SelectExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SelectExpressionResolutionInProgress { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SelectBindValueExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SelectSourceMemberCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, Dictionary<string, string>> LegacyExpressionAliasMapCache { get; set; } = new();
    public Dictionary<int, Dictionary<string, string>> AccessibleLegacyResourceReferenceMapCache { get; set; } = new();
    public Dictionary<int, IReadOnlyList<string>> AccessibleResourceKeysCache { get; set; } = new();
    public Dictionary<string, string>? ApplicationResourceBindingMap { get; set; }
    public Dictionary<int, Dictionary<string, string>> AncestorResourceBindingMapCache { get; set; } = new();
    public Dictionary<int, Dictionary<string, string>> TaskResourceAliasMapCache { get; set; } = new();
    public Dictionary<int, HashSet<string>> TaskResolvedMemberNameSetCache { get; set; } = new();
    public ConcurrentDictionary<int, Dictionary<string, string>> ResolvedAncestorSelectBindingMapCache { get; } = new();
    public Dictionary<int, TaskIoDef> OwnedMergeIoDefinitionCache { get; set; } = new();
    public bool OwnedMergeIoDefinitionCacheInitialized { get; set; }
    public object OwnedMergeIoDefinitionCacheLock { get; } = new();
    public Dictionary<int, IdentifierBindingPreparation> IdentifierBindingPreparationCache { get; set; } = new();
    public Dictionary<int, Dictionary<string, string>> TaskCommandMemberMapCache { get; set; } = new();
    public Dictionary<int, HashSet<string>> AllowedParameterSelectNamesCache { get; set; } = new();
    public Dictionary<string, string> UpdateTargetExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UpdateValueExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProjectGenerator.TargetValueInfo> TargetValueInfoCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, IReadOnlyDictionary<string, DataColumnDef>> DataViewMemberColumnIndexCache { get; set; } = new();
    public IReadOnlyDictionary<string, DataColumnDef>? DataObjectMemberColumnIndexCache { get; set; }
    public IReadOnlyDictionary<string, string>? ExternalManifestColumnAttrObjIndex { get; set; }
    public Dictionary<string, TaskResourceColumnDef?> TaskResourceForAssignmentCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ParentBindingExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<DataColumnDef>> LinkKeyColumnsCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> FilterOperandExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LinkFilterConditionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LinkFilterConditionTemplateCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, IReadOnlyList<TaskLogicSelectDef>> EffectiveRangeParameterSelectsCache { get; set; } = new();
    public Dictionary<int, Dictionary<string, int>> ParameterExpressionOrderCache { get; set; } = new();
    public Dictionary<string, string> ContextualInferenceCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ExpectedTypeEvidenceCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> TextualNumericResourceOverrideCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> NumericTextResourceOverrideCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> LogicalBlobResourceOverrideCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, Dictionary<TaskResourceColumnDef, string>> EffectiveTaskResourceAttrObjCache { get; set; } = new();
    public Dictionary<int, Dictionary<TaskResourceColumnDef, string>> TaskResourceColumnTypeCache { get; set; } = new();
    public Dictionary<string, string> AttrObjForColumnTypeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, TaskUpdateDef[]> TaskUpdatesForArrayItemInferenceCache { get; set; } = new();
    public Dictionary<int, string[]> TaskTextsForArrayItemInferenceCache { get; set; } = new();
    public Dictionary<string, string> PreparedRunArgumentsCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NonInputArgumentBindingCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, IReadOnlyList<(string Expression, string Member, string Direction, string ParameterType, int Depth)>> NonInputArgumentCandidatesByTaskOrdinal { get; set; } = new();
    public Dictionary<int, int> OptionalRunParameterStartIndexCache { get; set; } = new();
    public Dictionary<int, IReadOnlyList<(TaskSemantic Caller, TaskCallDef Call)>> IncomingTaskCallsByTargetOrdinal { get; set; } = new();
    public Dictionary<int, int> IncomingParameterCountCache { get; set; } = new();
    public Dictionary<int, IReadOnlyList<Dictionary<string, int>>> ObservedTaskParameterEvidenceCache { get; set; } = new();
    public HashSet<int> ObservedTaskParameterEvidenceInProgress { get; set; } = new();
    public Dictionary<int, List<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)>> TaskParametersCache { get; set; } = new();
    public Dictionary<string, IReadOnlyList<string>> ResolvedCallArgumentsCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RowActionConditionCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SharedActionConditionCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SharedConditionExpressionCodeCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, TaskRowActionDef> StrippedActionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NormalizedStructuredConditionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<string>> StructuredConditionConjunctionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CoveredStructuredConditionStripCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> StatementBooleanConditionSyntaxCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> RemarkLinesCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ControlDataExpressionCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, IReadOnlyDictionary<string, TaskResourceColumnDef?>> ViewDotNetResourceByRootOrdinal { get; set; } = new();
    public Dictionary<int, Dictionary<string, string>> ViewPrefixSymbolBindingMapCache { get; set; } = new();
    public Dictionary<string, TaskFormControlDef?> ControlHandlerControlCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, Dictionary<string, TaskFormControlDef>> ControlHandlerExactControlMapCache { get; set; } = new();
    public Dictionary<string, string> ControlHandlerMethodNameCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, Dictionary<string, string>> AccessibleParentFunctionTargetsCache { get; set; } = new();
    public string? TargetComponent { get; set; }
    public string? SolutionRoot { get; set; }
    public string SourceRoot { get; set; } = "";
    public bool IsComponentized { get; set; }
    public string TargetNamespace { get; set; } = "";
    public string OutputType { get; set; } = "WinExe";
    public string RuntimeCoreReferenceMode { get; set; } = "Project";
    public string? RuntimeCoreDllPath { get; set; }
    public Dictionary<string, string> ComponentNamespaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ComponentRightLiteralMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExternalMagicComponentNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectReferenceMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DllReferenceMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DotNetReferenceAssemblyPathMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProjectManifest> ProjectReferenceManifests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string>? UserMethodsPublicNames { get; set; }
    public Dictionary<string, string>? UserMethodsPublicNameMap { get; set; }
    public Dictionary<string, string> ComponentFunctionSourceByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ComponentFunctionReturnTypeByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
