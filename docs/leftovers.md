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
