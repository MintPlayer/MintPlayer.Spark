# PRD: FromIndex Projection Property Analyzer

**Status:** In Progress
**Last Updated:** 2026-03-10

## Problem

When a RavenDB index projection type (e.g., `VPerson`) is marked with `[FromIndex(typeof(IndexClass))]`, it must keep its properties consistent with the base entity type (e.g., `Person`) that the index maps from. Two common bugs:

1. **Property type mismatch** — A property exists on both the projection and entity types with the same name but different types. This causes silent runtime failures in entity mapping.
2. **Missing `[Reference]` attribute** — The base entity property has `[Reference(typeof(Target))]` but the projection property does not (or has a different target type). This causes reference breadcrumbs to show raw GUIDs instead of resolved display names on query-list pages.

These bugs are caught only at runtime and are hard to diagnose. A compile-time Roslyn analyzer prevents them.

## Solution

Add a `DiagnosticAnalyzer` to `MintPlayer.Spark.SourceGenerators` that validates projection types at compile time.

### Algorithm

1. **Find projection types**: Find all classes with `[FromIndex(typeof(IndexClass))]` attribute
2. **Resolve index type**: Get the `Type` argument from the attribute constructor
3. **Get base entity type**: Walk the index type's base class chain to find `AbstractIndexCreationTask<T>` and extract `T`
4. **Compare properties**: For each property on the projection type, find the matching property (by name) on the entity type:
   - **SPARK001**: If both exist but have different types → Error
   - **SPARK002**: If the entity property has `[Reference]` but the projection property does not, or has a different target type → Error

### Diagnostics

| ID | Severity | Message |
|----|----------|---------|
| SPARK001 | Error | Property '{0}' on projection type '{1}' has type '{2}' but the corresponding property on entity type '{3}' has type '{4}' |
| SPARK002 | Error | Property '{0}' on projection type '{1}' is missing [Reference(typeof({2}))] attribute that exists on entity type '{3}' |

### Design Decisions

- **Error severity** for both diagnostics — these are bugs, not style issues
- **Only check matching property names** — projection types often have fewer properties than entity types (e.g., computed `FullName`), so missing properties are fine
- Properties that exist only on the projection type (like `FullName`) are skipped — they're index-computed fields
- The analyzer runs on all projects that reference `MintPlayer.Spark.SourceGenerators` as an analyzer

## Files

| File | Change |
|------|--------|
| `MintPlayer.Spark.SourceGenerators/Diagnostics/ProjectionPropertyAnalyzer.cs` | New analyzer class |
| `MintPlayer.Spark.SourceGenerators/Diagnostics/ProjectionPropertyAnalyzer.Rules.cs` | Diagnostic descriptors |

## Implementation Notes

- Follow the `DiagnosticAnalyzer` pattern from `C:\Repos\MintPlayer.Dotnet.Tools\SourceGenerators`
- Use `RegisterSymbolAction` with `SymbolKind.NamedType`
- Use `context.Compilation.GetTypeByMetadataName()` to find `FromIndexAttribute`, `ReferenceAttribute`, and `AbstractIndexCreationTask`
- Use `SymbolEqualityComparer.Default.Equals()` for all type comparisons
- Enable concurrent execution and skip generated code
- Target `netstandard2.0` (already the project's target)
- Attribute metadata names:
  - `MintPlayer.Spark.Abstractions.FromIndexAttribute`
  - `MintPlayer.Spark.Abstractions.ReferenceAttribute`
  - `Raven.Client.Documents.Indexes.AbstractIndexCreationTask`1`

## Out of Scope

- Code fix providers (auto-adding `[Reference]` to projection types)
- Checking property visibility/accessibility differences
- Validating the index Map expression itself
