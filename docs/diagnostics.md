# Spark compiler diagnostics

Every `SPARK*` diagnostic this repository can emit, what raises it, and whether it offers a code
fix. There was no such list until now, which is how a retired id stayed cited in the README for
months and an allocated one was never used.

## Where they come from

Two assemblies, and **which one raises a diagnostic decides which projects see it** — a project only
gets a diagnostic (and its code fix) from an analyzer it references.

| Assembly | Referenced by | Reaches NuGet consumers via |
|---|---|---|
| `MintPlayer.Spark.SourceGenerators` | apps, entity libraries, most `libs/**` | `MintPlayer.Spark.AllFeatures` |
| `MintPlayer.Spark.LibraryGenerators` | apps and entity libraries | `MintPlayer.Spark.AllFeatures` |

⚠️ `LibraryGenerators` was packed into **no** package before `10.0.0-preview.80`, so SPARK016 —
the diagnostic guarding the generated row key — reached no external consumer at all.
`AnalyzerPackagingTests` now fails if any Roslyn component stops being packed.

## The diagnostics

| Id | Severity | Title | Raised by | Code fix |
|---|---|---|---|---|
| SPARK001 | Error | Projection property type mismatch | `ProjectionPropertyAnalyzer` | — |
| SPARK002 | Error | Projection property missing `[Reference]` | `ProjectionPropertyAnalyzer` | — |
| SPARK003 | — | **Retired.** Was `IgnoredBreadcrumbFieldAnalyzer`; no code exists | — | — |
| SPARK004 | Warning | `UseSpark()` should be called after `UseRouting()` | `MiddlewareOrderAnalyzer` | — |
| SPARK005 | Warning | Indexed field has no sort companion | `SortCompanionAnalyzer` | — |
| SPARK006 | Warning | Sort companion is never assigned in the index map | `SortCompanionAnalyzer` | — |
| SPARK007 | Warning | `[Breadcrumb]` inside a `[FromIndex]` projection has no effect | `BreadcrumbPlacementAnalyzer` | — |
| SPARK008 | Warning | `[Breadcrumb]` on a property kind that cannot carry it | `BreadcrumbPlacementAnalyzer` | — |
| SPARK009 | Error | Multiple `[DefaultIndex]` markers over one collection type | `DefaultIndexAnalyzer` | — |
| SPARK010 | Warning | `MapControllers()` mounts controllers outside Spark's pipeline | `MapControllersAnalyzer` | — |
| SPARK011 | Warning | Security right names an action Spark never asks for | `SecurityConfigurationAnalyzer` | — |
| SPARK012 | Warning | Security right names a type no model file declares | `SecurityConfigurationAnalyzer` | — |
| SPARK013 | Warning | Security right is granted to an undeclared group | `SecurityConfigurationAnalyzer` | — |
| SPARK014 | Warning | Security resource has three segments and can never match | `SecurityConfigurationAnalyzer` | — |
| SPARK015 | — | **Unallocated.** Never used; do not reuse without checking release notes | — | — |
| SPARK016 | Error | Value object must be partial | `ValueObjectKeyReporter` (a *generator*, not an analyzer) | ✅ Declare the value object 'partial' |
| SPARK017 | Error | Embedded type is missing `[ValueObject]` | `ValueObjectCompletenessAnalyzer` | ✅ Make this a value object |

Two further id namespaces are generator-only and not analyzer diagnostics: `SPARK_INDEX_001…012`
(`GenerateIndexDiagnostics.cs`, note `004` is absent) and `SPARK_TRANS_001…`
(`TranslationsDiagnostics.cs`).

## Code fixes

Both fixes live in `MintPlayer.Spark.LibraryGenerators`, alongside the generator that raises
SPARK016. They are an **IDE affordance**: `dotnet build` gains nothing, and the error severity
remains the enforcement.

⚠️ **A fix cannot reach a type that arrives as a metadata reference** — a NuGet-packaged entity
library, or any `dotnet build`, where a referenced project has become a `.dll`. There is no
document to edit. In a loaded IDE solution the same reference is a `CompilationReference`, source
locations survive, and the fix works across the project boundary.

⚠️ **Report at a location the analyzed symbol owns, and let only the fix travel.** Two separate
measurements forced this rule, and any future cross-project diagnostic must follow it:

- A diagnostic whose location lies in a syntax tree the analyzed **compilation** does not contain is
  discarded outright — a two-project fixture reported zero where the identical single-project
  fixture reported one.
- A diagnostic whose location lies outside the **document** being analyzed is filtered from that
  document's live diagnostics, so it never gets a light bulb even within one project.

⚠️ **Registration kind decides whether a fix can be offered at all.** `RegisterCompilationAction`
produces compilation-end diagnostics: they reach the Error List on build but are not live, and the
light bulb only offers fixes for live ones. SPARK017 originally used one, squiggled correctly, and
was never offered its fix. It is now a `RegisterSymbolAction` on the `SparkContext` subclass — the
same registration INTF001 uses, which is why that rule's fix always worked.

SPARK017 therefore reports on the `SparkContext` property that reaches the offending type, and
carries the type's metadata name in the `SparkOffendingType` diagnostic property so the fix can
resolve the declaration through the solution.

See `docs/prd/PRD-Analyzer-Code-Fixes.md` for the full design and the measurements behind it.
