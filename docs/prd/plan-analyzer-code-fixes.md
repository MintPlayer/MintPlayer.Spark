# Plan — Code fixes for the Spark diagnostics

**Status: NOT STARTED** · PRD: `PRD-Analyzer-Code-Fixes.md`

Three spikes first, because two of them can each kill half the feature and both are cheap to
measure. Nothing is designed further until S1–S3 report.

---

## Spikes

### S1 — does the IDE offer a code fix for a `RegisterCompilationAction` diagnostic?

**The risk.** `ValueObjectCompletenessAnalyzer` registers a *compilation action* (`:56`), not a
symbol or syntax-node action, because the question is about a graph whose roots live in another file.
Compilation-end diagnostics are computed by the IDE on a different cadence from per-document ones,
and the lightbulb only offers fixes for diagnostics Roslyn currently associates with the open
document's span. It is entirely possible that SPARK017 squiggles but is never offered a fix.

**Measure it, do not reason about it.** Write the thinnest possible `CodeFixProvider` for SPARK017
that does nothing but return a no-op action with a distinctive title, load it in VS against
`apps/HR`, and look at whether the lightbulb shows it on an unmarked type.

**Answer a second question in the same sitting** (PRD C4b): SPARK017 is raised in the *app*
compilation but its location is a file owned by a *library* project. Does the lightbulb consult the
analyzer references of the reporting project, or of the document's project? The answer decides
whether M4's reference list is the four app hosts or every entity library. Test it by giving only
the app host the `LibraryGenerators` reference and seeing whether the fix appears on the library's
file.

**If negative:** the fallback is a second registration in the analyzer — keep the compilation action
as the enforcement, and add a `RegisterSymbolAction` that re-reports the *same id* for offenders it
can see per-symbol. Measure the double-report risk before adopting it.

**Cost:** ~1h. **Blocks:** C2, C3, and therefore most of the value.

### S2 — does the IDE offer a code fix for a diagnostic emitted by a *source generator*?

**The risk.** SPARK016 does not come from a `DiagnosticAnalyzer` at all. It comes from
`ValueObjectKeyReporter`, an `IDiagnosticReporter` running inside `ValueObjectKeyGenerator`'s
pipeline (`ValueObjectKeyReporter.cs:34-38`). Roslyn matches fixes to diagnostics by **id**, and
`FixableDiagnosticIds` is just a string list, so on paper a fix is offerable. Whether the IDE
actually surfaces the lightbulb for a generator-produced diagnostic is unmeasured.

**Measure it** the same way: no-op fix provider declaring `FixableDiagnosticIds = ["SPARK016"]`, load
in VS against `apps/HR/HR.Library`, look.

**If negative:** promote the partial check to a real `DiagnosticAnalyzer` alongside the reporter
(same id, same message; the reporter keeps emitting for the generator path, or is retired in its
favour). That is a bigger change and would need its own milestone.

**Cost:** ~1h. **Blocks:** C1.

### S3 — can a `CodeFixProvider` ship in the same assembly as an analyzer without breaking `dotnet build`?

**The risk, and it is already measured once — in this repo, against us.** Putting a
`CodeFixProvider` in `MintPlayer.Spark.SourceGenerators` **broke every test in
`MintPlayer.Spark.SourceGenerators.Tests`**, not just its own (measured 2026-08-18 while building
SPARK005; recorded as R26 in `docs/issue_210_PRD.md`). Mechanism: `GeneratorHarness` loads the
analyzer assembly by name and calls `asm.GetTypes()`, which throws `ReflectionTypeLoadException`
when any type's base type is unresolvable — and `CodeFixProvider`'s base lives in
`Microsoft.CodeAnalysis.Workspaces`, deliberately not packed into `analyzers/dotnet/cs` because the
IDE host supplies it. It **compiles fine**, which is what makes it a trap.

So S3 is not "does this work" — it is "is that failure a *harness* problem or a *consumer* problem".
The two are separable, and the answer decides the layout:

- The harness half is fixable outright: M1 adds `Microsoft.CodeAnalysis.CSharp.Workspaces` to the
  test project (which it needs anyway for `AdhocWorkspace`), and the base type resolves.
