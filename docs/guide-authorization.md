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

`*` is a wildcard on either half — `Read/*`, `*/Person`, `*/*`. Use it sparingly: a wildcard
covers types and actions that do not exist yet, and the startup posture report warns when the
anonymous group holds one.

---

## `Query` without `Read`: the pair worth knowing

These are independently grantable, and the difference is visible in the UI:

| Granted | The grid | The first column |
|---|---|---|
| `QueryRead/Car` | lists rows | links to `/po/car/{id}` |
| `Query/Car` | lists rows | **plain text, no link** |
| `Read/Car` alone | not listed at all | — |

`Query` without `Read` is how you publish a list whose rows have no detail page. Both grids
(`spark-query-list` and `spark-sub-query`) gate the anchor on `canRead`, which comes from
`/spark/permissions/{type}`, which comes from this file. Nothing else is involved and there is no
per-query flag to set.

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

---

## Never delete a type-level grant to lock something down

Type-level rights **gate row rules**. With no grant at all, `GetRowFilterAsync` never runs, and
signed-in callers are denied along with everyone else. To restrict a type, *move* the grant to a
narrower group — do not remove it. See [row security](guide-row-security.md).

---

## What a refusal looks like

Access endpoints (`/spark/po/*`, `/spark/actions/*/…`, `/spark/lookupref/*`) answer:

- **401** to an anonymous caller, when the application has some way to sign in. The client
  interceptor turns this into the login redirect, and nothing else will.
- **404** otherwise — authenticated-but-denied, or an app with no login at all — with a body
  byte-identical to a genuine not-found.

A 403 would tell an unauthorized caller that the thing they asked for exists, which maps out the
data surface one probe at a time. So the status is a function of *the caller* and never of *the
resource's existence*: `GET /spark/po/Bogus` answers the same as `GET /spark/po/Car`.

Catalogue endpoints (`/spark/types`, `/spark/queries`, `/spark/aliases`, `/spark/program-units`,
`/spark/actions/{type}`, `/spark/permissions/{type}`) are the exception. The client shell loads
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

## In tests

```csharp
new SparkEndpointFactory<MyContext>(store, models,
    security: SparkTestSecurity.Permissive.Without("Secret"));
```

`SparkTestSecurity` gives you `Permissive` (the default — a wildcard grant, so the baseline
exercises the same evaluation path production does), `Empty`, `Granting`, `Denying`, `Without`,
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
