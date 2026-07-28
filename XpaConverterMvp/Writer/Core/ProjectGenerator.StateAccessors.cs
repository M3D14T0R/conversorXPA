using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace XpaConverterMvp;

internal static partial class ProjectGenerator
{
    private static readonly ProjectGenerationState _globalState = new();
    [ThreadStatic]
    private static ProjectGenerationState? _threadState;
    private static ProjectGenerationState _state => _threadState ?? _globalState;

    private static void RunWithIsolatedGenerationState(Action action)
    {
        var previous = _threadState;
        _threadState = CreateParallelWorkerState(_globalState);
        try
        {
            action();
        }
        finally
        {
            _threadState = previous;
        }
    }

    private static ProjectGenerationState CreateIsolatedGenerationState()
        => CreateParallelWorkerState(_globalState);

    private static ProjectGenerationState CreateNextProgramGenerationState(ProjectGenerationState previous)
    {
        var next = CreateParallelWorkerState(previous);

        // These indexes are structural and bounded by the number of tasks,
        // fields or data objects. Reusing them avoids repeating global lookup
        // work for every program. Expression/code/condition caches deliberately
        // remain empty in the new state: their keys contain program context and
        // were the unbounded part of long conversions.
        next.TaskClassNameByOrdinal = previous.TaskClassNameByOrdinal;
        next.TaskClassNameAssignedRegistry = previous.TaskClassNameAssignedRegistry;
        next.TaskTypeReferenceByOrdinal = previous.TaskTypeReferenceByOrdinal;
        next.ResourceMemberNameCache = previous.ResourceMemberNameCache;
        next.ResourceMemberNameByTaskOrdinal = previous.ResourceMemberNameByTaskOrdinal;
        next.ResourceMemberNameCacheBuiltTaskOrdinals = previous.ResourceMemberNameCacheBuiltTaskOrdinals;
        next.TaskResourceByMemberNameCache = previous.TaskResourceByMemberNameCache;
        next.ReservedTaskMemberNameCache = previous.ReservedTaskMemberNameCache;
        next.ReservedTaskCommandNameCache = previous.ReservedTaskCommandNameCache;
        next.ResolvedCallTargetOrdinalCache = previous.ResolvedCallTargetOrdinalCache;
        next.ExternalTaskTypeReferenceCache = previous.ExternalTaskTypeReferenceCache;
        next.ModelMembersCache = previous.ModelMembersCache;
        next.DataObjectColumnMemberNamesByObjectOrdinal = previous.DataObjectColumnMemberNamesByObjectOrdinal;
        next.LegacyExpressionAliasMapCache = previous.LegacyExpressionAliasMapCache;
        next.AccessibleLegacyResourceReferenceMapCache = previous.AccessibleLegacyResourceReferenceMapCache;
        next.AccessibleResourceKeysCache = previous.AccessibleResourceKeysCache;
        next.AncestorResourceBindingMapCache = previous.AncestorResourceBindingMapCache;
        next.TaskCommandMemberMapCache = previous.TaskCommandMemberMapCache;
        next.AllowedParameterSelectNamesCache = previous.AllowedParameterSelectNamesCache;
        next.DataViewMemberColumnIndexCache = previous.DataViewMemberColumnIndexCache;
        next.DataObjectMemberColumnIndexCache = previous.DataObjectMemberColumnIndexCache;
        next.EffectiveRangeParameterSelectsCache = previous.EffectiveRangeParameterSelectsCache;
        next.ParameterExpressionOrderCache = previous.ParameterExpressionOrderCache;
        next.TaskUpdatesForArrayItemInferenceCache = previous.TaskUpdatesForArrayItemInferenceCache;
        next.TaskTextsForArrayItemInferenceCache = previous.TaskTextsForArrayItemInferenceCache;
        next.IncomingParameterCountCache = previous.IncomingParameterCountCache;
        next.ObservedTaskParameterEvidenceCache = previous.ObservedTaskParameterEvidenceCache;
        next.TaskParametersCache = previous.TaskParametersCache;
        next.ViewDotNetResourceByRootOrdinal = previous.ViewDotNetResourceByRootOrdinal;
        next.ViewPrefixSymbolBindingMapCache = previous.ViewPrefixSymbolBindingMapCache;
        next.ControlHandlerExactControlMapCache = previous.ControlHandlerExactControlMapCache;
        next.AccessibleParentFunctionTargetsCache = previous.AccessibleParentFunctionTargetsCache;
        next.TextIoLayoutClassOwners = previous.TextIoLayoutClassOwners;
        next.TextIoLayoutClassOwnersInitialized = previous.TextIoLayoutClassOwnersInitialized;
        return next;
    }

