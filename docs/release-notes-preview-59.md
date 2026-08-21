# 10.0.0-preview.59

Ships `MintPlayer.Spark.*` `10.0.0-preview.59` (21 packages) and `@mintplayer/ng-spark-auth@22.2.1`.

Closes [#294](https://github.com/MintPlayer/MintPlayer.Spark/issues/294),
[#295](https://github.com/MintPlayer/MintPlayer.Spark/issues/295),
[#296](https://github.com/MintPlayer/MintPlayer.Spark/issues/296),
[#304](https://github.com/MintPlayer/MintPlayer.Spark/issues/304).

---

## Behaviour changes

Read these before upgrading. Each one changes what an existing app does, and two of them will make
something that previously appeared to work start behaving differently — or stop.

### 1. Sort columns must name an attribute of the query surface (#295)

`?sortColumns=` previously resolved **any CLR property** on the entity by reflection. It now requires
an attribute that exists in the type's model **and** whose `showedOn` includes `Query`. Anything else
is refused, logged, and the rows keep their index order.

**Why this is a security fix.** Ordering is a comparison oracle. Redaction blanks a value in the
response but leaves `ORDER BY` intact, so an attribute a caller was never allowed to read could be
recovered one comparison at a time — sort ascending, sort descending, observe where the row lands,
bisect. Silently, and indistinguishably from ordinary paging.

**What breaks.** A grid sorting by an attribute you deliberately hid from the query surface stops
sorting. That is the intended outcome; the console names the refused column. If a column *should* be
sortable, widen its `showedOn` to include `Query`.

Sort companions are unaffected — the check runs on the declared name, before companion redirection.

### 2. `AddGitHub` now requests the `user:email` scope (#296)

Previously it requested **no scope at all**, while auto-provisioning refused to create an account
without an issuer-attested email — obtainable only from `/user/emails`, which needs exactly that
scope. The two were mutually unsatisfiable, so **first-time external sign-in could not succeed**.

In `SparkLocalCredentials.Disabled` there are no local accounts to fall back on, so an external-only
app was unsignable-into while its entire test suite stayed green.

**What changes for you.** For an **OAuth App**, the consent screen now asks for email access — expect
existing users to re-consent. For a **GitHub App**, scopes are ignored entirely; grant the
**"Email addresses: Read-only"** account permission instead, or provisioning still fails.

A failed `/user/emails` call is now logged at Warning naming the missing scope or permission. It was
previously swallowed, so nothing connected the generic "email not verified" to its cause.

### 3. Async custom queries are first-class (#294)

An `async` custom query previously lost declared `sortColumns`, header-click sorting, row-filter
pushdown, search pushdown, index projection and `.Include()` — silently, with nothing logged.

Capabilities are now inferred from the **runtime result** rather than the declared signature.

**What changes for you.**

- A declared `sortColumns` on an async query now **takes effect**. If you worked around this by
  ordering in memory, the two can disagree — remove one.
- A method declared `IQueryable<T>` but returning `session.Query<T>()` now gets the Raven path,
  including projection and search pushdown. This applies to **sync** methods too; it was a
  pre-existing gap.
- `Task<IRavenQueryable<T>>` previously **threw** (a blocking `ToList()` over an async session, which
  RavenDB rejects). It now works.
- `Task<IEnumerable<T>>` is unchanged and still non-queryable.

A method returning `ValueTask<...>` was reported as "not found"; it now reports the actual shape.
`ValueTask` remains unsupported — use `Task`.

---

## New

### A well-known `Authenticated` group (#304)

Group membership came purely from claims, so a signed-in user carrying no group claims resolved to
exactly the same set as an anonymous visitor: `{Everyone}`. The commonest authorization shape there
is — *any signed-in user may query this type, and a row rule narrows it to their own rows* — could
not be expressed at all.

`Authenticated` is the counterpart to `Everyone`, added only when the caller is authenticated:

```json
{ "resource": "QueryRead/Repository", "groupId": "<Authenticated group id>", "isDenied": false }
```

Opt-in by definition, exactly as `Everyone` is: an app whose `security.json` declares no group by
this name is unaffected.

**Migrating off an anonymous grant.** Move the grant to `Authenticated` — do not simply delete it.
Type-level rights gate row rules, so with no grant at all `GetRowFilterAsync` and `IsAllowedAsync`
never run and every caller is denied, signed-in ones included. A row rule narrows an admitted right;
it cannot grant one.

Authentication state is read where `Everyone` is already resolved rather than synthesized as a claim,
so an external identity provider cannot assert this group name for itself.

---

## Tests

`SparkSignInComponent` shipped in 22.2.0 with no coverage; it now has specs for every template branch
and the provider click path.

The first assertions anywhere on the generated GitHub authorization redirect — endpoint, `client_id`,
`redirect_uri`, `state`, `scope`. `CallbackPath` must match what is registered on the provider and
until now no test read it.

`Fleet.Recent_Cars` and `HR.Company_People` are now async, so the demos exercise the fixed behaviour
rather than leaving it covered only by unit fixtures.

---

## Still not verified

Carried forward from preview.58 and **not** closed by this release: a real GitHub OAuth round trip
against github.com, and a browser run of the WebhooksDemo `/sign-in` page. The automated substitutes
above cover the challenge shape and every component branch, but whether a deployed GitHub App's
callback URL and granted permissions are correct lives on github.com and no test can read it.
