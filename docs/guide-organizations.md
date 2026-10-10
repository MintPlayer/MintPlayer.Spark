# Organizations: members, roles and organization-owned rows

The organizations package (`MintPlayer.Spark.Organizations`, alias `organizations`) lets users work
together. A user belongs to organizations with a role, and any document that carries an `OrganizationId`
is reached only by the members of that organization with the role the action needs. It depends on
`MintPlayer.Spark.Authorization` only: no OpenID Connect, no claims, no identity-provider change.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X11, D18, D21–D23, D25, D26 and §12
"Spike S13". Package reference: [README](../libs/organizations/MintPlayer.Spark.Organizations/README.md).

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMailManager();          // invitation and join-request mails
    spark.AddOrganizations();        // options: Spark:Organizations, or AddOrganizations(o => …)
});
```

```json
// App_Data/security.json — startup is refused without the binding
"bindings": { "organizations:users": [ "Users" ] }
```

```ts
// the SPA: the page the mailed links open
sparkAuthRoutes(withExternalLogin(), withAccount(), withOrganizationRoutes())
```

Set `Spark:Auth:PublicBaseUrl`: the mailed links are built from it, as the account mails are. In
Development the request's own origin is used when it is unset; anywhere else nothing is mailed and a
warning is logged, because a link built from the `Host` header of an invite request would let its
sender point the invitee anywhere.

The package's model, rights and menu come with the reference (library layers); its row policy comes with
`AddOrganizations()`. Without the call the package's own types are closed (no row for anyone), and an
`IOrganizationOwned` type of yours that every signed-in user may list refuses startup for want of a row
rule. Call `AddOrganizations()`.

## Two layers of authorization, never a third

- **`security.json` says who may use organizations at all.** The package grants
  `QueryReadEditNewDelete/Organization`, `QueryReadEdit` + `Delete` on `OrganizationMembership`,
  `QueryRead` + `Delete` on `OrganizationInvitation` and its three custom actions to the slot
  `organizations:users`. A binding names groups by id or name, not by token: for "every signed-in
  user", name the group your `wellKnown.authenticated` points at.
- **Org roles are data.** `OrganizationRole` is `Viewer < Member < Admin < Owner`, stored on one
  `OrganizationMembership` document per (user, organization). The package's row policy turns them into
  a row filter. There is no other permission system.

| Role (D23) | Conventionally |
|---|---|
| Owner | everything, including deleting the organization and transferring ownership |
| Admin | manages members, invitations, join requests and the organization's content |
| Member | works with the content |
| Viewer | reads only |

## Organization-owned types

```csharp
[RequiresOrganizationRole("New", OrganizationRole.Admin)]       // only Admins add a site
[RequiresOrganizationRole("Delete", OrganizationRole.Admin)]
[RequiresOrganizationRole("StartAudit", OrganizationRole.Member)] // a custom action
public class Site : IOrganizationOwned
{
    public string? Id { get; set; }
    public string? OrganizationId { get; set; }
    public string Origin { get; set; } = "";
}
```

Implementing `IOrganizationOwned` is the whole rule. Defaults when no attribute names the action:
`Query`/`Read` Viewer, `New`/`Edit` and custom actions Member, `Delete` Admin. `Query` and `Read` follow
each other when only one is declared, `Restore`/`Revert` follow `Edit`, `Purge` follows `Delete`.

What the caller gets, on every path that asks row security (list, detail, sub-queries, distinct values,
reference pickers, breadcrumbs, custom-action selections, the edit/delete gates and `WITH CHECK`):

- a non-member, or a member below the role: the **missing-row 404** on a load and no row in any list (#453);
- a create or an edit must leave the row in an organization where the caller holds the `New` / `Edit`
  role (`WITH CHECK`), so nobody can create a row in, or move one to, someone else's organization;
- a row without an `OrganizationId` belongs to nobody and is visible to nobody;
- the filter ANDs with every other policy and with the type's own `GetRowFilterAsync`. With expiration
  (X1) the query reads
  `from 'Sites' where OrganizationId in ($p0) and (true and not ((ExpiresAtUtc <= $p1 and ExpiresAtUtc != $p2)))`.

⚠️ Raw session queries are not filtered. Background code acting for a user asks
`ISparkOrganizations.GetMembershipsAsync(userId)` and filters with `.In(ids)` itself (never `Contains`,
which RavenDB's provider cannot translate on an array).

### How the filter is built (S13)

Once per request, the caller's memberships are read **by id prefix**
(`OrganizationMemberships/{userId}/…`), straight from storage: no index, so an accepted invitation or a
removed member counts on the very next request. The predicate is `OrganizationId in (…)` with the ids of
the memberships whose role is high enough, passed as one query parameter. Measured on 10 000 documents
over 200 organizations: the membership read costs 0.3–2.8 ms for 1–100 organizations and a grid page
7–14 ms end to end. A claim cached in the cookie was rejected: it is stale until the next sign-in, so a
removed member would keep reading the organization's data. Details: PRD §12 "Spike S13".

## Personal organizations

Every user gets one on their first sign-in (`ISparkSignedInHandler<TUser>`, a hook in the Authorization
package that runs after every fresh sign-in): id `Organizations/personal/{userId}`, named after the user,
the user its only Owner. Its fixed id makes the creation idempotent and race-free; after the first time a
sign-in costs one load by id. Turn it off with `Spark:Organizations:CreatePersonalOrganizationOnSignIn =
false` and call `ISparkOrganizations.EnsurePersonalOrganizationAsync` yourself.

A personal organization takes no invitations and no join requests, cannot be left, transferred or
deleted, and goes with the account.

## The generic UI

The menu gains **Organizations** (alias `organizations`): the caller's organizations, through the row
policy. New creates one; its creator becomes the Owner in the same commit. An organization's page shows:

- **Members** (`organization-members`, every member sees it): Admins change roles, remove members;
  only an Owner gives or takes the Owner role, and the last Owner can be neither demoted nor removed.
  **Make Owner** (`TransferOrganizationOwnership`, one selected row, Owners) makes that member Owner and
  the caller Admin.
- **Invitations and join requests** (`organization-invitations`, Admins): pending links with their
  expiry; deleting one revokes it.
- **Invite** (`InviteOrganizationMember`, Admins): an address and a role (never Owner). The answer is
  "Invitation sent" whether or not the address has an account.
- **Leave** (`LeaveOrganization`, any member but the last Owner).
- **Delete** (Owners): the cascade below.

## Invitations and join requests (D18)

```csharp
await organizations.InviteAsync(organizationId, "bob@example.com", OrganizationRole.Member, inviterId);
await organizations.RequestToJoinAsync(organizationId, requesterId);   // e.g. a site already held by that organization
```

- The mail (`SparkOrganizations/Invitation`, `SparkOrganizations/JoinRequest`, en + nl, overridable by
  file name in `Templates/Mail`) carries `{PublicBaseUrl}/organizations/accept/{token}?id={invitationId}`.
  The token is 256 random bits; **only its SHA-256 is stored**. The mail is queued `Sensitive` and
  expires with the invitation.
- The page posts both values to `POST /spark/organizations/accept` on an explicit click: mail scanners
  open links, so a GET never acts. The caller must be signed in.
- **An invitation** makes the signed-in account a member with the invited role. The token is the
  credential (a user signed in with GitHub or X may have another address than the one invited).
- **A join request** is mailed to every Owner. It makes its requester a member, and needs an Owner or
  Admin to accept it; the requester cannot accept their own.
- **Single use**: accepting deletes the invitation, with optimistic concurrency, so of two concurrent
  accepts one fails. A second invitation to the same address replaces the first.
- **Expiry**: `OrganizationInvitation` is `IExpiring` with `expiresAfter` 14 days in the library's
  model. Change it in your own model layer. An expired one is hidden everywhere and refused on accept
  (X1: RavenDB deletes it at its next sweep, up to 36 h later).
- Every refusal (unknown, forged, used, expired, someone else's join request) answers the same words.

## Deleting and exporting (D26)

Register what your application keeps per organization:

```csharp
public sealed class SiteData(IDocumentStore store) : IOrganizationDataHandler
{
    public string Name => "sites";
    public Task<object?> ExportAsync(Organization organization, CancellationToken ct) => …;
    public Task DeleteAsync(Organization organization, CancellationToken ct) => …;   // idempotent
}
builder.Services.AddScoped<IOrganizationDataHandler, SiteData>();
```

- **Deleting an organization** (the Delete button, or `ISparkOrganizations.DeleteAsync`) runs every
  handler's `DeleteAsync` first, then deletes the memberships, the invitations and the organization in
  one commit. A handler that throws stops it with everything intact, so handlers must be idempotent.
- **Leaving** removes access only. The last Owner must make someone else Owner first.
- **Deleting an account** (`ISparkAccountDeletionHandler<TUser>`) deletes the personal organization and
  any organization the user alone was in (handlers included), and every other membership. If the user is
  the only Owner of an organization others are in, the deletion stops with the account intact until
  ownership moves.
- **The personal-data export** (`GET /spark/auth/manage/personal-data`) gains an `organizations`
  section: each membership with its role, and for every organization the user owns, each handler's
  `ExportAsync` under its `Name`.

## Options

| Key | Default | Meaning |
|---|---|---|
| `Spark:Organizations:AcceptPath` | `/organizations/accept` | The SPA page the mailed link opens; keep it in step with `withOrganizationRoutes({ accept })`. |
| `Spark:Organizations:CreatePersonalOrganizationOnSignIn` | `true` | Create the personal organization on sign-in. |

## Testing

`tests/MintPlayer.Spark.Tests/Organizations/` boots a host with Authorization, the package and a
recording `ISparkMailer`, and signs requests in with a header. `OrganizationsTestHost.LastLink(address)`
reads the token and invitation id back from the last mail's link.