    private static void RunWithGenerationState(ProjectGenerationState state, Action action)
    {
        var previous = _threadState;
        _threadState = state;
        try
        {
            action();
        }
        finally
        {
            _threadState = previous;
        }
    }

    private static ProjectGenerationState CreateParallelWorkerState(ProjectGenerationState source)
    {
        return new ProjectGenerationState
        {
            ApplicationSelectMap = source.ApplicationSelectMap,
            ParentSelectMapByTaskOrdinal = source.ParentSelectMapByTaskOrdinal,
            DataSourceTypeByObjectOrdinal = source.DataSourceTypeByObjectOrdinal,
            ComponentDataSourcesByLiteral = source.ComponentDataSourcesByLiteral,
            DataObjectsByOrdinal = source.DataObjectsByOrdinal,
            AllFieldModels = source.AllFieldModels,
            AllTasks = source.AllTasks,
            UniqueFunctionContractByLeaf = source.UniqueFunctionContractByLeaf,
            ApplicationTask = source.ApplicationTask,
            TasksByOrdinal = source.TasksByOrdinal,
            TasksByPublicName = source.TasksByPublicName,
            EventCommandByDescription = source.EventCommandByDescription,
            ExpressionInvocationByOrdinal = source.ExpressionInvocationByOrdinal,
            HandlerKindByReference = source.HandlerKindByReference,
            HandlerBodyByReference = source.HandlerBodyByReference,
            ResourceOwnerByReference = source.ResourceOwnerByReference,
            UniqueResourceReturnTypeByMemberName = source.UniqueResourceReturnTypeByMemberName,
            TasksByDeclaredTaskId = source.TasksByDeclaredTaskId,
            TopLevelTasksByProgramIndex = source.TopLevelTasksByProgramIndex,
            ChildTasksByParentOrdinal = source.ChildTasksByParentOrdinal,
            TopLevelTasks = source.TopLevelTasks,
            TopLevelAccessibleResourceKeys = source.TopLevelAccessibleResourceKeys,
            SubformTargetTaskOrdinals = source.SubformTargetTaskOrdinals,
            MultiFormCandidateCounts = source.MultiFormCandidateCounts,
            ViewClassNameCounts = source.ViewClassNameCounts,
            ReservedViewClassNames = source.ReservedViewClassNames,
            FieldModelTypeNameByOrdinal = source.FieldModelTypeNameByOrdinal,
            TaskClassBaseNameByOrdinal = source.TaskClassBaseNameByOrdinal,
            TaskClassSiblingCollisionIndexByOrdinal = source.TaskClassSiblingCollisionIndexByOrdinal,
            TaskClassNameByOrdinal = source.TaskClassNameByOrdinal,
            TaskClassNameAssignedRegistry = source.TaskClassNameAssignedRegistry,
            TaskClassNameResolutionInProgress = new HashSet<int>(),
            TaskTypeReferenceByOrdinal = source.TaskTypeReferenceByOrdinal,
            ResourceMemberNameCache = source.ResourceMemberNameCache,
            ResourceMemberNameByTaskOrdinal = source.ResourceMemberNameByTaskOrdinal,
            ResourceMemberNameCacheBuiltTaskOrdinals = source.ResourceMemberNameCacheBuiltTaskOrdinals,
            TaskResourceByMemberNameCache = source.TaskResourceByMemberNameCache,
            ReservedTaskMemberNameCache = source.ReservedTaskMemberNameCache,
            ReservedTaskCommandNameCache = source.ReservedTaskCommandNameCache,
            LoadedSourceComponents = source.LoadedSourceComponents,
            OptionalRunParameterStartIndexCache = source.OptionalRunParameterStartIndexCache,
            IncomingTaskCallsByTargetOrdinal = source.IncomingTaskCallsByTargetOrdinal,
            TargetComponent = source.TargetComponent,
            SolutionRoot = source.SolutionRoot,
            SourceRoot = source.SourceRoot,
            IsComponentized = source.IsComponentized,
            TargetNamespace = source.TargetNamespace,
            OutputType = source.OutputType,
            RuntimeCoreReferenceMode = source.RuntimeCoreReferenceMode,
            RuntimeCoreDllPath = source.RuntimeCoreDllPath,
            ComponentNamespaces = source.ComponentNamespaces,
            ComponentRightLiteralMap = source.ComponentRightLiteralMap,
            ExternalMagicComponentNames = source.ExternalMagicComponentNames,
            ProjectReferenceMap = source.ProjectReferenceMap,
            DllReferenceMap = source.DllReferenceMap,
            DotNetReferenceAssemblyPathMap = source.DotNetReferenceAssemblyPathMap,
            ProjectReferenceManifests = source.ProjectReferenceManifests,
            CurrentProjectManifest = source.CurrentProjectManifest,
            ExternalManifestColumnAttrObjIndex = source.ExternalManifestColumnAttrObjIndex,
            ExternalManifestColumnTextLengthIndex = source.ExternalManifestColumnTextLengthIndex,
            ApplicationResourceBindingMap = source.ApplicationResourceBindingMap,
            TaskCommandMemberMapCache = source.TaskCommandMemberMapCache,
            ViewDotNetResourceByRootOrdinal = source.ViewDotNetResourceByRootOrdinal,
            TextIoLayoutClassOwners = source.TextIoLayoutClassOwners,
            TextIoLayoutClassOwnersInitialized = source.TextIoLayoutClassOwnersInitialized,
            ComponentFunctionSourceByName = source.ComponentFunctionSourceByName,
            ComponentFunctionReturnTypeByName = source.ComponentFunctionReturnTypeByName
        };
    }

