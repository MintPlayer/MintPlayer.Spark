# Issue #374 — verify that a program unit's alias resolves

**Status:** PRD, not implemented
**Issue:** [#374](https://github.com/MintPlayer/MintPlayer.Spark/issues/374)

## The problem

A program unit identifies its target twice, and only one of the two is checked.

```json
{
  "type": "query",
  "queryId": "237b1f50-88b8-48e5-a946-98a2f5251772",
  "alias": "github-projects"
}
```

The `queryId` resolves, so nothing complains. But the client routes to `/query/{unit alias}` and then
fetches `/spark/queries/{unit alias}` — the **unit's alias is used as the query identifier**. When the
target query resolves to a different alias, that fetch 404s and the page renders empty.

### Why the 404 is the expensive part

`Endpoints/Queries/Get.cs` and `Execute.cs` answer 404 for an *unauthorized* query deliberately, to
close an existence oracle: a denied caller must not be able to distinguish a real query id from a
fake one. That is correct, and it is exactly what makes this failure costly — **a misspelled alias
and a missing right produce byte-identical responses.**

Observed cost, 2026-09-07: `apps/CodeCoverage`'s new Project boards page rendered empty after
deploy. The candidate causes were an alias mismatch, a missing `Query/GitHubProject` right, and
row security filtering everything out. All three present identically from the client.

### Why it is easy to hit

A query's alias is **derived** when not declared — `SparkQueryAliases.Derive` strips a `Get` prefix
and lowercases, so `GetGitHubProjects` → `githubprojects`. A hyphenated unit alias never matches
unless the query declares one explicitly.

Every unit in the workspace agrees today, but by two different routes, which is why the invariant
looks maintained when it is merely uniformly satisfied:

| Unit alias | Where the query's alias comes from |
|---|---|
| `people`, `cars`, `companies`, `stocks`, `professions` | Derived — the unit happens to use the derived form |
| `stolen-cars`, `recent-cars`, `github-projects` | Declared explicitly on the query |

## It is not only query units

Investigated while writing this: `persistentObject` units have the **same** shape.
`ModelLoader` assigns `entityType.Alias ??= entityType.Name.ToLowerInvariant()`, and a PO unit routes
to `/po/{unit alias}/{objectId}`, resolved by `IModelLoader.ResolveEntityType(idOrAlias)`. So a PO
unit whose alias does not resolve to an entity type fails the same way, for the same reason, with the
same indistinguishable 404.

The issue describes only query units. Both are in scope here: it is one check over one file, and
fixing half of it would leave the identical trap armed next door.

## Requirements

- **FR1** — `--spark-verify-model` fails when a `type: "query"` unit's `alias` does not resolve to a
  query, or resolves to a **different** query than its `queryId`.
- **FR2** — the same for a `type: "persistentObject"` unit's `alias` against `persistentObjectId`.
- **FR3** — the message names **both sides** and the fix. A reader must not have to work out which
  of the two identifiers is wrong, or that aliases are derived at all.
- **FR4** — a unit with no `alias` is **valid** and unchecked: the client then routes by id.
- **FR5** — a missing or malformed `programUnits.json` is not this check's problem. Absent is legal
  (an app may ship no menu); malformed is the loader's error to report, and reporting it twice
  differently helps nobody.
- **FR6** — `type: "url"` units are unchecked. They have no server-side target.

## Design

A sibling of the checks already in `SparkDevelopmentExtensions`, called from the same sequence:

```
VerifyQueryAliasesAreUnique(contentRoot);
VerifyRefreshTriggersAreImplemented(contentRoot);
VerifyComposedQueriesAreUsable(contentRoot);
VerifyCustomQueryMethodsExist(contextType, contentRoot);
VerifySubQueriesCanBeParentScoped(contentRoot);
VerifyProgramUnitTargetsResolve(contentRoot);   // new
```

That placement is deliberate and follows the precedent set by #327 M3: these run **independently of
the model hash**, because a change that both drifts the hash and breaks an alias should report both
at once rather than hiding the second until the first is fixed.

Reads the files from disk rather than resolving services, exactly as its siblings do — there is no
service provider in the builder phase, and building one would need a document store. The alias
**rules** stay shared (`SparkQueryAliases.Derive`, and `Name.ToLowerInvariant()` for entity types),
so the set of models CI accepts remains provably the set the runtime resolves.

### Why build-time rather than startup

A startup gate would also catch it, but this is static configuration knowable without a request, and
the model commands return before `builder.Build()` — so `UseSpark`'s gate never runs in CI at all.
That is the same reasoning `VerifyQueryAliasesAreUnique` records, and the same reason DemoApp once
shipped an alias collision.

## Out of scope

- **Changing the 404.** It closes an existence oracle and is correct. This PRD makes the
  *configuration* verifiable instead of making the runtime chattier.
- **Deriving unit aliases from the target.** Tempting, and wrong: an app may legitimately want a URL
  that differs from a query's own alias, and silently rewriting one identifier to match the other
  would hide exactly the mistake being reported.
- **Warning when a unit declares no alias.** FR4 — that is a supported shape, not a smell.

## Risks

- **R1 — an existing app fails its next CI run.** That is the check working; the alternative is a
  page that 404s in production. Mitigated by the message naming the fix. Verified against all five
  apps in the workspace before landing.
- **R2 — reading `programUnits.json` twice** (here and in `ProgramUnitsLoader`) could drift. Low: the
  shape is a DTO in Abstractions and both deserialize the same type.