- The consumer half is the real question, and is untested. INTF001 ships analyzer + fix in one
  assembly to real NuGet consumers, and the `MintPlayer.SourceGenerators` test csprojs document the
  hazard at `:26-32` / `:37-45` while shipping anyway — evidence it can work, not proof it works on
  a clean consumer build.

**Placement is decided, not a spike output: both fixes go in `MintPlayer.Spark.LibraryGenerators`,
no new project** (PRD C4). S3 therefore only has to prove the arrangement is safe, and to produce
the fallback signal if it is not.

**Measure:** with the fix providers in `LibraryGenerators`, `dotnet pack` AllFeatures to a local
feed, consume it from a throwaway project, and build with `/warnaserror:AD0001,CS8032` and `-v:n`.
A clean build with the analyzer still reporting is a pass.

**If it fails:** the fallback is a separate `MintPlayer.Spark.CodeFixes` assembly with its own
`<None PackagePath="analyzers/dotnet/cs">` items — the inherited `MintPlayer.SourceGenerators.Tools`
props pack only the one analyzer DLL, so a second assembly must pack itself. Do not pre-build it.

**Cost:** ~2h. **Blocks:** packaging only — M1/M2/M3 proceed regardless.

---

## Milestones

Ordered so the cheapest, least risky fix ships first and proves the harness.

### M0 — record the spike outcomes

Write S1/S2/S3 results back into the PRD as a `## Decisions taken during implementation` section with
`D1…D3`, following the house style. If a spike came back negative, amend the affected requirement in
the PRD before writing code.

### M1 — RED: the code-fix harness, with a two-project workspace

`GeneratorHarness.RunCodeFixAsync(codeFixTypeName, analyzerOrGeneratorSource, …)` over an
`AdhocWorkspace`, locating the provider by type-name string + `Activator.CreateInstance` exactly as
`RunAnalyzerAsync` locates analyzers today (`_Infrastructure/GeneratorHarness.cs:84`). Requires
`Microsoft.CodeAnalysis.CSharp.Workspaces` in the test project.

**Two things the INTF001 harness got wrong and this one must not:**

- It builds **one** project and **one** document, so it structurally cannot cover the cross-project
  case — and none of its five fix tests does.
- Its documents were originally added without a `filePath:`, so the fix's `d.FilePath == …`
  comparison matched nothing and the entire fix body was unreachable **while the tests passed**
  (`CodeFixHarness.cs:229-235`). Assert the fix actually changed something, not merely that it ran.

Land this milestone red: the harness plus failing tests for M2/M3.

### M2 — GREEN: SPARK016 → add `partial`

`createChangedDocument`; same compilation, real source location, no cross-project component. This is
the milestone that validates the harness end to end at the lowest risk.

### M3 — GREEN: SPARK017 → add `[ValueObject]` + `partial` + `using`, cross-project

`createChangedSolution`. Resolve the target document via the symbol's `DeclaringSyntaxReferences`
first, then `solution.GetDocumentIdsWithFilePath(path)` — **not** INTF001's
`solution.Projects.SelectMany(p => p.Documents)` string scan.

Two traps carried over from INTF001's own defects, both of which the tests must pin:

- Fix the entity the **diagnostic named**, not `…FirstOrDefault()`. INTF001 adds the member to the
  first interface in the list rather than the one the diagnostic reported, and gets it wrong for a
  class implementing two source interfaces.
- Keep the fix's applicability filter **identical** to the analyzer's. INTF001's is looser, so a
  public field reaches `CreateInterfaceMember` and throws `NotImplementedException` at the user.

Emitting only the attribute and not `partial` is a regression, not a partial win: it trades SPARK017
for SPARK016 on the next build. One edit, both changes, or the milestone is not done.

**Read the modifiers, don't assume them.** Partiality is perfectly readable for any symbol with
source declarations, and the guarded idiom is already in this repo at `ValueObjectKeyGenerator.cs:85-86`:

```csharp
IsPartial = declarations.Length > 0
    && declarations.All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword)),
```

The `Length > 0` guard is the whole point — without it, `All()` over an empty
`DeclaringSyntaxReferences` is vacuously `true` for every metadata type. That bug is what produced
the "analyzers cannot check `partial`" claim now sitting in
`ValueObjectCompletenessAnalyzer.Rules.cs:32-36`; M6 retires the claim. Add the keyword only when it
is genuinely absent, and pin the already-`partial` case with a test.