    private static Dictionary<string, string> _applicationSelectMap { get => _state.ApplicationSelectMap; set => _state.ApplicationSelectMap = value; }
    private static Dictionary<int, Dictionary<string, string>> _parentSelectMapByTaskOrdinal { get => _state.ParentSelectMapByTaskOrdinal; set => _state.ParentSelectMapByTaskOrdinal = value; }
    private static Dictionary<int, string> _dataSourceTypeByObjectOrdinal { get => _state.DataSourceTypeByObjectOrdinal; set => _state.DataSourceTypeByObjectOrdinal = value; }
    private static Dictionary<int, DataObjectDef> _dataObjectsByOrdinal { get => _state.DataObjectsByOrdinal; set => _state.DataObjectsByOrdinal = value; }
    private static IReadOnlyList<FieldModelDef> _allFieldModels { get => _state.AllFieldModels; set => _state.AllFieldModels = value; }
    private static IReadOnlyList<TaskSemantic> _allTasks { get => _state.AllTasks; set => _state.AllTasks = value; }
    private static Dictionary<string, FunctionOverrideSemantic?> _uniqueFunctionContractByLeaf { get => _state.UniqueFunctionContractByLeaf; set => _state.UniqueFunctionContractByLeaf = value; }
    private static TaskSemantic? _applicationTask { get => _state.ApplicationTask; set => _state.ApplicationTask = value; }
    private static Dictionary<int, TaskSemantic> _tasksByOrdinal { get => _state.TasksByOrdinal; set => _state.TasksByOrdinal = value; }
    private static Dictionary<string, TaskSemantic> _tasksByPublicName { get => _state.TasksByPublicName; set => _state.TasksByPublicName = value; }
    private static Dictionary<string, string> _eventCommandByDescription { get => _state.EventCommandByDescription; set => _state.EventCommandByDescription = value; }
    private static Dictionary<int, string> _expressionInvocationByOrdinal { get => _state.ExpressionInvocationByOrdinal; set => _state.ExpressionInvocationByOrdinal = value; }
    private static Dictionary<TaskHandlerDef, byte> _handlerKindByReference { get => _state.HandlerKindByReference; set => _state.HandlerKindByReference = value; }
    private static Dictionary<TaskHandlerDef, HandlerBodySemantic> _handlerBodyByReference { get => _state.HandlerBodyByReference; set => _state.HandlerBodyByReference = value; }
    private static Dictionary<TaskResourceColumnDef, TaskSemantic> _resourceOwnerByReference { get => _state.ResourceOwnerByReference; set => _state.ResourceOwnerByReference = value; }
    private static Dictionary<string, string?> _uniqueResourceReturnTypeByMemberName { get => _state.UniqueResourceReturnTypeByMemberName; set => _state.UniqueResourceReturnTypeByMemberName = value; }
    private static Dictionary<int, TaskSemantic> _tasksByDeclaredTaskId { get => _state.TasksByDeclaredTaskId; set => _state.TasksByDeclaredTaskId = value; }
    private static Dictionary<int, TaskSemantic> _topLevelTasksByProgramIndex { get => _state.TopLevelTasksByProgramIndex; set => _state.TopLevelTasksByProgramIndex = value; }
    private static Dictionary<int, IReadOnlyList<TaskSemantic>> _childTasksByParentOrdinal { get => _state.ChildTasksByParentOrdinal; set => _state.ChildTasksByParentOrdinal = value; }
    private static IReadOnlyList<TaskSemantic> _topLevelTasks { get => _state.TopLevelTasks; set => _state.TopLevelTasks = value; }
    private static HashSet<string> _topLevelAccessibleResourceKeys { get => _state.TopLevelAccessibleResourceKeys; set => _state.TopLevelAccessibleResourceKeys = value; }
    private static HashSet<int> _subformTargetTaskOrdinals { get => _state.SubformTargetTaskOrdinals; set => _state.SubformTargetTaskOrdinals = value; }
    private static Dictionary<string, int> _multiFormCandidateCounts { get => _state.MultiFormCandidateCounts; set => _state.MultiFormCandidateCounts = value; }
    private static Dictionary<string, int> _viewClassNameCounts { get => _state.ViewClassNameCounts; set => _state.ViewClassNameCounts = value; }
    private static HashSet<string> _reservedViewClassNames { get => _state.ReservedViewClassNames; set => _state.ReservedViewClassNames = value; }
    private static Dictionary<int, string> _fieldModelTypeNameByOrdinal { get => _state.FieldModelTypeNameByOrdinal; set => _state.FieldModelTypeNameByOrdinal = value; }
    private static Dictionary<int, string> _taskClassBaseNameByOrdinal { get => _state.TaskClassBaseNameByOrdinal; set => _state.TaskClassBaseNameByOrdinal = value; }
    private static Dictionary<int, int> _taskClassSiblingCollisionIndexByOrdinal { get => _state.TaskClassSiblingCollisionIndexByOrdinal; set => _state.TaskClassSiblingCollisionIndexByOrdinal = value; }
    private static Dictionary<int, string> _taskClassNameByOrdinal { get => _state.TaskClassNameByOrdinal; set => _state.TaskClassNameByOrdinal = value; }
    private static Dictionary<string, int> _taskClassNameAssignedRegistry { get => _state.TaskClassNameAssignedRegistry; set => _state.TaskClassNameAssignedRegistry = value; }
    private static HashSet<int> _taskClassNameResolutionInProgress { get => _state.TaskClassNameResolutionInProgress; set => _state.TaskClassNameResolutionInProgress = value; }
    private static Dictionary<int, string> _taskTypeReferenceByOrdinal { get => _state.TaskTypeReferenceByOrdinal; set => _state.TaskTypeReferenceByOrdinal = value; }
    private static ConcurrentDictionary<string, string> _resourceMemberNameCache { get => _state.ResourceMemberNameCache; set => _state.ResourceMemberNameCache = value; }
    private static Dictionary<int, Dictionary<int, string>> _resourceMemberNameByTaskOrdinal { get => _state.ResourceMemberNameByTaskOrdinal; set => _state.ResourceMemberNameByTaskOrdinal = value; }
    private static HashSet<int> _resourceMemberNameCacheBuiltTaskOrdinals { get => _state.ResourceMemberNameCacheBuiltTaskOrdinals; set => _state.ResourceMemberNameCacheBuiltTaskOrdinals = value; }
    private static Dictionary<int, Dictionary<string, TaskResourceColumnDef>> _taskResourceByMemberNameCache { get => _state.TaskResourceByMemberNameCache; set => _state.TaskResourceByMemberNameCache = value; }
    private static Dictionary<int, HashSet<string>> _reservedTaskMemberNameCache { get => _state.ReservedTaskMemberNameCache; set => _state.ReservedTaskMemberNameCache = value; }
    private static Dictionary<int, HashSet<string>> _reservedTaskCommandNameCache { get => _state.ReservedTaskCommandNameCache; set => _state.ReservedTaskCommandNameCache = value; }
    private static HashSet<string> _loadedSourceComponents { get => _state.LoadedSourceComponents; set => _state.LoadedSourceComponents = value; }
    private static Dictionary<string, int> _resolvedCallTargetOrdinalCache { get => _state.ResolvedCallTargetOrdinalCache; set => _state.ResolvedCallTargetOrdinalCache = value; }
    private static Dictionary<string, string?> _externalTaskTypeReferenceCache { get => _state.ExternalTaskTypeReferenceCache; set => _state.ExternalTaskTypeReferenceCache = value; }
    private static Dictionary<string, string> _expressionCodeCache { get => _state.ExpressionCodeCache; set => _state.ExpressionCodeCache = value; }
    private static Dictionary<string, string> _rawExpressionCodeCache { get => _state.RawExpressionCodeCache; set => _state.RawExpressionCodeCache = value; }
    private static Dictionary<string, string> _sharedExpressionEntryCodeCache { get => _state.SharedExpressionEntryCodeCache; set => _state.SharedExpressionEntryCodeCache = value; }
    private static Dictionary<string, string> _sharedRawExpressionEntryCodeCache { get => _state.SharedRawExpressionEntryCodeCache; set => _state.SharedRawExpressionEntryCodeCache = value; }
    private static Dictionary<string, ProjectGenerator.EmittedExpression> _typedExpressionEntryCodeCache { get => _state.TypedExpressionEntryCodeCache; set => _state.TypedExpressionEntryCodeCache = value; }
    private static Dictionary<string, string> _typedExpressionReturnTypeByCodeCache { get => _state.TypedExpressionReturnTypeByCodeCache; set => _state.TypedExpressionReturnTypeByCodeCache = value; }
    private static Dictionary<string, string> _sourceExpressionReturnTypeCache { get => _state.SourceExpressionReturnTypeCache; set => _state.SourceExpressionReturnTypeCache = value; }
    private static Dictionary<string, ProjectGenerator.SourceFunctionCallTypeInfo> _sourceFunctionCallTypeInfoCache { get => _state.SourceFunctionCallTypeInfoCache; set => _state.SourceFunctionCallTypeInfoCache = value; }
    private static HashSet<string> _sourceExpressionReturnTypeResolutionInProgress { get => _state.SourceExpressionReturnTypeResolutionInProgress; set => _state.SourceExpressionReturnTypeResolutionInProgress = value; }
    private static HashSet<string> _expressionCodeResolutionInProgress { get => _state.ExpressionCodeResolutionInProgress; set => _state.ExpressionCodeResolutionInProgress = value; }
    private static Dictionary<int, Dictionary<string, string>> _selectNameToExpressionMapCache { get => _state.SelectNameToExpressionMapCache; set => _state.SelectNameToExpressionMapCache = value; }
    private static HashSet<int> _selectNameToExpressionMapResolutionInProgress { get => _state.SelectNameToExpressionMapResolutionInProgress; set => _state.SelectNameToExpressionMapResolutionInProgress = value; }
    private static Dictionary<int, List<(int DbObj, string ModelType, string MemberName)>> _modelMembersCache { get => _state.ModelMembersCache; set => _state.ModelMembersCache = value; }
    private static Dictionary<int, Dictionary<int, string>> _dataObjectColumnMemberNamesByObjectOrdinal { get => _state.DataObjectColumnMemberNamesByObjectOrdinal; set => _state.DataObjectColumnMemberNamesByObjectOrdinal = value; }
    private static HashSet<int> _modelMembersResolutionInProgress { get => _state.ModelMembersResolutionInProgress; set => _state.ModelMembersResolutionInProgress = value; }
    private static Dictionary<string, List<(TaskLogicLinkDef Link, string MemberName)>> _linkMembersCache { get => _state.LinkMembersCache; set => _state.LinkMembersCache = value; }
    private static Dictionary<string, string> _selectExpressionCache { get => _state.SelectExpressionCache; set => _state.SelectExpressionCache = value; }
    private static HashSet<string> _selectExpressionResolutionInProgress { get => _state.SelectExpressionResolutionInProgress; set => _state.SelectExpressionResolutionInProgress = value; }
    private static Dictionary<string, string> _selectBindValueExpressionCache { get => _state.SelectBindValueExpressionCache; set => _state.SelectBindValueExpressionCache = value; }
    private static Dictionary<string, string> _selectSourceMemberCache { get => _state.SelectSourceMemberCache; set => _state.SelectSourceMemberCache = value; }
    private static Dictionary<int, Dictionary<string, string>> _legacyExpressionAliasMapCache { get => _state.LegacyExpressionAliasMapCache; set => _state.LegacyExpressionAliasMapCache = value; }
    private static Dictionary<int, Dictionary<string, string>> _accessibleLegacyResourceReferenceMapCache { get => _state.AccessibleLegacyResourceReferenceMapCache; set => _state.AccessibleLegacyResourceReferenceMapCache = value; }
    private static Dictionary<int, IReadOnlyList<string>> _accessibleResourceKeysCache { get => _state.AccessibleResourceKeysCache; set => _state.AccessibleResourceKeysCache = value; }
    private static Dictionary<string, string>? _applicationResourceBindingMap { get => _state.ApplicationResourceBindingMap; set => _state.ApplicationResourceBindingMap = value; }
    private static Dictionary<int, Dictionary<string, string>> _ancestorResourceBindingMapCache { get => _state.AncestorResourceBindingMapCache; set => _state.AncestorResourceBindingMapCache = value; }
    private static Dictionary<int, Dictionary<string, string>> _taskResourceAliasMapCache { get => _state.TaskResourceAliasMapCache; set => _state.TaskResourceAliasMapCache = value; }
    private static Dictionary<int, HashSet<string>> _taskResolvedMemberNameSetCache { get => _state.TaskResolvedMemberNameSetCache; set => _state.TaskResolvedMemberNameSetCache = value; }
    private static Dictionary<int, IdentifierBindingPreparation> _identifierBindingPreparationCache { get => _state.IdentifierBindingPreparationCache; set => _state.IdentifierBindingPreparationCache = value; }
    private static Dictionary<int, Dictionary<string, string>> _taskCommandMemberMapCache { get => _state.TaskCommandMemberMapCache; set => _state.TaskCommandMemberMapCache = value; }
    private static Dictionary<int, HashSet<string>> _allowedParameterSelectNamesCache { get => _state.AllowedParameterSelectNamesCache; set => _state.AllowedParameterSelectNamesCache = value; }
    private static Dictionary<string, string> _updateTargetExpressionCache { get => _state.UpdateTargetExpressionCache; set => _state.UpdateTargetExpressionCache = value; }
    private static Dictionary<string, string> _updateValueExpressionCache { get => _state.UpdateValueExpressionCache; set => _state.UpdateValueExpressionCache = value; }
    private static Dictionary<string, ProjectGenerator.TargetValueInfo> _targetValueInfoCache { get => _state.TargetValueInfoCache; set => _state.TargetValueInfoCache = value; }
    private static Dictionary<int, IReadOnlyDictionary<string, DataColumnDef>> _dataViewMemberColumnIndexCache { get => _state.DataViewMemberColumnIndexCache; set => _state.DataViewMemberColumnIndexCache = value; }
    private static IReadOnlyDictionary<string, DataColumnDef>? _dataObjectMemberColumnIndexCache { get => _state.DataObjectMemberColumnIndexCache; set => _state.DataObjectMemberColumnIndexCache = value; }
    private static IReadOnlyDictionary<string, string>? _externalManifestColumnAttrObjIndex { get => _state.ExternalManifestColumnAttrObjIndex; set => _state.ExternalManifestColumnAttrObjIndex = value; }
    private static IReadOnlyDictionary<string, int>? _externalManifestColumnTextLengthIndex { get => _state.ExternalManifestColumnTextLengthIndex; set => _state.ExternalManifestColumnTextLengthIndex = value; }
    private static Dictionary<string, TaskResourceColumnDef?> _taskResourceForAssignmentCache { get => _state.TaskResourceForAssignmentCache; set => _state.TaskResourceForAssignmentCache = value; }
    private static Dictionary<string, string> _parentBindingExpressionCache { get => _state.ParentBindingExpressionCache; set => _state.ParentBindingExpressionCache = value; }
    private static Dictionary<string, IReadOnlyList<DataColumnDef>> _linkKeyColumnsCache { get => _state.LinkKeyColumnsCache; set => _state.LinkKeyColumnsCache = value; }
    private static Dictionary<string, string> _filterOperandExpressionCache { get => _state.FilterOperandExpressionCache; set => _state.FilterOperandExpressionCache = value; }
    private static Dictionary<string, string> _linkFilterConditionCache { get => _state.LinkFilterConditionCache; set => _state.LinkFilterConditionCache = value; }
    private static Dictionary<string, string> _linkFilterConditionTemplateCache { get => _state.LinkFilterConditionTemplateCache; set => _state.LinkFilterConditionTemplateCache = value; }
    private static Dictionary<int, IReadOnlyList<TaskLogicSelectDef>> _effectiveRangeParameterSelectsCache { get => _state.EffectiveRangeParameterSelectsCache; set => _state.EffectiveRangeParameterSelectsCache = value; }
    private static Dictionary<int, Dictionary<string, int>> _parameterExpressionOrderCache { get => _state.ParameterExpressionOrderCache; set => _state.ParameterExpressionOrderCache = value; }
    private static Dictionary<string, string> _contextualInferenceCache { get => _state.ContextualInferenceCache; set => _state.ContextualInferenceCache = value; }
    private static Dictionary<string, string> _expectedTypeEvidenceCache { get => _state.ExpectedTypeEvidenceCache; set => _state.ExpectedTypeEvidenceCache = value; }
    private static Dictionary<string, bool> _textualNumericResourceOverrideCache { get => _state.TextualNumericResourceOverrideCache; set => _state.TextualNumericResourceOverrideCache = value; }
    private static Dictionary<string, bool> _numericTextResourceOverrideCache { get => _state.NumericTextResourceOverrideCache; set => _state.NumericTextResourceOverrideCache = value; }
    private static Dictionary<string, bool> _logicalBlobResourceOverrideCache { get => _state.LogicalBlobResourceOverrideCache; set => _state.LogicalBlobResourceOverrideCache = value; }
    private static Dictionary<int, Dictionary<TaskResourceColumnDef, string>> _effectiveTaskResourceAttrObjCache { get => _state.EffectiveTaskResourceAttrObjCache; set => _state.EffectiveTaskResourceAttrObjCache = value; }
    private static Dictionary<int, Dictionary<TaskResourceColumnDef, string>> _taskResourceColumnTypeCache { get => _state.TaskResourceColumnTypeCache; set => _state.TaskResourceColumnTypeCache = value; }
    private static Dictionary<string, string> _attrObjForColumnTypeCache { get => _state.AttrObjForColumnTypeCache; set => _state.AttrObjForColumnTypeCache = value; }
    private static Dictionary<int, TaskUpdateDef[]> _taskUpdatesForArrayItemInferenceCache { get => _state.TaskUpdatesForArrayItemInferenceCache; set => _state.TaskUpdatesForArrayItemInferenceCache = value; }
    private static Dictionary<int, string[]> _taskTextsForArrayItemInferenceCache { get => _state.TaskTextsForArrayItemInferenceCache; set => _state.TaskTextsForArrayItemInferenceCache = value; }
    private static Dictionary<string, string> _preparedRunArgumentsCache { get => _state.PreparedRunArgumentsCache; set => _state.PreparedRunArgumentsCache = value; }
    private static Dictionary<string, string> _nonInputArgumentBindingCache { get => _state.NonInputArgumentBindingCache; set => _state.NonInputArgumentBindingCache = value; }
    private static Dictionary<int, IReadOnlyList<(string Expression, string Member, string Direction, string ParameterType, int Depth)>> _nonInputArgumentCandidatesByTaskOrdinal { get => _state.NonInputArgumentCandidatesByTaskOrdinal; set => _state.NonInputArgumentCandidatesByTaskOrdinal = value; }
    private static Dictionary<int, int> _optionalRunParameterStartIndexCache { get => _state.OptionalRunParameterStartIndexCache; set => _state.OptionalRunParameterStartIndexCache = value; }
    private static Dictionary<int, IReadOnlyList<(TaskSemantic Caller, TaskCallDef Call)>> _incomingTaskCallsByTargetOrdinal { get => _state.IncomingTaskCallsByTargetOrdinal; set => _state.IncomingTaskCallsByTargetOrdinal = value; }
    private static Dictionary<int, int> _incomingParameterCountCache { get => _state.IncomingParameterCountCache; set => _state.IncomingParameterCountCache = value; }
    private static Dictionary<int, IReadOnlyList<Dictionary<string, int>>> _observedTaskParameterEvidenceCache { get => _state.ObservedTaskParameterEvidenceCache; set => _state.ObservedTaskParameterEvidenceCache = value; }
    private static HashSet<int> _observedTaskParameterEvidenceInProgress { get => _state.ObservedTaskParameterEvidenceInProgress; set => _state.ObservedTaskParameterEvidenceInProgress = value; }
    private static Dictionary<int, List<(string ColumnMember, string ParameterType, string ParameterName, string ParameterDirection)>> _taskParametersCache { get => _state.TaskParametersCache; set => _state.TaskParametersCache = value; }
    private static Dictionary<string, IReadOnlyList<string>> _resolvedCallArgumentsCache { get => _state.ResolvedCallArgumentsCache; set => _state.ResolvedCallArgumentsCache = value; }
    private static Dictionary<string, string> _rowActionConditionCodeCache { get => _state.RowActionConditionCodeCache; set => _state.RowActionConditionCodeCache = value; }
    private static Dictionary<string, string> _sharedActionConditionCodeCache { get => _state.SharedActionConditionCodeCache; set => _state.SharedActionConditionCodeCache = value; }
    private static Dictionary<string, string> _sharedConditionExpressionCodeCache { get => _state.SharedConditionExpressionCodeCache; set => _state.SharedConditionExpressionCodeCache = value; }
    private static Dictionary<string, TaskRowActionDef> _strippedActionCache { get => _state.StrippedActionCache; set => _state.StrippedActionCache = value; }
    private static Dictionary<string, string> _normalizedStructuredConditionCache { get => _state.NormalizedStructuredConditionCache; set => _state.NormalizedStructuredConditionCache = value; }
    private static Dictionary<string, IReadOnlyList<string>> _structuredConditionConjunctionCache { get => _state.StructuredConditionConjunctionCache; set => _state.StructuredConditionConjunctionCache = value; }
    private static Dictionary<string, string> _coveredStructuredConditionStripCache { get => _state.CoveredStructuredConditionStripCache; set => _state.CoveredStructuredConditionStripCache = value; }
    private static Dictionary<string, string> _statementBooleanConditionSyntaxCache { get => _state.StatementBooleanConditionSyntaxCache; set => _state.StatementBooleanConditionSyntaxCache = value; }
    private static Dictionary<string, string[]> _remarkLinesCache { get => _state.RemarkLinesCache; set => _state.RemarkLinesCache = value; }
    private static Dictionary<string, string> _controlDataExpressionCache { get => _state.ControlDataExpressionCache; set => _state.ControlDataExpressionCache = value; }
    private static Dictionary<int, IReadOnlyDictionary<string, TaskResourceColumnDef?>> _viewDotNetResourceByRootOrdinal { get => _state.ViewDotNetResourceByRootOrdinal; set => _state.ViewDotNetResourceByRootOrdinal = value; }
    private static Dictionary<int, Dictionary<string, string>> _viewPrefixSymbolBindingMapCache { get => _state.ViewPrefixSymbolBindingMapCache; set => _state.ViewPrefixSymbolBindingMapCache = value; }
    private static Dictionary<string, TaskFormControlDef?> _controlHandlerControlCache { get => _state.ControlHandlerControlCache; set => _state.ControlHandlerControlCache = value; }
    private static Dictionary<int, Dictionary<string, TaskFormControlDef>> _controlHandlerExactControlMapCache { get => _state.ControlHandlerExactControlMapCache; set => _state.ControlHandlerExactControlMapCache = value; }
    private static Dictionary<string, string> _controlHandlerMethodNameCache { get => _state.ControlHandlerMethodNameCache; set => _state.ControlHandlerMethodNameCache = value; }
    private static Dictionary<int, Dictionary<string, string>> _accessibleParentFunctionTargetsCache { get => _state.AccessibleParentFunctionTargetsCache; set => _state.AccessibleParentFunctionTargetsCache = value; }
    private static string? _targetComponent { get => _state.TargetComponent; set => _state.TargetComponent = value; }
    private static string? _solutionRoot { get => _state.SolutionRoot; set => _state.SolutionRoot = value; }
    private static string _sourceRoot { get => _state.SourceRoot; set => _state.SourceRoot = value; }
    private static bool _isComponentized { get => _state.IsComponentized; set => _state.IsComponentized = value; }
    private static string _targetNamespace { get => _state.TargetNamespace; set => _state.TargetNamespace = value; }
    private static string _outputType { get => _state.OutputType; set => _state.OutputType = value; }
    private static string _runtimeCoreReferenceMode { get => _state.RuntimeCoreReferenceMode; set => _state.RuntimeCoreReferenceMode = value; }
    private static string? _runtimeCoreDllPath { get => _state.RuntimeCoreDllPath; set => _state.RuntimeCoreDllPath = value; }
    private static Dictionary<string, string> _componentNamespaces { get => _state.ComponentNamespaces; set => _state.ComponentNamespaces = value; }
    private static Dictionary<string, string> _componentRightLiteralMap { get => _state.ComponentRightLiteralMap; set => _state.ComponentRightLiteralMap = value; }
    private static Dictionary<string, int> _componentDataSourcesByLiteral { get => _state.ComponentDataSourcesByLiteral; set => _state.ComponentDataSourcesByLiteral = value; }
    private static HashSet<string> _externalMagicComponentNames { get => _state.ExternalMagicComponentNames; set => _state.ExternalMagicComponentNames = value; }
    private static Dictionary<string, string> _projectReferenceMap { get => _state.ProjectReferenceMap; set => _state.ProjectReferenceMap = value; }
    private static Dictionary<string, string> _dllReferenceMap { get => _state.DllReferenceMap; set => _state.DllReferenceMap = value; }
    private static Dictionary<string, string> _dotNetReferenceAssemblyPathMap { get => _state.DotNetReferenceAssemblyPathMap; set => _state.DotNetReferenceAssemblyPathMap = value; }
    private static Dictionary<string, ProjectManifest> _projectReferenceManifests { get => _state.ProjectReferenceManifests; set => _state.ProjectReferenceManifests = value; }
    private static ProjectManifest? _currentProjectManifest { get => _state.CurrentProjectManifest; set => _state.CurrentProjectManifest = value; }
    private static HashSet<string>? _userMethodsPublicNames { get => _state.UserMethodsPublicNames; set => _state.UserMethodsPublicNames = value; }
    private static Dictionary<string, string>? _userMethodsPublicNameMap { get => _state.UserMethodsPublicNameMap; set => _state.UserMethodsPublicNameMap = value; }
    private static Dictionary<string, string> _componentFunctionSourceByName { get => _state.ComponentFunctionSourceByName; set => _state.ComponentFunctionSourceByName = value; }
    private static Dictionary<string, string> _componentFunctionReturnTypeByName { get => _state.ComponentFunctionReturnTypeByName; set => _state.ComponentFunctionReturnTypeByName = value; }
}
