# #310 M10 — derived `security.json` for DemoApp and WebhooksDemo

Fleet and HR already ship one. These two do not, and under #310 they must. The content below
was derived by tracing every screen, not guessed — copy it into
`Demo/{DemoApp,WebhooksDemo}/…/App_Data/security.json` when M10 runs.

Both files are valid under the **current** validator, so they can be committed before the core
move lands; they are simply inert until `AddAuthorization()`/`AllowAnonymousAccess()` go away.

---

## Validator rules these files must not trip

`SecurityConfigurationValidator`: resource must be `<action>/<target>` with a non-empty side
either way (`:47-54`); a **combined action in a denial** is rejected (`:61-67`, and M8 deletes
this rule); duplicate right `id` (`:69-75`, the nil GUID is exempt); a group named `Everyone`
when `wellKnown` is absent (`:94-115`); an unknown `wellKnown` key, a non-GUID value, or one
naming a group not in `groups` (`:130-148`); `anonymous` and `authenticated` on the same group
(`:150-156`).

**Not enforced, so get them right by hand:** a `groupId` matching no declared group is accepted
and silently never matches; a right naming a nonexistent type or action is accepted dead config;
and group **display names are load-bearing**, because a claim resolves a group by matching *any*
translation (`AccessControlService.cs:176-178`).

---

## DemoApp

Five types (`Person`, `Company`, `Car`, `Stock`, `Address`), no custom actions, no sign-in UI.
`authenticated` mirrors every `anonymous` grant: the app has no login today, but an unmirrored
file would 404 the entire app the moment one is added.

```json
{
  "wellKnown": {
    "anonymous": "00000000-0000-0000-0000-000000000000",
    "authenticated": "d1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Anonymous visitors", "fr": "Visiteurs anonymes", "nl": "Anonieme bezoekers" },
    "d1b2c3d4-0000-0000-0000-00000000000f": { "en": "Signed-in users", "fr": "Utilisateurs connectés", "nl": "Aangemelde gebruikers" }
  },
  "rights": [
    { "id": "d0000001-0000-0000-0000-000000000001", "resource": "QueryReadEditNewDelete/Person",  "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },
    { "id": "d0000001-0000-0000-0000-000000000002", "resource": "QueryReadEditNewDelete/Company", "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },
    { "id": "d0000001-0000-0000-0000-000000000003", "resource": "QueryReadEditNewDelete/Car",     "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },
    { "id": "d0000001-0000-0000-0000-000000000004", "resource": "Query/Stock",                    "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },
    { "id": "d0000001-0000-0000-0000-000000000005", "resource": "Query/Address",                  "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },
    { "id": "d0000001-0000-0000-0000-000000000006", "resource": "ReadEdit/LookupReferences",      "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },

    { "id": "d000000f-0000-0000-0000-000000000001", "resource": "QueryReadEditNewDelete/Person",  "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "d000000f-0000-0000-0000-000000000002", "resource": "QueryReadEditNewDelete/Company", "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "d000000f-0000-0000-0000-000000000003", "resource": "QueryReadEditNewDelete/Car",     "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "d000000f-0000-0000-0000-000000000004", "resource": "Query/Stock",                    "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "d000000f-0000-0000-0000-000000000005", "resource": "Query/Address",                  "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "d000000f-0000-0000-0000-000000000006", "resource": "ReadEdit/LookupReferences",      "groupId": "d1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false }
  ]
}
```

| Resource | Why |
|---|---|
| `QueryReadEditNewDelete/Person` | People menu unit, grid, detail page, CRUD buttons; also the `Company_People` sub-query, which resolves to `Query/Person` via `query.EntityType` |
| `QueryReadEditNewDelete/Company` | Companies menu + CRUD — **and the `Person.Company` / `Car.Owner` reference pickers**, which run `executeQueryByName('GetCompanies')` and need Company present in `allEntityTypes` for the link route |
| `QueryReadEditNewDelete/Car` | Cars menu + CRUD |
| **`Query/Stock`**, no `Read` | **The showcase.** Grid lists, no row anchor. Correct rather than merely illustrative: `StockActions` fabricates 300 rows in memory and nothing writes the `Stocks` collection, so `Read` would load nothing. Withholding New/Edit/Delete correctly hides those buttons on a non-persisted type |
| **`Query/Address`**, no `Read` | Non-obvious. `Person.Address` is a scalar AsDetail; the client resolves the child definition out of the `Query`-filtered `getEntityTypes()`. Without it the address renders blank. `Read` is pointless — `Address` is embedded |
| `ReadEdit/LookupReferences` | Literal target. `Read` for the `Car.Brand`/`Car.Status` dropdowns. `Edit` matches `CarBrand : DynamicLookupReference` (Modal), whose mutation endpoints demand it — ⚠️ **no shipped ng-spark component calls those methods today**, so the `Edit` half is intent, not exercised. Drop to `Read/` if the file should describe only what runs |

