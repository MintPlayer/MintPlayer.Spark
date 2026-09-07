# Issue #374 — implementation plan

**PRD:** [issue_374_PRD.md](issue_374_PRD.md)
**Status:** in progress

## Status

| | |
|---|---|
| **M0** establish the facts | **Done** — recorded in the PRD. Both unit kinds are affected; `persistentObject` was found while writing it, not from the issue |
| **M1** the check | **Done** — `VerifyProgramUnitTargetsResolve`, covering query AND persistentObject units |
| **M2** tests | **Done** — 6 facts. One asserts the MESSAGE, not just exit 3, because several checks and the hash all exit 3 |
| **M3** verify against the apps | **Done** — CodeCoverage, DemoApp, Fleet, HR all exit 0; and the check was proven by removing GitHubProject.json's alias, which reproduced #374 and exited 3 |
| **M4** docs | **Done** — `guide-aliases.md` gains the invariant beside the collision rule; also corrected a stale claim there that queries live in `App_Data/Queries/` |

## M1 — `VerifyProgramUnitTargetsResolve`

In `libs/spark/MintPlayer.Spark/Extensions/SparkDevelopmentExtensions.cs`, beside its siblings.

- Read `App_Data/programUnits.json` into `ProgramUnitsConfiguration`. Return on absent; swallow
  `JsonException` (FR5 — the loader owns that error).
- Read `App_Data/Model/*.json` into `EntityTypeFile[]`, as `VerifyQueryAliasesAreUnique` already
  does, giving both the queries and the entity types.
- Build two alias maps, using the **same** derivations as the runtime:
  - queries: `query.Alias ?? SparkQueryAliases.Derive(query.Name)`
  - entity types: `entityType.Alias ?? entityType.Name.ToLowerInvariant()`
- For each unit with a non-empty `Alias`:
  - `type == "query"` → the alias must resolve, and to the query whose `Id == unit.QueryId`;
  - `type == "persistentObject"` → the alias must resolve, and to the entity type whose
    `Id == unit.PersistentObjectId`;
  - anything else → skip (FR6).
- On failure: `Console.Error.WriteLine` and `Environment.ExitCode = ExitDrift`, matching the siblings.

**Message (FR3)** must carry the unit, both identifiers, what the alias actually resolved to, and the
fix — including that aliases are derived, which is the part nobody guesses:

```
Program unit 'Project boards' routes to alias 'github-projects', but that alias resolves to no
query. Its queryId points at 'GetGitHubProjects', whose alias is 'githubprojects' (derived from
the name, because the query declares none). The client fetches /spark/queries/{alias}, so this
unit would 404 at runtime -- and that 404 is indistinguishable from a missing Query right. Fix
either side: add "alias": "github-projects" to the query, or set the unit's alias to
'githubprojects'.
```

**Verify:** `dotnet build`, then `--spark-verify-model` on `apps/CodeCoverage` exits 0.

## M2 — tests

`tests/MintPlayer.Spark.Tests`, driving the check over a temporary content root, which is how the
sibling checks are tested.

| Case | Expectation |
|---|---|
| Unit alias matches the query's **declared** alias | passes |
| Unit alias matches the query's **derived** alias | passes |
| Unit alias resolves nowhere | fails, message names both sides |
| Unit alias resolves to a **different** query than `queryId` | fails |
| PO unit alias resolves nowhere | fails |
| PO unit alias matches a derived entity alias | passes |
| Unit with **no** alias | passes (FR4) |
| `type: "url"` unit with an alias matching nothing | passes (FR6) |
| No `programUnits.json` | passes (FR5) |
| Malformed `programUnits.json` | passes — not this check's error (FR5) |

The last two matter more than they look: a check that fails on an absent menu would break every app
that ships none, and one that reports malformed JSON would double-report the loader's own error.

## M3 — verify against the workspace

Run `--spark-verify-model` for all five apps. The expectation from the PRD's table is that every one
passes today; if one does not, that is a real bug this check just found, and it gets fixed here
rather than worked around.

## M4 — docs

- The `docs/guide-program-units.md` section on aliases, if one exists, gains the invariant.
- `libs/spark/MintPlayer.Spark/README.md` where the verify flags are listed.

## Deliberately not doing

- **A startup check as well.** The build-time one is strictly better for static configuration, and
  two gates for one invariant is two things to keep in step.
- **Auto-deriving the unit alias.** Rejected in the PRD: it would hide the mistake rather than report
  it.
