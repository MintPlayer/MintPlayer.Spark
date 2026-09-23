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

---

# Runtime query diagnostics

Everything above is a **compile-time** diagnostic: a `SPARK*` code raised by an analyzer or a
generator, fixable before the app runs. This section is different in kind — these are facts a running
query reports about its own execution, and they exist because the alternative is guessing.

They are logged **once per distinct outcome**, not per request. A query's shape does not change
between requests, so repeating it would be noise that buries the one line that matters.

## Paging mode (#431)

```
Query GetCars: paging is pushed into the database. Row filter: NoRule.
Query GetPeople: paging runs in memory over the whole secured result set. Row filter: ProjectionFallback.
```

A query pages in one of two ways, and the difference is large: the second materializes the entire
secured result set to return one page.

Paging is pushed down only when **nothing can remove rows after the database answers**. Four
conditions, all of which must hold:

| Condition | Why |
|---|---|
| The row filter left nothing to remove | otherwise the database's page and the caller's page are different sets |
| No `restrictToIds` | that path returns exactly the rows asked for and ignores paging |
| No in-memory search fallback | it narrows *after* materialization |
| No index projection | the gate dedupes by id, and a fan-out index makes `Skip(n)` skip *entries*, not documents |

The `Row filter:` half names which branch composition took:

| Mode | Meaning | Pages in database |
|---|---|---|
| `SystemContext` | not a viewer to scope rows for | yes |
| `NoRule` | the type declares no row rule — the common case | yes, unless refined |
| `ConstantPredicate` | "all" or "none", evaluated in memory | no |
| `ProjectionFallback` | typed on the entity, query returns a projection | no |
| `PushedDown` | composed into the query as a `Where` | yes, unless refined |

`, refined per row by IsAllowedAsync` means the type also refines per row **after** materialization.
That still removes rows, so it refuses pushdown even on the `PushedDown` branch — the expression
composed, but it was not the only gate.

⚠️ **`ProjectionFallback` is the default for an indexed query, not an edge case.** A grid reporting it
is behaving normally; it is simply paying O(result set). If that matters, the fix is making the row
filter composable into projections, which is a larger piece of work than this line suggests.
