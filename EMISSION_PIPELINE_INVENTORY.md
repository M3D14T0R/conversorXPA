# Emission Pipeline Inventory

Date: 2026-06-05

## 2026-06-05 Migration Pass

This pass separates legacy translators into two groups:

- **Allowed lexical translation**: source-to-C# rendering that is still inside a typed source path and does not decide coercion by reading generated C#.
- **Critical legacy decision**: any callsite that has a target/sink contract but translates a source expression to C# string before feeding source and target evidence into `EmittedExpression`.

Changes applied in this pass:

| Area | Previous behavior | New behavior | Risk guard |
|---|---|---|---|
| `TaskUpdateAssignments.ResolveUpdateValueExpression` fallback | fallback path translated `exp.LiteralNormalizedSyntax` through `TranslateXpaExpressionToCSharp`, then normalized by attribute and context | fallback path now calls `ResolveTypedExpressionEntryCode(exp, ..., context)` so source evidence and assignment sink travel together | only affects the rare branch where the normal contextual `ResolveExpressionCode(...)` returned empty |
| `DataViewRanges.EmitTaskRangeExpressions` RHS fragments | RHS extracted from `A = <rhs>` was translated directly by `TranslateXpaExpressionToCSharp` | when the left side has filter-comparison type evidence, the RHS is wrapped as a synthetic `ExpressionEntrySemantic` and emitted by `ResolveTypedExpressionEntryCode(...)` with `FilterComparison` context | if the left side has no reliable target type, the old lexical translation remains instead of guessing |
| `DataViewRanges.EmitTaskRangeExpressions` value+condition ranges | `A = 'LA' AND condition` could be emitted as a text RHS, producing invalid code such as `u.CastToText("LA" && condition)` | the XPA RHS is split before C# rendering; the range value is emitted through the filter-comparison context and the condition is emitted as `BooleanCondition`, producing `CndRange(() => condition, A.IsEqualTo("LA"))` | only top-level XPA `AND` is rewritten; other boolean shapes keep the existing path |

New helper:

- `CreateSyntheticExpressionEntry(...)`
- `ResolveSourceFragmentCode(...)`

Purpose:

- support source fragments that do not exist as standalone XML expression entries
- keep the fragment in the typed pipeline until final C# rendering
- avoid adding source-fragment coercion rules through generated C# cleanup

Still intentionally not migrated in this pass:

- `StringLiteralResolution.TryResolveExpressionAsStringLiteralCode`: lexical-only literal detection, not coercion.
- `CanEmitExpressionEntryAsStatement`: lexical fallback only after source CLR return type evidence fails.
- Core `ExpressionResolution` internal calls that translate source while building `SourceTranslatedExpression`: these remain part of the source-rendering layer and must be migrated only when they start deciding type/coercion outside `EmittedExpression`.
- `BuildBindValueSuffix`: still string-shape based for choosing direct bind versus lambda, but not currently a coercion bridge. It should receive a typed `BindValueEmission` result in a later pass.

## Objective

Inventory the expression/code emission paths that are not fully closed by the strict `EmittedExpression` pipeline, especially the paths that can still produce coercion errors by emitting C# without a reliable target contract.

This document follows the converter rule that type evidence must come from XML/declarations/contracts/sinks and that final coercion must be centralized through `XpaTypeEngine`, not through post-generation string normalization.

## Standard

The intended standard for expression emission is:

1. Resolve the expression from XPA/source evidence.
2. Carry source type evidence in `EmittedExpression`.
3. Pass the target/sink contract (`Assignment`, `RunArgument`, `BindValue`, `FilterComparison`, `BooleanCondition`, etc.).
4. Let the strict `EmittedExpressionEngine` choose the emission from reliable evidence.
5. Let `XpaTypeEngine` finalize scalar coercion.
6. If evidence is insufficient, log a diagnostic and enrich the semantic source later.

## Current Strict Entry Point

