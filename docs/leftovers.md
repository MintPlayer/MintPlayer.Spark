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

## Found 2026-09-13 while surveying anonymous disclosure

Three findings turned up by a survey run for an unrelated question (how a client should detect that an
optional server module is absent). None was caused by the route-table work. They were brought into
scope at the issue owner's direction; the outcomes differ, so each is recorded with its verdict.

### 1. ✅ FIXED — enabling the identity provider applied wildcard CORS to the entire pipeline

`EnableDynamicCors` defaulted to `true`, and the policy — `SetIsOriginAllowed(_ => true)` — was applied
by a bare `app.UseCors("SparkOidcCors")`, which is **pipeline-wide**. So merely turning the identity
provider on let any page on any origin read the anonymous view of `/spark/types`,
`/spark/translations`, `/spark/permissions/*` and `/spark/auth/capabilities`. No `AllowCredentials`, so
it was the anonymous view only — but a grant nobody asked for, to support a scenario nobody in this
repository has.

⚠️ **The inline comment said `// Validated at runtime below`. There was no such validation.**
`OidcApplication.AllowedCorsOrigins` occurs exactly twice — its declaration and a doc comment — and is
read by nothing. A comment describing a control that does not exist is worse than no comment: it
answers the reviewer's question wrongly.

**Fixed three ways**, and the default is the important one:
- `EnableDynamicCors` now defaults to **off**. Only a browser-based client on a *different* origin
  needs it; an application's own Angular frontend is served from the same host, so it never did.
- The policy is **endpoint-scoped** (`RequireCors` on the five endpoints a browser actually calls with
  `fetch`) rather than pipeline-wide, so the decision lives next to the route instead of in a path
  list that can drift away from one.
- The false comment is replaced by an accurate one.

**Then the model was generalised**, at the issue owner's direction, into the three tiers now documented
in [the CORS guide](guide-cors.md): Spark's own endpoints answer cross-origin by **default** under a
named `SparkCors` policy; a library's and an application's do not, and opt in. `UseSpark` registers the
CORS middleware unconditionally, the way it already did antiforgery, so `RequireCors` is safe for any
module to write.

⚠️ **A named policy, never `AddDefaultPolicy`.** A default policy is last-write-wins, so Spark
registering one would clobber an application's own — or be clobbered by it — depending on whether the
app called `AddCors` before or after `AddSpark`. An app's controllers would break cross-origin because
it added Spark, with the outcome depending on call order.

⚠️ **This reversed an earlier decision in the same PR.** An application's default policy used to reach
`/spark/*`, recorded as deliberate on the grounds that a default policy is the app saying "everywhere".
Spark's named policy now wins on its own prefix, and that is better for a reason the first framing
missed: the framework's endpoints have their own security model, and what they expose cross-origin
should not depend on a convenience default set for an application's controllers.

⚠️ **A test caught a defect in that fix, which is worth knowing before touching it again.**
`RequireCors` attaches metadata, and ASP.NET **throws on any request** to an endpoint carrying CORS
metadata when no CORS middleware is registered. Applied unconditionally, that would have made
`/connect/token` — the PKCE code exchange — a 500 in the new default configuration. `WithOidcCors`
applies the convention only when the option is on.

**Still open:** turning it on grants **any** origin. Narrowing to each application's registered origins
needs a cached lookup, because `SetIsOriginAllowed` is synchronous and would otherwise hit RavenDB on
every preflight. Worth doing as defence in depth — it stops a hostile page burning a victim's
authorization code — but it is a feature, not a fix, and it is not built.

### 2. ⚠️ OPEN, deliberately — the translation catalogue is anonymous and unfiltered

`GET /spark/translations` returns `translationsLoader.GetAll()` with no `IPermissionService` anywhere
in the endpoint (`Translations/Get.cs`); `GET /spark/culture` is the same shape. An anonymous caller
receives every translation key and value in the application.

**The sharp form of the problem is the inconsistency, not the endpoint.** `/spark/types` is
permission-filtered — a caller sees only the entity types they may see. `/spark/translations` is not,
and its values can name the very types `/spark/types` withheld.

**Not fixed, and not with a heuristic.** The obvious repair — drop keys that look like the names of
entities or actions the caller cannot reach — is exactly the kind of downstream inference this
repository has decided against (*fix the measurement, not the number*). Translation keys are arbitrary
strings; nothing maps them to resources. A real fix is a model change: split the catalogue into a
shell portion that must be anonymous (the app has to render a sign-in page) and an application portion
that must not be. That is a feature with a design, and it should be decided rather than improvised.

`docs/issue_236_security_sweep_PRD.md:76` caught the analogous `lookupref` hole and is silent on these
two, which suggests they were never considered rather than considered and cleared.

### 3. ✅ NOT A DEFECT — the external-login redirect

`GET /spark/auth/external-login?provider=X` 302s to the upstream provider, disclosing the provider and
its client id. Checked and dismissed on the evidence: `/spark/auth/capabilities` **already volunteers
the provider list to anonymous callers by design** (`GetAuthCapabilities.cs:43-48`), and an OAuth
client id is a public parameter, not a secret — it appears in every authorization URL a browser
follows. Nothing to fix. Recorded so the next survey does not re-raise it.