### M4 — the packaging and reachability defects (PRD C4b + C6)

Both fixes ship inside `LibraryGenerators`, so this milestone is not housekeeping — **it is what
makes the fixes appear at all.**

- Pack `MintPlayer.Spark.LibraryGenerators.dll` into `MintPlayer.Spark.AllFeatures` alongside the
  other two (`AllFeatures.csproj:44-53`). **SPARK016 currently reaches no external consumer at all**,
  and now neither would either fix.
- Add the `LibraryGenerators` analyzer ProjectReference to `apps/DemoApp/DemoApp.Library` (the one
  entity library missing it) **and to the four app hosts** — `CodeCoverage`, `DemoApp`, `Fleet`,
  `HR` — which raise SPARK017 but do not reference the assembly that now carries its fix.
- Revisit that list once S1 reports: if the lightbulb consults the *document's* project rather than
  the reporting one, every entity library needs the reference, not just the app hosts.
- A guard so the next omission is not silent — a test that enumerates the analyzer/generator DLLs
  produced by the build and asserts each appears in some package's `analyzers/` path. This is the
  same shape of trap as the `[ValueObject]`-inert-without-the-generator-reference note and as PRD
  `PRD-Server-Side-Row-Lifecycle.md:251` (F3); it has now cost the repo three times.

### M5 — version bumps

Every touched project under `libs/**` needs a `<Version>` bump or the PR fails the CI gate at
`.github/workflows/pull-request.yml:184-188`. Preview minors only — the NuGet major tracks the
targeted .NET major and does not move here.

### M6 — docs

- `docs/guide-asdetail-attributes.md:272-287` — extend the "two checks, in two places" table with the
  fixes, and say plainly that they are **IDE-only** and that `dotnet build` behaviour is unchanged.
- **Retire the "cannot check `partial`" claim** at `ValueObjectCompletenessAnalyzer.Rules.cs:32-36`
  and wherever the guide mirrors it. Replace with the truth: the SPARK016/SPARK017 split is a
  *responsibility* boundary — SPARK016 owns the compilation that owns the syntax — not a capability
  limit. An analyzer reads partiality fine for any source-declared symbol; only a true metadata
  symbol is opaque, and there it has no location and no fixable document either. The current wording
  overstates a vacuous-truth bug into an impossibility, and it is the reason this PRD's premise was
  doubted.
- New `docs/diagnostics.md` — the full SPARK id table (ids, titles, severities, owning project,
  whether a fix exists). No such list exists anywhere today.
- Correct the `SPARK003` citations in `README.md:354` and
  `docs/code-coverage/adopt-generated-indexes.md:88` (the diagnostic was retired; the doc bug is
  already flagged at `docs/query_pipeline_PRD.md:266`), and note `SPARK015` as unallocated.

---

## Order

S1 ∥ S2 ∥ S3 → M0 → M1 → M2 → M3 → M4 → M5 → M6.

M4 does not depend on the spikes and can be pulled forward if the spikes stall; it is independently
valuable and fixes a live reachability hole.

## Traps

- **The whole feature is IDE-only.** Do not write an acceptance criterion, a test, or a doc sentence
  that implies `dotnet build` gains anything. It gains nothing, by measurement.
- **A fix that leaves a different error behind is worse than no fix** — see M3.
- **A passing code-fix test proves very little by default.** Assert on the fixed source text, and
  assert the document actually changed; INTF001's suite passed for a while against an unreachable fix
  body.
- **Do not add `Microsoft.CodeAnalysis.Testing`.** It fights the reflection-load pattern this repo
  uses to keep netstandard2.0 polyfills out of the net10.0 compile graph
  (`MintPlayer.Spark.SourceGenerators.Tests.csproj:47-53`).
- **Roslyn version.** The analyzer projects pin `Microsoft.CodeAnalysis` 5.0.0 / `.CSharp` 5.3.0 by
  `Update`. Any Workspaces reference must match, and the test project's Roslyn host is already 5.3.0.
- **One PR.** The fixes, the packaging repairs, the DemoApp reference, the guard test, the version
  bumps and the docs land together.