No `New`/`Delete` on `Address`: child permissions are fetched only for `isArray: true` AsDetail
attributes, and `Address` is scalar.

---

## WebhooksDemo

Three types, one custom action (`SyncColumns`, `showedOn: "detail"`), GitHub OAuth sign-in with
local credentials disabled. `anonymous` is declared and granted **nothing** — the honest
statement of an app whose UI hides everything until you sign in.

**Webhook ingestion needs no grant.** The recipients inject `IAsyncDocumentSession` and query
Raven directly; nothing under `libs/webhooks` touches `IPermissionService`. The `/api/github/*`
controller is gated by MVC `[Authorize]` and an org-access service, not by rights.

```json
{
  "wellKnown": {
    "anonymous": "00000000-0000-0000-0000-000000000000",
    "authenticated": "e1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Anonymous visitors", "fr": "Visiteurs anonymes", "nl": "Anonieme bezoekers" },
    "e1b2c3d4-0000-0000-0000-00000000000f": { "en": "Signed-in users", "fr": "Utilisateurs connectés", "nl": "Aangemelde gebruikers" }
  },
  "rights": [
    { "id": "e000000f-0000-0000-0000-000000000001", "resource": "QueryReadEditNewDelete/GitHubProject", "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "e000000f-0000-0000-0000-000000000002", "resource": "SyncColumns/GitHubProject",            "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "e000000f-0000-0000-0000-000000000003", "resource": "Query/ProjectColumn",                  "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "e000000f-0000-0000-0000-000000000004", "resource": "Query/EventColumnMapping",             "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "e000000f-0000-0000-0000-000000000005", "resource": "NewDelete/EventColumnMapping",         "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false },
    { "id": "e000000f-0000-0000-0000-000000000006", "resource": "Read/LookupReferences",                "groupId": "e1b2c3d4-0000-0000-0000-00000000000f", "isDenied": false }
  ]
}
```

| Resource | Why |
|---|---|
| `QueryReadEditNewDelete/GitHubProject` | The `/github-projects` page lists, creates and deletes; rows link to the PO detail, which saves. Row-level org filtering rides on top via `GetRowFilterAsync` |
| `SyncColumns/GitHubProject` | Custom-action resource is `{ActionName}/{simple CLR type}`. Granted on `GitHubProject` only — `customActions.json` is a flat map evaluated against every type, so granting it on `ProjectColumn` too would render a stray button |
| **`Query/ProjectColumn`**, no `Read` | Second showcase, and forced by the model: an embedded value object reached through `Custom.GetProjectColumns`, with **no `ProjectColumns` collection registered**, so a detail load could never resolve. Also needed for the `GitHubProject.Columns` AsDetail child definition |
| **`Query/EventColumnMapping`**, no `Read`/`Edit` | Same shape — embedded, no collection |
| `NewDelete/EventColumnMapping` | Non-obvious. `EventMappings` is an editable array AsDetail; the add/remove-row buttons read `canCreate`/`canDelete` on the **child** type. Without these the grid is frozen. `Edit` is deliberately absent: field edits inside child rows ride the parent's save, which authorizes on the parent |
| `Read/LookupReferences` | Non-obvious. `EventColumnMapping.WebhookEvent` is a lookup, and the form fetches `/spark/lookupref/{name}` **even for a transient lookup**. Without it the event dropdown is empty. No `Edit` — the lookup is transient |

---

## Fleet is load-bearing; HR is not

⚠️ **Do not edit Fleet's `security.json` casually.** The E2E suite runs the real Fleet app out
of process against the committed file, and asserts on details invisible from reading it:

- the literal display names `Administrators`, `Fleet managers`, `Machine:FleetApi`, `Module:HR`
- **two absences** — no `Car` grant to `anonymous`, and no `Edit/LookupReferences` grant to
  anyone

Both absences are asserted, so *adding* a right can break tests as easily as removing one.

M9's acceptance test needs a sub-query fixture and Fleet declares none: add
`"queries": ["cars"]` to `persistentObject` in Fleet's `Company.json`. Anonymous has
`QueryRead/Company` but nothing on `Car`, which is exactly the matrix required.
`persistentObject.queries` is **not** hashed by `ModelFileShape`, so this does not invalidate
`modelHashes.json` — verify that rather than assume it.

Nothing in the E2E suite reads DemoApp's or WebhooksDemo's `App_Data`, so the two new files are
free of test coupling.