The current central entry is:

- `XpaConverterMvp/Writer/Core/ProjectGenerator.ExpressionEmissionContext.cs`
  - `EmitExpressionForContext(...)`
  - calls `TryEmitThroughStrictEmittedExpression(...)`
  - if strict emission fails, logs `EMITTED_EXPR_UNRESOLVED` and returns the normalized input unchanged

This behavior prevents unsafe guessing, but it also means a sink can exist and still not be coerced if the evidence is incomplete.

## Static Inventory

### 1. Calls With No Explicit Typed Sink

These are the highest-priority static candidates because the final expression is resolved without an explicit expected type.

Observed direct `ResolveExpressionCode(..., dataObjects)` callsites outside the core overload:

- `Writer/DataView/ProjectGenerator.DataViewLinkComparisons.cs`
- `Writer/DataView/ProjectGenerator.SelectModelResolution.cs`
- `Writer/DataView/ProjectGenerator.DataViewRanges.cs`

Risk:

- link/range/select expressions can lose the type of the left side or target model
- later code may rely on `NormalizeComparisonRightExpression(...)` instead of the strict sink contract

### 2. Raw Expression Resolution

Raw paths still exist for cases where the converter avoids attribute normalization or has external/DLL ambiguity:

- `ResolveRawExpressionEntryCode(...)`
- `ResolveExpressionEntryCodeWithoutAttributeNormalization(...)`
- fallback in `ResolveFilterOperandExpression(...)`

Risk:

- useful as a guard against wrong casts, but it can leave relation/range values uncoerced when the real target type exists elsewhere

### 3. Direct Translation Calls

Direct translation bypasses the strict sink unless it is immediately re-entered with context:

- `TranslateXpaExpressionToCSharp(...)`
- notable callsites in:
  - `Writer/DataView/ProjectGenerator.DataViewRanges.cs`
  - `Writer/Tasks/ProjectGenerator.TaskUpdateAssignments.cs`
  - core translation helpers

Risk:

- source syntax becomes final-ish C# before target type is known
- later repair tends to become string-level normalization

### 4. Side Normalizers Around Comparisons

Comparison paths still rely on:

- `NormalizeComparisonRightExpression(...)`
- `BuildFilterIsEqualTo(...)`
- `BuildLinkComparison(...)`
- `ResolveFilterOperandExpression(...)`

Risk:

- comparisons are a proper typed sink, but some right-side values are still normalized after translation instead of being emitted as `FilterComparison` from the beginning

### 5. Assignment Fallbacks

Assignments are partially in the strict path:

- `CreateAssignmentEmissionContext(...)`
- `TryEmitThroughStrictEmittedExpression(...)`

But there are still local bridges:

- `TryEmitForDeclaredAssignmentType(...)`
- `RenderDeclaredAssignmentBridge(...)`
- blob/array/dotnet special handling in `BuildUpdateAssignment(...)`

Risk:

- some bridges are legitimate target rules, but they should be converted into central evidence/contracts where possible

### 6. Run/Call Arguments With Weak Contract

Run arguments use `CreateRunArgumentEmissionContext(...)` when the target signature is known.

Weak cases remain:

- `CreateCallArgumentEmissionContext()` has no expected type
- snippet/external/public program calls can fall back to `CallArgument`
- `PreserveBinding=true` correctly avoids coercion, but also means the contract must be handled separately

Risk:

- CGGeral ranges/mocks and external calls can generate argument coercion errors because the target signature is unknown or incomplete

### 7. SQL/Evaluate/Side-Effect Expressions

Contexts with no expected return type:

- `CreateSqlExpressionEmissionContext()`
- `CreateEvaluateStatementEmissionContext()`
- generic `CallArgument`

Risk:

- some side-effect functions return values in XPA but are used as statements/conditions in C#
- without an expected type, the strict engine often refuses to coerce, which is correct but incomplete

### 8. Generated Code Cleanup

