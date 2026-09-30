# Authorization

Every Spark application has an authorization model. It lives in `App_Data/security.json`, it is
loaded by Spark core, and a missing or malformed file **refuses startup** rather than degrading
into a default. There is no code-level way to switch it off.

That is deliberate. The previous design made authorization an opt-in package, which meant every
application had a state — "the developer has not wired it up yet" — that had to be given a
meaning. Neither meaning was any good: deny-everything looks like a broken app, allow-everything
is a fail-open path nobody notices until it is in production.

```
dotnet run -- --spark-init-security
```

writes a starting file. It grants nothing, and it carries the whole grammar in comments.

---

## The shape of the file

```jsonc
{
  "wellKnown": {
    "anonymous":     "00000000-0000-0000-0000-000000000000",
    "authenticated": "a1b2c3d4-0000-0000-0000-00000000000f"
  },
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Anonymous visitors" },
    "a1b2c3d4-0000-0000-0000-00000000000f": { "en": "Signed-in users" },
    "a1b2c3d4-0000-0000-0000-000000000001": { "en": "Administrators" }
  },
  "rights": [
    { "id": "…", "resource": "QueryRead/Car", "groupId": "…000000f", "isDenied": false },
    { "id": "…", "resource": "EditNewDelete/Car", "groupId": "…0000001", "isDenied": false }
  ]
}
```

A **right** is `{action}/{target}`.

| Actions | |
|---|---|
| `Query` | list rows in a grid |
| `Read` | open one row's detail page |
| `New`, `Edit`, `Delete` | the obvious three |
| *any custom action name* | from `customActions.json`, e.g. `SyncColumns/GitHubProject` |

| Combined | expands to |
|---|---|
| `QueryRead` | Query, Read |
| `ReadEdit`, `EditNew`, `NewDelete` | the pairs |
| `ReadEditNew`, `EditNewDelete`, `QueryReadEdit` | the triples |
| `ReadEditNewDelete`, `QueryReadEditNew` | the quads |
| `QueryReadEditNewDelete` | all five |

Combined actions expand **symmetrically**: `deny EditNewDelete/Car` denies Edit, New *and*
Delete. (They used to expand on the grant side only, so a combined denial denied the literal
string and therefore nothing at all. The loader refused that shape rather than fixing it. Both
are gone.)

