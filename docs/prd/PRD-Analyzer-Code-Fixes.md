# PRD — Code fixes for the Spark diagnostics

**Status: PROPOSED** · Plan: `plan-analyzer-code-fixes.md`

## 1. Problem

`SPARK016` ("value object must be partial") and `SPARK017` ("embedded type is missing
`[ValueObject]`") are both **errors**, and both have a fix that is purely mechanical: add the
`partial` keyword, and add the `[ValueObject]` attribute. The author is told exactly what to type and
then types it by hand, once per offending type, with no tooling assistance whatsoever.

The repository has never shipped a `CodeFixProvider`. A repo-wide search for `CodeFixProvider`,
`CodeRefactoringProvider` and `ExportCodeFixProvider` returns **zero** occurrences, and
`Microsoft.CodeAnalysis.CSharp.Workspaces` is referenced by nothing.

## 2. Why it was believed impossible — and the measurement that corrects it

The standing belief in this repository is recorded in `ValueObjectCompletenessAnalyzer.Rules.cs:12-22`
and in the memory note *"analyzers cannot locate cross-assembly types — no location at dotnet build;
severity is the enforcement, codefix is IDE-only"*. The reasoning was: the offending type lives in an
entity library, the analyzer runs in the application compilation, so under `dotnet build` the type
arrives as a metadata `.dll` with zero `DeclaringSyntaxReferences` and `Locations[0].Kind ==
MetadataFile`. No location, no squiggle, no anchor for a fix.

**Every word of that is true, and it does not reach the conclusion that was drawn from it.** It rules
out a code fix *at the command line*, which nobody wanted. It says nothing about the IDE, which is
where code fixes are consumed.

The counter-example is `INTF001` in `C:\Repos\MintPlayer.Dotnet.Tools\SourceGenerators`
(`InterfaceImplementationAnalyzer`), which does the harder version of exactly this: a diagnostic
reported on `Person.LastName` in one project is fixed by adding a member to `IPerson` in **another
project**. Measured, not assumed:

- **`dotnet build` on the two-project test solution emits 0 warnings and no `INTF001` at all.** The
  analyzer loads (`/analyzer:…MintPlayer.SourceGenerators.dll`), sees `IPerson` arrive as
  `xlib\obj\Debug\net10.0\ref\xlib.dll`, and deliberately bails at
  `InterfaceImplementationAnalyzer.cs:33-35`:
  ```csharp
  // Skip interfaces that are not defined in source (metadata-only)
  if (iface.Locations.All(l => !l.IsInSource))
      continue;
  ```
- **In a loaded IDE solution the same `ProjectReference` is a `CompilationReference`**, `IPerson`
  keeps a source location, the diagnostic fires, and the fix applies.

The API that carries the cross-project write is a **Solution-scoped** code action —
`createChangedSolution`, not `createChangedDocument` (`InterfaceImplementationAnalyzer.Codefix.cs:31-36`)
— plus a whole-solution document scan (`:41`, `:52-56`):

```csharp
var solution = cfContext.Document.Project.Solution;
var interfaceDocument = solution.Projects
    .SelectMany(p => p.Documents)                       // every project, not just this one
    .FirstOrDefault(d => d.FilePath == interfaceFirstLocation.SourceTree?.FilePath);
…
return solution.WithDocumentSyntaxRoot(interfaceDocument.Id, updatedRoot);
```

Note also what INTF001 does **not** do: it has no separate `.CodeFixes` assembly. The analyzer, its
rule and its fix are three files in the one `MintPlayer.SourceGenerators` project, which references
no `Workspaces` package at all — `CodeFixProvider` resolves through the `Microsoft.CodeAnalysis`
metapackage, the IDE host supplies the Workspaces assemblies at runtime, and the project simply
carries the resulting **RS1038** warnings. That is a live, shipping counter-example to this repo's
memory note *"a CodeFixProvider needs its own assembly"*, and §4/S3 below decides which way Spark goes.

**The honest scope, therefore:** these fixes are an IDE affordance. `dotnet build` behaviour does not
change, the error is still the enforcement, and CI is unaffected. That is worth building anyway —
`SPARK017` on a real model flags a whole subtree at once, and each one is a two-token edit in a file
the developer then has to go find.

## 3. What already exists, and what it is worth

| Piece | State | Worth to this work |
|---|---|---|
| `ValueObjectCompletenessAnalyzer` (SPARK017) | `[DiagnosticAnalyzer]`, `RegisterCompilationAction` | The diagnostic exists and is well-tested (10 facts). Its **registration kind** is the main open risk — see S1. |
| `ValueObjectKeyReporter` (SPARK016) | **not** an analyzer — an `IDiagnosticReporter` inside `ValueObjectKeyGenerator`'s pipeline | Roslyn matches fixes by diagnostic **id**, not by producer, but whether the IDE lightbulb offers a fix for a *generator*-emitted diagnostic is unmeasured — see S2. |
| `GeneratorHarness` (`tests/…/_Infrastructure/`) | hand-rolled; `RunAnalyzerAsync` over `compilation.WithAnalyzers`; analyzers located by **type-name string** + `Activator.CreateInstance` | Extends naturally to a `RunCodeFixAsync`. `Microsoft.CodeAnalysis.Testing` is deliberately not used here and should stay unused. |
| `MintPlayer.Spark.AllFeatures` packaging | the only package that packs analyzer DLLs into `analyzers/dotnet/cs` | The delivery vehicle for a fix assembly, if a separate one is needed. |
| `docs/guide-asdetail-attributes.md:272-287` | the "two checks, in two places" table | The doc surface to update; it currently never mentions code fixes. |

## 4. Design

### C1 — SPARK016 gets a code fix: add `partial`

The simplest possible fix and the one with no cross-project component at all: SPARK016 is reported by
a reporter running in the **same compilation that owns the syntax** (the entity library), so the
location is always real source in the current project. The fix adds the `partial` modifier to the
`TypeDeclarationSyntax` the diagnostic points at. A `createChangedDocument` action suffices.

### C2 — SPARK017 gets a code fix: add `[ValueObject]` **and** `partial`, together

`ValueObjectCompletenessAnalyzer` deliberately does not check `partial` (`:32-36`: the keyword is
source-only and leaves no trace in IL). A fix that adds only the attribute therefore **trades
SPARK017 for SPARK016 on the next build** — a strictly worse experience than no fix, because the
developer now believes the tool handled it. The fix must emit both in one edit, and add the
`using MintPlayer.Spark.Abstractions;` directive when absent.

### C2b — the fix decides `partial` by *reading* it, and the "cannot check" claim is retired

`ValueObjectCompletenessAnalyzer.Rules.cs:32-36` states that the analyzer "deliberately does not
check `partial`" because "that keyword is source-only and leaves no trace in IL, so no metadata
symbol can report it — and the obvious helper gets it silently wrong, computing partial-ness as
`All()` over an empty `DeclaringSyntaxReferences`, which is `true` for every metadata type."

The observation is right and the conclusion drawn from it is not. That is a **vacuous-truth bug**,
not a capability limit, and the guarded form is already in this repository —
`ValueObjectKeyGenerator.cs:85-86`:

```csharp
IsPartial = declarations.Length > 0
    && declarations.All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword)),
```

An analyzer can read partiality for any symbol that has source declarations: every same-project
type, and every cross-project type in a loaded IDE solution (`CompilationReference`). It genuinely
cannot for a true metadata symbol — but there the diagnostic has no location and the fix has no
`Document`, so nothing is lost that was reachable.

Two consequences:

1. The fix in C2 must **read** the target's modifiers with the guarded idiom above and add `partial`
   only when it is actually absent, rather than blindly inserting it (which would produce
   `partial partial class` on a type that already has it, or fight a second declaration).
2. The XML doc at `.Rules.cs:32-36` and the mirrored claim in
   `docs/guide-asdetail-attributes.md:272-287` are corrected to say what is true: the split between
   SPARK016 and SPARK017 is a **responsibility** boundary (SPARK016 owns the compilation that owns
   the syntax), not a capability one. Leaving the current wording in place is what caused this PRD's
   premise to be doubted in the first place.

**To be unambiguous, because the wording above has already misled once:** one invocation of the
SPARK017 fix produces **one** edit that adds the `[ValueObject]` attribute *and* the `partial`
keyword (and the `using`) together. The developer never sees an intermediate state, and SPARK016
never fires as a consequence of having applied the fix. Reading the modifiers first is only about
*not duplicating* a `partial` that is already there — it is not a reason to defer adding one that
isn't.

What is out of scope is narrower: whether the **SPARK017 analyzer** should also *report* a missing
`partial` on its own. It should not, and nothing needs it to — SPARK016 already owns that diagnostic
for hand-written code, in the compilation that owns the syntax. That is the responsibility boundary;
it constrains the diagnostics, not the fix.

### C3 — SPARK017's fix is Solution-scoped and may cross a project boundary

The offending type is normally in an entity library while the `SparkContext` that roots the walk is
in the application. In the IDE that is a `CompilationReference`, so a source location exists but
belongs to another project's `Document`. The fix therefore uses `createChangedSolution` and resolves
the document across `solution.Projects`, as INTF001 does.

**Prefer `solution.GetDocumentIdsWithFilePath(path)` over INTF001's `SelectMany(p => p.Documents)`
`FilePath` string comparison.** The scan is O(all documents in the solution) on every invocation and
the string compare is exactly the trap that harness comment at `CodeFixHarness.cs:229-235` records —
a missing `filePath:` argument made the comparison match nothing and silently rendered the whole fix
body unreachable *while the tests passed*. Better still, prefer the symbol's own
`DeclaringSyntaxReferences` where available and fall back to the file-path lookup.

### C4 — the fixes must not break `dotnet build`

A `CodeFixProvider` derives from a `Workspaces` type. The command-line analyzer host (csc) does not
load Workspaces, so an assembly containing both an analyzer and a fix can throw
`ReflectionTypeLoadException` during type discovery. **This already happened here**: adding a fix
provider to `MintPlayer.Spark.SourceGenerators` broke every test in the generator test suite
(measured 2026-08-18 on SPARK005, R26 in `docs/issue_210_PRD.md`) because `GeneratorHarness` calls
`asm.GetTypes()`. That specific failure is a harness problem and M1 resolves it by referencing
Workspaces in the test project; whether the same arrangement is safe on a *consumer's* build is the
open question. INTF001 ships exactly that arrangement and its test projects document the hazard
explicitly. Spark ships analyzers to **external NuGet
consumers** via `MintPlayer.Spark.AllFeatures`, so a discovery failure would be their build breaking,
not ours.

**Decided: no new project. Both fixes live in `MintPlayer.Spark.LibraryGenerators`.** RS1038 is
accepted, exactly as INTF001 accepts it. A separate `MintPlayer.Spark.CodeFixes` assembly is *not*
built: it would need its own csproj, its own `<None PackagePath="analyzers/dotnet/cs">` items (the
inherited props pack only the one analyzer DLL), and a reference added to every consuming project.

S3 keeps only its consumer-build half — prove csc tolerates an assembly carrying both a generator
and a fix provider. The measured 2026-08-18 failure is a *harness* problem, resolved by M1 adding
`Microsoft.CodeAnalysis.CSharp.Workspaces` to the test project (which it needs anyway for
`AdhocWorkspace`), and the harness loads `LibraryGenerators` by name too, so the same resolution
covers it.

### C4b — reachability: `LibraryGenerators` must be referenced wherever a fix should appear

This is the cost of C4's placement, and it is a requirement, not a caveat. Visual Studio offers
fixes only from assemblies the relevant project references, and the two diagnostics are raised in
**different compilations** — SPARK016 in the entity library, SPARK017 in the application. Today
`LibraryGenerators` is referenced by exactly four projects (`CodeCoverage.Library`, `Fleet.Library`,
`HR.Library`, `IdentityProvider`) and is packed into **no** NuGet package at all.

So, in the same PR:

1. Add the `LibraryGenerators` analyzer `ProjectReference` to every project that can raise either
   diagnostic — the four app hosts (`CodeCoverage`, `DemoApp`, `Fleet`, `HR`) for SPARK017, and
   `DemoApp.Library`, which is the one entity library missing it today.
2. Pack `MintPlayer.Spark.LibraryGenerators.dll` into `MintPlayer.Spark.AllFeatures` (C6.1), without
   which no external consumer gets either the SPARK016 diagnostic or any fix.

⚠️ **Open, and S1 must answer it:** when a diagnostic is raised in the *app* compilation but its
location is a file owned by a *library* project, it is not established whether the lightbulb
consults the analyzer references of the reporting project or of the document's project. If it is the
document's project, step 1's app-host references are insufficient on their own and every entity
library needs the reference too. Measure before relying on either.

### C5 — a code-fix test harness in the existing style

Extend `GeneratorHarness` with `RunCodeFixAsync`, built on `AdhocWorkspace`, locating the
`CodeFixProvider` by type-name string exactly as analyzers are located today. It must support a
**two-project** workspace, or it cannot cover C3 at all — the INTF001 harness is single-project and
single-document, and consequently none of its five code-fix tests exercises the cross-project path
that is the feature's whole point.

### C6 — adjacent defects found while investigating

These are in scope for the same PR (one-PR rule), because they are the same mechanism and three of
them make the diagnostics silently absent:

1. **`MintPlayer.Spark.LibraryGenerators.dll` is packed into no NuGet package.** `AllFeatures.csproj:44-53`
   packs `AllFeatures.SourceGenerators.dll` and `Spark.SourceGenerators.dll` only. **SPARK016 is
   therefore unreachable for every external consumer** — and it is the diagnostic that guards the
   generated row key.
2. **`apps/DemoApp/DemoApp.Library` does not reference `LibraryGenerators`** (the other three app
   libraries do). DemoApp's value objects get no SPARK016.
3. **Neither analyzer csproj packs its own DLL.** With `IncludeBuildOutput=false` and no
   `analyzers/dotnet/cs` `ItemGroup`, `dotnet pack` on `MintPlayer.Spark.SourceGenerators` /
   `.LibraryGenerators` produces nupkgs containing no analyzer. They work only via AllFeatures or an
   in-repo ProjectReference.
4. **`SPARK003` does not exist** but is cited by `README.md:354` and
   `docs/code-coverage/adopt-generated-indexes.md:88`; `SPARK015` is allocated to nothing. There is no
   central diagnostics list anywhere in the repo.

### C7 — Non-goals

- **Making SPARK017 fixable from `dotnet build`.** Measured impossible: no location, no document, no
  workspace. The error remains the enforcement.
- **Fixing a type that arrives as a true metadata reference** (a NuGet-packaged entity library). There
  is no `Document`, so there is nothing to edit. The diagnostic still fires; only the fix is absent.
- **Code fixes for the other fifteen diagnostics.** SPARK001/002/005/006 are plausible follow-on
  candidates and are explicitly *not* being built here; this PRD establishes the mechanism.
- **Adopting `Microsoft.CodeAnalysis.Testing`.** The repo's reflection-load harness is incompatible
  with its `ReferenceAssemblies` machinery.
- **Repairing INTF001's own two defects** (it fixes `Interfaces.FirstOrDefault()` rather than the
  interface the diagnostic named, and its member filter is looser than the analyzer's so a public
  field reaches `throw new NotImplementedException`). Different repository, different PR — but Spark's
  equivalents must not copy either mistake, and C5's tests pin that.

