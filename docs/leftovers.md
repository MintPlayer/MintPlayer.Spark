# Leftovers

What the work in this repository knowingly did **not** close. One section per issue or initiative,
added when that work ships, so a deferral survives its pull request being merged instead of living
only in a PR description nobody reads again.

Two kinds of entry live here and they are not the same thing:

- **Unverified** — shipped, believed correct, but nothing (test or browser) exercises it.
- **Deliberately deferred** — a decision was made not to build it, with the reasoning that led there.

Deferred items are not a backlog to burn down. Each was rejected on evidence, and the evidence is
recorded so a future change can disagree with it knowingly rather than accidentally.

---

## Issue #260 — TriggersRefresh / OnRefreshAsync

Companion to [issue_260_PRD.md](issue_260_PRD.md) and [issue_260_plan.md](issue_260_plan.md).


### Unverified

#### The modal AsDetail path

`editMode: "modal"` renders a nested `spark-po-form` recursively rather than an inline grid. It
inherits the refresh coordinator by construction, and the per-instance coordinator test covers the
re-entrancy hazard that construction creates — but **no sample declares a trigger on a modal detail
type**, so the path has never been driven end to end.

To close it: declare `"triggersRefresh": true` on an attribute of a type rendered with
`editMode: "modal"`, and check that the overlay lands on the modal's own form rather than the
owner's.

#### Real-world hook idempotence

The contract says `OnRefreshAsync` must have no side effects, because save now runs it (once per
triggering attribute, via `BuildEffectiveAsync`). `AGENTS.md` says so loudly and both demo hooks
honour it, but nothing *enforces* it and nothing detects a violation. A hook that writes to the
database will do so on every save.

An analyzer could plausibly catch the obvious cases (a session write, an HTTP call) — see the
"could not be built" note below on why this is newly load-bearing rather than merely advisable.

---

### Deliberately deferred

Carried forward verbatim in substance from the PRD's out-of-scope table, so this file stands alone.

| Item | Why it was not done |
|---|---|
| **Per-row metadata inside a detail grid** | Confirmed in the browser: a nested refresh reshapes the *column*, so making `ContractEnd` read-only for one row makes it read-only for every row. Inherent to the inline grid rendering from one shared `EntityAttributeDefinition` per column, where Vidyano renders a `PersistentObject` per row. Values stay correctly per-row. Fixing it is a rewrite of the inline grid, not a refinement of this feature. |
| **Dynamic attribute add/remove inside the hook** | `AddAttribute` / `RetainAttributes` are `internal` (F22). Making them public is an API commitment that deserves its own evidence, and all five verbs the issue asked for are served by mutating existing attributes. |
| **Server-initiated cascade (`SetValueWithRefresh`)** | D6. Vidyano ships it with **no re-entrancy guard at all** (F15), and one idempotent pass covers the same outcomes. Addable later behind a depth cap without a wire change. |
| **Refreshing the *owner* from a detail row (`TriggerRefreshOnOwner`)** | A second addressing problem stacked on R20's. Worth doing only once nested triggers have real usage; nothing in the brief needs it. |
| **`QueueQueryRefresh` — hook asks a detail grid to re-search** | Adjacent and genuinely useful (F-4 note 17), but it is a *query* concern, and `refreshOnCompleted` already covers the custom-action case. Needs separate evidence. |
| **A `[TriggersRefresh]` C# attribute on the entity property** | Vidyano has no C# equivalent either (F3). The flag is presentation, and presentation lives in the model file in this codebase. Would additionally drag `SparkModelShape` hashing into scope. |
| **Client-side evaluation of rule types beyond those the server implements** | R19 deliberately covers only the rule types `ValidationService` already knows, so the client can never claim a rule the server won't enforce. A richer client rule engine is a feature in its own right. |
| **Bulk-edit interaction (F18)** | Spark has no bulk edit. Recorded only because the interaction is real if one is ever built. |

---

### Filed elsewhere, not forgotten

Fixed *inside* this PR rather than deferred, but listed because each is a standing hazard rather than
a one-off:

- **Fleet and HR were missing the `Read/LookupReferences` grant** since preview.44 — every lookup
  dropdown in both demos was empty for every user, admins included, and the refusal is deliberately
  shaped like a 404 so nothing surfaced it. The class of bug (a grant added to two demos out of four)
  has no gate.
- **Fixtures used shapes no real model produces** (`dataType: 'LookupReference'`), which let a broken
  `triggersImmediately` pass six tests. The fixtures are corrected; nothing prevents the next one.
- **Save runs `OnRefreshAsync`.** Newly load-bearing — see *Real-world hook idempotence* above.

---

## Found 2026-09-13 while surveying anonymous disclosure — NOT fixed, NOT in scope

Three findings turned up by a survey run for an unrelated question (how a client should detect that an
optional server module is absent). None is caused by the route-table work, none was in its scope, and
each is recorded here rather than fixed so that the decision to fix is taken deliberately.

**1. ⚠️ Enabling the identity provider applies wildcard CORS to the entire pipeline.**
`SparkIdentityProviderOptions.EnableDynamicCors` defaults to `true`
(`SparkIdentityProviderOptions.cs:38`). The policy is
`SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod()`
(`SparkIdentityProviderExtensions.cs:55-64`), applied by a bare `app.UseCors("SparkOidcCors")` at
`:74` — **not scoped to `/connect`**. So any page on any origin can read the anonymous view of
`/spark/types`, `/spark/translations`, `/spark/permissions/*` and `/spark/auth/capabilities`
cross-origin. There is no `AllowCredentials`, so it is the anonymous view only.

⚠️ **The inline comment says `// Validated at runtime below`, and no such validation exists.**
Verified: `AllowedCorsOrigins` occurs exactly twice in the repository — a doc comment
(`SparkIdentityProviderOptions.cs:36`) and an unread model property (`OidcApplication.cs:63`). The
per-application origin allow-list the comment promises was never implemented. A comment describing a
control that does not exist is worse than no comment: it answers the reviewer's question wrongly.

Two candidate fixes, and they are not exclusive: scope the policy to the `/connect` group, and/or
implement the per-application validation against `OidcApplication.AllowedCorsOrigins`.

**2. `GET /spark/culture` and `GET /spark/translations` inject no `IPermissionService`**
(`Culture/Get.cs:13-17`, `Translations/Get.cs:13-17`). An anonymous caller receives every translation
key and value in the application, including labels for entities and actions they can never reach.
⚠️ **Uncertain whether this is intentional** — a shell must render before anyone signs in, so *some*
anonymous translation access is required by design; whether it should be the whole catalogue is the
open question. `docs/issue_236_security_sweep_PRD.md:76` caught the analogous `lookupref` hole and is
silent on these two, which suggests they were not considered rather than cleared.

**3. `GET /spark/auth/external-login?provider=X` 302s to the upstream IdP**
(`SparkAuthenticationExtensions.cs:99-122`), disclosing the provider and its client id. Lowest of the
three: `/spark/auth/capabilities` volunteers the provider list to anonymous callers anyway, by design.
