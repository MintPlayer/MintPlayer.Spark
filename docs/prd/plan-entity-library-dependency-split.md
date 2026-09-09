# Plan — An entity library should not depend on ASP.NET Core

**Status: NOT STARTED** · PRD: `PRD-Entity-Library-Dependency-Split.md`
**Depends on** `MintPlayer.Spark.Attributes` existing (`PRD-AsDetail-Row-Identity.md` A1). This is
the rest of that job: A1 moves the attributes because `[ValueObject]` needs a home, and on its own it
frees exactly one project out of four.

## Milestones

### S1 — ⚠️ Fix the assembly filter properly first
Before adding another package that carries a model attribute, replace
`GenerateIndexGenerator.cs:481`'s literal-name pair with the resolved symbol's assembly:

```csharp
compilation.GetTypeByMetadataName(GenerateIndexAttributeFullName)!.ContainingAssembly.Name
```

∪ `"MintPlayer.Spark.Abstractions"` for pre-split binaries. A1's fix accepts both names, which is
correct but is a patch — **this plan adds exactly the third host that would re-break it.**

Gate: `tests/.../Generators/ReferencedAssemblyEntityTests.cs:18-28` already builds an entity that uses
`[GenerateIndex]`/`[Search]` and nothing else Spark — the precise HR shape. It would have caught the
original breakage. Run it, don't rely on a solution build.

### S2 — Move the four model types
`TranslatedString`, `TransientLookupReference`, `DynamicLookupReference`, `ELookupDisplayType`. All
dependency-free. Prefer a new `MintPlayer.Spark.Model` package over widening `Attributes` past what
its name says.
⚠️ **Keep the namespace `MintPlayer.Spark.Abstractions`** — 50 fully-qualified metadata literals plus
~15 `global::` prefixes emitted *into generated code* are immune to an assembly move and fatal to a
namespace move. Put that in the package README, not only here.

### S3 — Break `Replication.Abstractions`'s dependency
Its csproj `ProjectReference`s Abstractions, so Fleet and HR keep `Microsoft.AspNetCore.App`
regardless of S2. Either drop the reference or move `[Replicated]` into the attributes package.

### S4 — Drop the Abstractions reference from all four entity libraries
`HR.Library` can already; the other three need S2. Verify each builds.

### S5 — Target `netstandard2.0` on at least one
The actual payoff, and the proof S1–S4 worked.

### S6 — `[TypeForwardedTo]` for every moved type
⚠️ Not optional despite preview-grade breaking changes being acceptable. These types are read by
**runtime reflection** (`ModelSynchronizer.cs:684-686`, `ReferenceResolver.cs:15`), so a pre-built
consumer does not fail loudly — it produces a **wrong model**. Verify by loading a pre-split consumer
assembly against the new packages.

### S7 — Versions, in lockstep, in this PR
⚠️ `dotnet-build-master.yml:82-83` packs solution-wide and `:148-149` pushes with `--skip-duplicate`.
Without a bump, the new package publishes while the *changed* one silently keeps its old contents —
and a consumer with both gets CS0433. Worse for the generators: `GetTypeByMetadataName` returns
`null` on a duplicate declaration, so indexes and translations **switch off with no diagnostic**.

### S8 — Docs and CI
Update `guide-reference-attributes`, `guide-asdetail-attributes`, `guide-attribute-descriptions`,
`guide-attribute-grouping`, `guide-translated-strings` to say which package an entity library
references; add an `AGENTS.md` for the new directory. Add a `dotnet pack` step to
`pull-request.yml` — it has none today, so package shape is never checked before nuget.org.

## Order

S1 → S2 → S3 → S4 → S5, with S6 and S7 in the same PR as S2, and S8 last.

S1 genuinely first: it is the guard against the failure this plan would otherwise repeat.

## Traps

1. **The HR breakage was not bad luck.** HR was the only library using Abstractions for *nothing but*
   attributes, which is exactly why its `AssemblyRef` disappeared. CodeCoverage survived only because
   it touches `TransientLookupReference` — which S2 moves. Expect the same class of failure to reach
   the other three unless S1 is done first.
2. **A duplicate type declaration silently disables generators**, it does not error. See S7.
3. **Only one of four libraries benefits from the attribute split alone.** If this plan is dropped,
   say so explicitly rather than leaving the split looking finished.