## 5. Acceptance criteria

1. In Visual Studio, a `[ValueObject]` type that is not `partial` and has no `[ValueKey]` offers
   **Add 'partial' modifier**, and applying it clears SPARK016.
2. In Visual Studio, a type flagged SPARK017 offers **Make this a value object**, and applying it adds
   `[ValueObject]`, `partial` **and** the `using` in one edit, clearing both SPARK017 and SPARK016.
3. Criterion 2 holds when the offending type is in a **different project** of the same loaded
   solution from the `SparkContext`.
4. `dotnet build` of every app in the repo, and of a clean consumer project referencing the
   `MintPlayer.Spark.AllFeatures` nupkg from a local feed, produces **no analyzer-discovery warning or
   error** (`AD0001`, `CS8032`, `ReflectionTypeLoadException`) — measured, not assumed.
5. Tests cover: SPARK016 single-project fix; SPARK017 same-project fix; SPARK017 **cross-project**
   fix; SPARK017 fix on a type that already has a `using`; no fix offered when the type is
   metadata-only.
6. `MintPlayer.Spark.LibraryGenerators.dll` is packed into `MintPlayer.Spark.AllFeatures`, and a test
   or CI check asserts every analyzer DLL that exists is packed somewhere.
7. `apps/DemoApp/DemoApp.Library` references `LibraryGenerators` as an analyzer.
8. Applying the SPARK017 fix to a type that is **already** `partial` adds the attribute only — no
   duplicate modifier — and a test pins it.
9. `docs/guide-asdetail-attributes.md` documents both fixes and states plainly that they are
   IDE-only; the "cannot check `partial`" claim there and at
   `ValueObjectCompletenessAnalyzer.Rules.cs:32-36` is replaced with the responsibility-boundary
   wording from C2b; a new `docs/diagnostics.md` lists every SPARK id with its severity and owning
   project, and the SPARK003 citations in `README.md` and `adopt-generated-indexes.md` are corrected.
10. Versions bumped in every touched `libs/**` project (CI gate at
   `.github/workflows/pull-request.yml:184-188`), majors unchanged — this is a preview minor.

## 6. Sizing

Small-to-medium, and **front-loaded with risk**: the three spikes in the plan decide whether this is
a two-file addition or a new packaged assembly. If S1 or S2 comes back negative, the corresponding
fix is not buildable at all and this PRD shrinks to whichever half survives plus C6.