**There are no wildcards.** A resource containing `*` — `Read/*`, `*/Person`, `*/*`, grant or
denial — is refused at startup, and the build reports it first as **SPARK021** (an error). An
access review (GDPR, ISO 27001) has to be able to enumerate who can do what, and a wildcard also
covers types and actions that do not exist yet. Name every target, and use a combined action to
cover several actions at once: `QueryReadEditNewDelete/Person` instead of `*/Person`. A new
entity type is therefore denied to everyone until a right names it — accepted busywork. (Wildcards
existed until #460; the posture report's "floor rather than a ceiling" warning went with them.)

---

## Attribute-level rights: `{verb}/{Type}/{Attribute}`

A third segment scopes a right to one attribute: `Edit/Song/Lyrics`, `QueryRead/Employee/Salary`.

- **Verbs:** only `Query`, `Read`, `Edit`, `New` and the combined verbs made solely of them
  (`QueryRead`, `QueryReadEdit`, `QueryReadEditNew`, `ReadEdit`, `ReadEditNew`, `EditNew`), which
  expand as prefixes. `Delete`, `*Delete` combinations, `Replicate`, package verbs and custom actions
  stay type-level, and a third segment on them refuses the file.
- **Targets:** the type by its **name** (not an alias, a query or a reserved target such as
  `LookupReferences`), and an attribute that type's model declares. An AsDetail row's attributes
  target the row type (`Edit/Lyrics/Text`). Names are case-insensitive. An unknown type or
  attribute refuses startup (and a hot reload), and the build reports it first as **SPARK014** (an
  error).
- **The type right is required.** An attribute right composes over its type right and never
  unlocks it: `Edit/Song/Lyrics` without `Edit/Song` allows nothing. For an attribute that some
  attribute right mentions, the type right and the attribute right together are ranked with the
  four tiers of [Precedence](#precedence) across all groups; an attribute no right mentions inherits
  the type decision. `Read` ⇒ `Query` applies to attribute grants exactly as to type grants.
- **The contributor pattern:** grant `Edit/Song`, deny `Edit/Song/{each other attribute}`.
- **The stale-deny trap:** an attribute added later is mentioned by none of those denials and
  inherits the type grant. **SPARK024** (a warning) and a startup posture note
  (information) name it: *"Group 'X' restricts Edit on 2 of 3 attributes of 'Song'; 'Genre' is still
  editable through the type-level right — intended?"* Index-derived and projection columns are
  ordinary attributes and are denied the same way, one by one.
- System context (module sync, replication) is not restricted by attribute rights.

Evaluated once per request per (type, verb) by `IAttributeRights`. What a denied attribute does —
removed from the persistent object, the query columns and the searchable/sortable fields, and
shielded on save — lands with the enforcement step of #460 contributions (M2c-2).

---

## `Query` without `Read`: the pair worth knowing

The difference is visible in the UI:

| Granted | The grid | The first column |
|---|---|---|
| `QueryRead/Car` | lists rows | links to `/po/car/{id}` |
| `Query/Car` | lists rows | **plain text, no link** |
| `Read/Car` alone | lists rows — **`Read` implies `Query`** | links to `/po/car/{id}` |

**`Read` implies `Query`, one way.** A caller who may open every row individually may list them:
a grid discloses nothing that reading the rows one by one would not, and withholding the listing
would only break the pages that render *from* the type catalogue (`GET /spark/types`) — a
detail page whose metadata never arrives renders blank. So `Read/Car` and `QueryRead/Car` mean
the same thing, which is also how Vidyano's rights editor models it: it offers `Query`,
`QueryRead`, `QueryReadEdit` … as bundles and never a bare `Read`.

The implication is applied once, when the file is expanded into the rights index — evaluation
stays a set lookup — and it has two deliberate asymmetries:

- **The inverse does not hold.** `Query` without `Read` stays exactly what the rest of this
  section describes.
- **It widens grants only.** A *denied* `Read` leaves `Query` alone, so "list, but no
  click-through" remains expressible by denial and not only by omission.

`Query` without `Read` is how you publish a list whose rows have no detail page. The grid
(`spark-query-grid`, which backs both the routed page and the sub-query card) gates the row
anchor on `canRead`, which comes from `/spark/permissions/{type}`, which comes from this file.
Nothing else is involved and there is no per-query flag to set.

It is the right model, not a workaround, whenever a row cannot be loaded by id:

- a `Custom.*` query that fabricates rows in memory (DemoApp's `Stock` — 300 rows, no collection)
- an embedded value object reached through a query (`Address`, `ProjectColumn`)
- a projection whose ids belong to something else

`Read` also gates the row detail endpoint itself, so withholding it is enforcement and not just
presentation.

---

## Precedence

Four tiers, evaluated in order, each across **all** of the caller's groups before the next is
considered:

1. an **important denial** refuses, whatever else is granted
2. an **important grant** allows, over any ordinary denial
3. an ordinary **denial** refuses
4. an ordinary **grant** allows

Anything not matched is refused.

The whole-set-per-tier order is the point. **A denial is absolute unless an important right
overrides it** — it cannot be granted around by putting the caller in another group. So a denial
on `authenticated` locks out administrators too, and a mistaken one is not something a support
ticket can be fixed with by adding a group.

`isImportant` is a precedence tier, not an audit marker. Use it for the small set of rights where
being *sure* matters more than being composable: a break-glass administrative grant, or a hard
prohibition that must survive any future group. Two contradicting important rights resolve to the
denial, so the outcome never depends on file order.

---

## Groups

`wellKnown` names the group id playing each of two roles:

| Role | Who |
|---|---|
| `anonymous` | a caller who has **not** signed in |
| `authenticated` | every caller who has, whatever claims they carry |

**`anonymous` is not "everyone".** A right that both an anonymous visitor and a signed-in user
should have is **two grants**. That is verbose on purpose: the token it replaced (`Everyone`) was
added to every caller's group set, so a right granted to it was granted to the public internet,
and nothing at the point of writing said so.

Neither role is assertable. They are decided from authentication state, and their ids are
excluded from claim-derived membership — so no identity provider, and no custom
`IGroupMembershipProvider`, can hand a caller `authenticated` by naming a group.

Every **other** group is matched by **name** against the caller's group claims, in any
translation. Display names are therefore load-bearing: renaming a group in `security.json`
without renaming the claim silently drops the membership.

Replace where membership comes from with:

```csharp
spark.UseGroupMembershipProvider<MyProvider>();
```

The default reads `group` / `groups` / the two Microsoft role claim types / the SOAP group claim.

To **add** a source of membership instead of replacing the claims — groups earned by reputation,
a directory lookup on top of sign-in claims — compose one:

```csharp
spark.AddGroupMembershipProvider<EarnedPrivilegesProvider>();
```

Every composed provider's answer is merged with the primary one's (`Use…` keeps replacing only the
primary; the call order does not matter; adding the same type twice is a no-op). A provider may also
name groups by **id** — the keys of the `groups` block — by implementing `IGroupIdMembershipProvider`
next to `IGroupMembershipProvider`; that is the unambiguous way, since a display name may be any
string. Ids obey the same rules as names: a well-known id is dropped and an undeclared one grants
nothing. All providers are asked **once per request**; the merged answer is cached for the rest of it
and shared by `[SparkAuthorize(Group = …)]`, which matches a provider-returned id by the id itself or
by any translation of that group's name.

**The current user.** Code that needs *who* rather than *which groups* — audit stamping, moderation —
injects `ISparkCurrentUser` (`Id`, `IsAuthenticated`), scoped, which reads the principal's
`NameIdentifier` claim by default. It is an id, never a name; replace the registration to resolve it
differently.

---

## Never delete a type-level grant to lock something down

Type-level rights **gate row rules**. With no grant at all, `GetRowFilterAsync` never runs, and
signed-in callers are denied along with everyone else. To restrict a type, *move* the grant to a
narrower group — do not remove it. See [row security](guide-row-security.md).

---

## What a refusal looks like

Access endpoints (`/spark/po/*`, `/spark/actions/execute`, `/spark/lookupref/*`) answer:

- **401** to an anonymous caller, when the application has some way to sign in. The client
  interceptor turns this into the login redirect, and nothing else will.
- **404** otherwise — authenticated-but-denied, or an app with no login at all — with a body
  byte-identical to a genuine not-found.

A 403 would tell an unauthorized caller that the thing they asked for exists, which maps out the
data surface one probe at a time. So the status is a function of *the caller* and never of *the
resource's existence*: a load naming `Bogus` answers the same as one naming `Car`.

`/spark/lookupref/{name}` is allowed by `Read/LookupReferences`, **or** (since #453) when the caller
may Read an entity type with an attribute bound to that lookup — a persistent object the caller may
read stays renderable without handing anonymous visitors every lookup. A lookup that no readable type
binds still needs the grant. The ng-spark detail page shows an attribute's raw value if its lookup
fails to load, rather than failing the page.

[`[SparkAuthorize]`](guide-controllers.md#authorizing-against-securityjson) on your own endpoints is
decided by the same rights, `anonymous` grants included (since #453; before, ASP.NET Core's default
policy refused every anonymous caller first). A host that configures authorization without `AddSpark`
must call `SparkAuthorizeAttribute.AddPolicy`.

Catalogue endpoints (`/spark/types`, `/spark/queries`, `/spark/aliases`, `/spark/program-units`,
`/spark/actions/list`, `/spark/permissions/{type}`) are the exception. The client shell loads
them on boot for every visitor, so they answer **200 with everything filtered out** rather than
refusing — otherwise an anonymous visitor would be bounced to sign-in merely for opening a page.

---

## The two gates

**At startup**, every application prints which rights an anonymous caller holds — including when
that is nothing, because silence is indistinguishable from the check not running.

**In CI**, `--spark-verify-security` compares that list against a committed
`App_Data/securityPosture.txt` and exits 3 if it moved:

```bash
dotnet run -- --spark-verify-security     # the gate
dotnet run -- --spark-synchronize-security  # accept the change, then commit the file
```

`security.json` is a data file: widening it is a one-line diff that reads no differently from
narrowing it, and the consequence is invisible until someone reaches the endpoint. The baseline
is what makes it reviewable.

---

## ⚠️ Redacting an attribute is not enough: also set `canFilter: false`

`GetProtectedAttributesAsync` nulls an attribute's **value in the response**. It does not change what
the attribute is worth **filtering on**, and per-column filters (#431) compare the value stored in
the database.

So if `Salary` is redacted per-row but remains filterable, a caller sends
`{"name":"Salary","includes":[100000]}` and gets the row back with `"salary": null`. The row's
presence — and `totalItems` — confirms the value. That is an equality oracle on something the caller
may never read, and with a handful of requests it reads the number outright.

```jsonc
// on any attribute your row security may protect
{
  "name": "Salary",
  "canFilter": false,
  "canListDistincts": false   // stronger again: this one enumerates the values
}
```

**Both default to `true`, so this is an obligation rather than a default.**

It cannot be closed by the framework, and the reason is worth knowing because it applies to sorting
too. `GetProtectedAttributesAsync` takes an *entity* and may answer differently for every row, so it
cannot decide a *query-level* operation — by the time rows exist to ask about, the filtering and the
ordering have already happened. Filtering and sorting are therefore gated on the static signals
(`showedOn`, `canSort`, `canFilter`, `canListDistincts`), and matching them to a dynamic redaction
rule is the application's job.

A related consequence, in the other direction: a refused filter is **silent** — the rows come back
unnarrowed and nothing says so — because a distinguishable refusal would answer "does this column
exist?" for a caller who may not see it.

---

## In tests

```csharp
new SparkEndpointFactory<MyContext>(store, models,
    security: SparkTestSecurity.Permissive.Without("Secret"));
```

`SparkTestSecurity` gives you `Permissive` (the default — everything not explicitly denied; the
factory layers that baseline over the real evaluator, so denials still run through the production
path), `Empty`, `Granting`, `Denying`, `Without`,
`FromFile` and `FromJson`. The factory writes the file and then asserts the host loaded it, so a
silently-ignored override cannot make an authorization test vacuously green.

For the two things a grant list cannot express — recording what was asked, and deciding by
predicate — swap the service instead:

```csharp
configureServices: s => s.UseSparkTestAccessControl(SparkTestAccessControl.DenyAll())
```

---

## See also

- [Authentication schemes](guide-authentication-schemes.md) — which credential yields which principal
- [Row security](guide-row-security.md) — narrowing *within* a type the caller may reach
- [Custom actions](guide-custom-actions.md) — `{ActionName}/{Type}` rights
- [`[SparkAuthorize]`](guide-controllers.md) — the same rights on your own controllers