Post-generation cleanup still exists:

- `Writer/Core/ProjectGenerator.GeneratedCodeCleanup.cs`

Risk:

- this is explicitly outside the ideal emission standard
- some cleanup is structural and historical, but new type/coercion fixes must not be added here

## Telemetry Inventory

Source: latest `D:\Projetos_CSharp\Conversoes\Versao_2` conversion telemetry available during this inventory. The batch was interrupted while `CGCalculoImposto` was still running, so these numbers are a snapshot, not a final green validation.

Total `EMITTED_EXPR_UNRESOLVED` records: `7314`

By sink:

| Sink | Count |
|---|---:|
| ExpectedValue | 5524 |
| EvaluateStatement | 902 |
| BooleanCondition | 782 |
| Assignment | 64 |
| RunArgument | 29 |
| CallArgument | 6 |
| SqlExpression | 4 |
| BindValue | 2 |
| ViewBinding | 1 |

High-risk unresolved sinks for coercion/build:

| Sink | Count |
|---|---:|
| Assignment | 64 |
| RunArgument | 29 |
| CallArgument | 6 |
| SqlExpression | 4 |
| BindValue | 2 |
| ViewBinding | 1 |

Top unresolved roots:

| Root | Count |
|---|---:|
| `u.TranslateNR` | 1148 |
| `u.FlwMtr` | 738 |
| `u.KBGet` | 474 |
| `Counter` | 434 |
| `ENV.Security.UserManager.CurrentUser.Description` | 374 |
| `*.IndexOf` family | high volume |
| `Application.Instance.Counter_` | 204 |
| `u.SharedValSet` | 162 |
| `u.IniPut` | 106 |
| `u.CtrlGoTo` | 96 |
| `u.BOM` / `u.EOM` | 138 combined |

Representative high-risk families:

- `Assignment -> byte[]`
  - `u.Cipher`
  - `u.DeCipher`
  - `u.BlobFromBase64`
  - `u.SharedValGet`
  - `u.MTblGet`
  - `XmlCompat.XMLBlobGet`
  - Java/runtime object calls
- `RunArgument -> Text/Number/byte[]`
  - `ENV.Security.UserManager.CurrentUser.Name/Description`
  - `DNText.Text`
  - `DNText.SelectionStart`
  - `typeof(...)`
  - `*.IndexOf(...)`
  - `u.If(...)`
- `BindValue -> byte[]`
  - `u.CurrPosition`
  - `u.BlobFromBase64`
- `SqlExpression`
  - `u.If(...)`
  - `u.TranslateNR`
  - `ENV.Security.UserManager.CurrentUser.Description`

## Interpretation

Not every `EMITTED_EXPR_UNRESOLVED` is a build error. Many are harmless or side-effect expressions where no coercion is needed.

The important point is that every unresolved record is evidence that the strict engine could not close the expression from reliable source/target evidence. When these occur in `Assignment`, `RunArgument`, `BindValue`, `FilterComparison`, `ViewBinding` or SQL-producing contexts, they are the likely source of recurring coercion failures.

## Recommended Next Step

Before fixing more project-specific coercion errors, add a first-class audit layer:

```text
ExpressionEmissionAudit
task:
emitter:
sink:
expectedReturnType:
targetMember:
sourceExpression:
sourceReturnType:
strictPassed:
strictFailureReason:
xmlOrigin:
```

Then make the audit actionable:

1. Track every `EmitExpressionForContext(...)` failure with sink and emitter.
2. Track every no-context call to `ResolveExpressionCode(...)`.
3. Track every direct `TranslateXpaExpressionToCSharp(...)` used in a sink.
4. Fail validation for high-risk sinks when `expectedReturnType` is empty and a target exists.
5. Keep low-risk side-effect contexts diagnostic only.

This turns the current reactive pattern into a measurable migration plan: close one sink family at a time, validate with sentinels, and avoid creating new string-level repair paths.
