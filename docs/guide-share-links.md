# Share links: a read-only link to one object

A share link lets anyone who holds it read one object, without signing in: a public report, a status page for a
customer. It is a capability: a 256-bit token, of which only the SHA-256 is stored on the object. Making a new link
or stopping sharing ends the old one at once. Share links are part of core.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X6, D11, D31 and §12 "M6".

## Declaring what a link shows

```csharp
public class Site : IOrganizationOwned, ISparkShareable          // MintPlayer.Spark.Abstractions.Sharing
{
    public string? Id { get; set; }
    public string? OrganizationId { get; set; }
    public string Origin { get; set; } = "";
    public int? LatestScore { get; set; }
    public string? ShareTokenHash { get; set; }                     // never a model attribute
}

spark.AddShareLinks(o => o.Share<Site>(
    attributes: ["Origin", "LatestScore", "LatestGrade"],
    queries: new Dictionary<string, string[]> { ["site-runs"] = ["StartedAtUtc", "Score", "Grade"] }));
```

A child query is named by its **alias** (or id), as `IQueryLoader.ResolveQuery` resolves it, and the same key names it
on `/spark/share/query` and in the `queries` of `/spark/share/load`. A query without an alias goes by its name; one
with an alias (SeoTester's `"alias": "site-runs"`) must be declared as `["site-runs"]`, or the link shows no table.

Both lists are **allow-lists**: a link shows the named attributes of the object, and for each named query the named
columns, nothing else, whatever the type gains later. A query is run only when it is also one of the type's own
sub-queries (`"queries"` on the persistent object in the model), and always with the shared object as its parent,
so a link never lists anything but that object's children.

## Who may share

Core ships two custom actions, `ShareLink` and `RevokeShareLink` (detail page, no selection). They are granted like
any custom action, together with `Read` on the type:

```json
{ "resource": "ShareLink/Site", "groupId": "…" },
{ "resource": "RevokeShareLink/Site", "groupId": "…" }
```

and checked by the type's row rules for that action name. With organizations, D31's "Owners and Admins" is

```csharp
[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]
[RequiresOrganizationRole("RevokeShareLink", OrganizationRole.Admin)]
public class Site : IOrganizationOwned, ISparkShareable { … }
```

`ShareLink` makes a new token (replacing the old one) and shows the link once, in the secret dialog:
`{origin}/shared/{type}/{token}?id={id}`. The origin is `Spark:Share:PublicBaseUrl`, else `Spark:Auth:PublicBaseUrl`,
else the request's own (the link is shown only to its creator). Code can do the same through `ISparkShareLinks`
(`CreateAsync`, `RevokeAsync`, `LinkFor`). `IsValidAsync(type, id, token)` makes the share page's own check (declared
type, switch, gates, fixed-time compare) for an app's own anonymous surface on a link, such as SeoTester's SVG badge
(`/seotester/badge.svg?site=…&token=…`): it answers yes only for the object's current token while the link may be read.

`ShareTokenHash` is framework-protected like `ExpiresAtUtc`: a create starts without a link and an edit keeps the
stored value, whatever a client posts.

## Switching sharing off

`Spark:Share:Enabled = false` switches every link off; an `ISparkShareGate` decides at runtime, e.g. from a
[setting](guide-settings.md) (D11, D31):

```csharp
public sealed class SharingSwitch(ISparkSettings settings) : ISparkShareGate
{
    public async ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken ct)
        => await settings.GetAsync(SeoSettings.SharingEnabled, ct);
}
builder.Services.AddScoped<ISparkShareGate, SharingSwitch>();
```

Off means no new link is made (the action says so) and every existing link answers as unknown; switched back on,
the same links work again.

A gate can also decide **per object**: `IsEnabledForAsync(typeName, entity)` (yes by default) is asked with the stored
object after `IsEnabledAsync` said yes for its type, when `ShareLink` makes a link and on every read. SeoTester uses it
for "verified sites only" (D11):

```csharp
public async ValueTask<bool> IsEnabledForAsync(string typeName, object entity, CancellationToken ct)
    => await settings.GetAsync(SeoSettings.Sharing, ct) != SeoFeatureScope.VerifiedOnly || entity is Site { IsVerified: true };
```

A refused object's link reads like an unknown one, and works again once the gate says yes. Decide from the stored
object only (and other stored state), so making and reading a link agree.

## The page (ng-spark)

```ts
// app.routes.ts: top level, no guard (the token is the credential)
export const routes: Routes = [...withShareRoutes(), /* … */];
```

`withShareRoutes({ path?, component? })` (`@mintplayer/ng-spark/share`) lazy-loads `SparkSharedObjectComponent`
(`@mintplayer/ng-spark/share/page`) at `shared/:type/:token?id=…`: the attributes through `<spark-grid-cell>`, so
registered renderers (the [score renderers](guide-score-renderers.md) too) apply, under the type's label, and one table per child query
(headed by its query's label),
paged through `/spark/share/query`. A refused link shows `share.notFound`.

## Reading

The page calls two anonymous endpoints:

| Endpoint | Body | Answer |
|---|---|---|
| `POST /spark/share/load` | `{ type, id, token }` | `{ id, type, label, attributes, queries: { name: firstPage }, queryLabels: { name: label } }` |
| `POST /spark/share/query` | `{ type, id, token, query, skip, take, sortColumns? }` | one page of a declared child query |

Every refusal is the same **404** (#453): unknown type, id or token, a revoked or replaced link, sharing off, an
expired object. Reads are limited per address (`Spark:Share:RequestsPerMinute`, 60), on top of any application
rate limiter.

How the read is secured: the token is compared in fixed time; the normal read paths then run in a fresh scope under
a system principal whose permission service answers **only** `Read/{Type}` and `Query/{child entity}`. Row policies
that bypass the system context (organizations, an Actions class's `GetRowFilterAsync`) are skipped for that one
object and its children; the expiration filter (X1) is not, so an expired object is gone from its link too.

## Options (`Spark:Share`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | the global switch |
| `LinkPath` | `/shared` | the SPA page a link opens; keep it in step with `withShareRoutes()` |
| `PublicBaseUrl` | — | the links' origin (else `Spark:Auth:PublicBaseUrl`, else the request's) |
| `RequestsPerMinute` | `60` | anonymous reads per address |
| `MaxTake` | `100` | the largest child page |

## Testing

`tests/MintPlayer.Spark.Tests/Sharing/ShareLinkTests.cs` (organization-owned notes with organization-owned
comments; an Admin shares, a Member may not; the anonymous read shows only the declared attributes and columns).
