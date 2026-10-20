# Organizations: members, roles and organization-owned rows

The organizations package (`MintPlayer.Spark.Organizations`, alias `organizations`) lets users work
together. A user belongs to organizations with a role, and any document that carries an `OrganizationId`
is reached only by the members of that organization with the role the action needs. It depends on
`MintPlayer.Spark.Authorization` only: no OpenID Connect, no claims, no identity-provider change.

Decisions and evidence: [seo_analyzer_PRD.md](seo_analyzer_PRD.md) X11, D18, D21–D23, D25, D26, D42, D43 and §12
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

### Reading across organizations (`AllOrganizations/{Type}`)

Some callers work on every organization's rows without belonging to them: a warehouse's staff handle every
owner's goods (warehouse WM3; lead decision 2026-10-20). `security.json` grants that per type, as a right:

```json
{ "resource": "AllOrganizations/StockBalance", "groupId": "warehouse:staff" }
```

- It lifts the organization filter for **`Query` and `Read`** of that type only. Writes (`New`, `Edit`, `Delete`,
  custom actions) keep the membership rule.
- It is explicit and per type: no role implies it, and there is no global bypass. No grant, no change.
- The switcher still narrows a list when an organization is chosen; every other policy (expiration, soft
  deletion) still applies, and the rows are read through the same audited read paths as any other read.
- A library grants it to a slot it declares (the warehouse grants it to `warehouse:staff` on its stock, item,
  lot and task types); the application decides who is in the slot.

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

## Published rows: readable by everyone (commerce X8)

A product on sale or an event's public page belongs to the seller's organization, yet every visitor must
see it. Implement `IOrganizationPublishable` instead of `IOrganizationOwned`:

```csharp
[RequiresOrganizationRole("Publish", OrganizationRole.Admin)]   // optional: Publish follows Edit by default
public class Product : IOrganizationPublishable
{
    public string? Id { get; set; }
    public string? OrganizationId { get; set; }
    public bool IsPublished { get; set; }
    public string Name { get; set; } = "";
}
```

- **`Query` and `Read` match published rows for everyone**, anonymous visitors included. The predicate
  becomes `OrganizationId in (…) || IsPublished == true`, and just `IsPublished == true` for a non-member
  or an anonymous caller. Every other action (`Edit`, `Delete`, custom actions) stays with the members.
- **It never lifts another rule.** Row policies compose with AND ([row security](guide-row-security.md));
  this is the one OR, and it sits *inside* the organization policy's own predicate. The type's
  `GetRowFilterAsync`, expiration, soft deletion and moderation still AND with it: a published row that
  expired, was deleted or is hidden stays hidden.
- **Type rights come first.** Anonymous visitors reach a published row only when `security.json` gives
  their group `QueryRead/Product`. The anonymous-readable startup check is satisfied by the organization
  policy, which is a visibility decision.
- **Publishing is its own right.** Flipping `IsPublished`, creating a row already published, or moving a
  published row to another organization needs the `Publish` role in the row's organization (default: the
  `Edit` role). A member without it gets a 400 on `IsPublished`. The system context is not limited.
- **The switcher still narrows.** With an organization chosen, a member's list shows that organization's
  rows only. Under "All organizations" it also shows other organizations' published rows. Loads are never
  narrowed.
- ⚠️ `IsPublished` is rebound by member name and must be a `bool`. A document without the field does not
  match `IsPublished == true`, so it stays unpublished (the safe direction). A projection that does not
  store `IsPublished` falls back to filtering after materialization.

## Custom roles (commerce X8)

Beside the ordered built-in role, a member can hold **custom roles** that the host or a library declares:
door staff who only scan tickets, a finance team that refunds.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddOrganizations();
    spark.AddOrganizationRole("scanner", "Scans tickets at the door");
    spark.AddOrganizationRole("finance", "Refunds and payouts");
});
```

Configuration works too: `Spark:Organizations:CustomRoles:scanner = "Scans tickets at the door"`. A name
is a letter, then letters, digits, `-` or `_` (at most 64), never `Viewer`/`Member`/`Admin`/`Owner`, and
matches case-insensitively.

Name it in rights next to, or instead of, a built-in role:

```csharp
[RequiresOrganizationRole("Scan", "scanner")]                 // holders of scanner, and Owners
[RequiresOrganizationRole("Refund", OrganizationRole.Admin)]  // Admins...
[RequiresOrganizationRole("Refund", "finance")]               // ...or holders of finance
public class Ticket : IOrganizationOwned { … }
```

- The declarations of one action are **alternatives**: the built-in role or higher, or any named custom
  role. An action that names only custom roles is open to their holders alone. An **Owner passes every
  action**.
- Custom roles are **not ordered**: holding `finance` says nothing about `scanner`.
- **Who gives them:** Admins, on the member's form (**Extra roles**) or with
  `ISparkOrganizations.SetCustomRolesAsync(organizationId, userId, roles, actingUserId)`. An undeclared name
  is refused with a 400; stored names take the declared spelling. Read them back with
  `GetCustomRolesAsync`. `GET /spark/organizations/current` lists them per organization (`customRoles`), so
  a client can show the scanner page only to scanners. The personal-data export includes them.
- Like the built-in role, they are read per request by id prefix, so a role given or taken counts on the
  next request.

## Personal organizations

Every user gets one on their first sign-in (`ISparkSignedInHandler<TUser>`, a hook in the Authorization
package that runs after every fresh sign-in): id `Organizations/personal/{userId}`, named after the user,
the user its only Owner. Its fixed id makes the creation idempotent and race-free; after the first time a
sign-in costs one load by id. Turn it off with `Spark:Organizations:CreatePersonalOrganizationOnSignIn =
false` and call `ISparkOrganizations.EnsurePersonalOrganizationAsync` yourself.

It is the user's default organization: an application puts what a user makes on their own there unless
they pick another (`Organization.PersonalIdFor(userId)` names it without a lookup).

A personal organization takes members like any other (PRD D42): invitations and join requests work, so a
second user who verifies a site held there asks to join and its user gets the usual acceptance-link mail.
What stays personal is the Owner role: its user is its only Owner for good. Ownership cannot be
transferred, nobody else is made Owner, its user cannot step down, be removed or leave, and the
organization cannot be deleted on its own. It goes with its user's account, members and all. Its other
members are edited, removed and leave as in any organization, and deleting one of their accounts leaves
it alone.

## The generic UI

The menu gains **Organizations** (alias `organizations`): the caller's organizations, through the row
policy. New creates one; its creator becomes the Owner in the same commit. An organization's page shows:

- **Members** (`organization-members`, every member sees it): Admins change roles, remove members;
  only an Owner gives or takes the Owner role, and the last Owner can be neither demoted nor removed.
  **Make Owner** (`TransferOrganizationOwnership`, one selected row, Owners) makes that member Owner and
  the caller Admin.
- **Invitations** (`organization-invitations`, Admins): pending links with their expiry; deleting one
  revokes it. Join requests are approval requests (below): each Owner and Admin finds them in their
  approvals inbox.
- **Invite** (`InviteOrganizationMember`, Admins): an address and a role (never Owner). The answer is
  "Invitation sent" whether or not the address has an account.
- **Leave** (`LeaveOrganization`, any member but the last Owner).
- **Delete** (Owners): the cascade below.

## The organization switcher (PRD D50)

A user in several organizations picks the one they work in, in the top bar:

```ts
import { SparkOrganizationSwitcherComponent } from '@mintplayer/ng-spark/organizations';
```

```html
@if (auth.isAuthenticated()) { <spark-organization-switcher /> }
```

That is the whole setup: `AddOrganizations()` already maps the endpoints and applies the choice. The
switcher offers **All organizations** (the default) and each of the caller's organizations (the personal
one marked), and renders nothing for a user in one organization.

- **Where the choice lives:** a cookie, `spark-organization` (per browser, like the theme; HttpOnly,
  SameSite=Lax, a year), set by `POST /spark/organizations/current` (`{ organizationId }`, `null` for all;
  antiforgery like every POST) and read by `GET /spark/organizations/current` (the choice and the caller's
  organizations). Nothing is stored server-side.
- **It is never trusted:** every request checks it against the caller's memberships, which the row policy
  reads once per request anyway. An organization the caller is not (or no longer) a member of counts as
  no choice, so it can only narrow what the row policy allows. Posting one answers the missing-row 404.
- **Losing membership:** the lists fall back to all of the caller's organizations on the very next request,
  and `GET /current` deletes the stale cookie, so the switcher shows *All organizations*.
- **What follows it:** lists of `IOrganizationOwned` rows (the row policy narrows the `Query` action:
  grids, query cards, reference pickers, distinct values, live lists). **What does not:** a detail page or
  an edit (a link to another organization's row still opens), custom actions, a sub-query (a site opened
  from elsewhere still shows its own audits: core marks parent-scoped queries with
  `RowPolicyContext.IsSubQuery`), and the organization list itself.
- **New rows:** a create through the generic pipeline whose form leaves `OrganizationId` empty lands in the
  chosen organization when the caller holds the type's `New` role there, else in the caller's personal
  one. An application interceptor that reads `OrganizationId` on create and is registered before
  `AddOrganizations()` states `InterceptorStage.Finalize` to see that default.
- **Code:** `ISparkOrganizations.GetCurrentOrganizationIdAsync()` answers the request's valid choice (null =
  all).
- After a choice the component reloads the page (`[reloadOnChange]="false"` to opt out), so every grid and
  live list reads the new scope.

## Invitations (D18) and join requests (D26)

```csharp
await organizations.InviteAsync(organizationId, "bob@example.com", OrganizationRole.Member, inviterId);
await organizations.RequestToJoinAsync(organizationId, requesterId);   // e.g. a site already held by that organization
```

- The invitation mail (`SparkOrganizations/Invitation`, en + nl, overridable by file name in
  `Templates/Mail`) carries `{PublicBaseUrl}/organizations/accept/{token}?id={invitationId}`.
  The token is 256 random bits; **only its SHA-256 is stored**. The mail is queued `Sensitive` and
  expires with the invitation.
- The page posts both values to `POST /spark/organizations/accept` on an explicit click: mail scanners
  open links, so a GET never acts. The caller must be signed in.
- **The sender's address must be confirmed** (PRD D43). An invitation is mailed on the inviter's behalf,
  so `InviteAsync` refuses, with a `SparkValidationException` and nothing stored or sent, when the
  inviting account has no confirmed email address. The *Invite* action says so (translated) before it
  asks for an address. A join request shows the requester's address to the deciders, so
  `RequestToJoinAsync` applies the same rule to the requester.
- **An invitation** makes the signed-in account a member with the invited role, **only when that
  account's address is the invited one** (PRD D43), compared the way ASP.NET Identity normalizes it. The
  invitee's address need not be confirmed: they may not even have had an account when invited. Any other
  account gets `reason: "otherAddress"`, and the accept page says the invitation was sent to another
  address and to sign in with the account that uses it. That refusal does not spend the link, so the
  right account can still use it.
- **A join request** is an approval request of kind `organizations.join` (`OrganizationJoinApproval`,
  [Approvals](guide-approvals.md), commerce PRD D26), not a mailed link. `AddOrganizations()` adds
  Approvals itself.
  - **Who decides:** every Owner and Admin of the organization is told (Approvals' notice, or its mail
    `SparkApprovals/Submitted` without Notifications) and sees it in their approvals inbox. Any of them
    approves or declines it on the request page, `/po/approval-request/{id}`. Their role is read from
    storage at decision time, so a demoted Admin can no longer decide.
  - **Approve** makes the requester a member with the role asked for (never Owner). The request goes stale
    instead when the organization or the requester is gone, or the requester is already a member.
  - **Decline** needs a reason, which is kept on the request. Either way the requester is told the decision.
  - **Refusals:** the requester cannot decide their own request, and a Member or outsider gets the
    uniform 404. A new request from the same user replaces their pending one, and the deciders have
    14 days.
  - **Deletion:** deleting the organization closes its pending join requests. Deleting the requester's
    account is handled by Approvals.
  - **Old links:** a join-request link mailed before D26 still opens: the migration
    `M_202610191700_OrganizationJoinRequestsToApprovals` turned each pending join request into an approval
    request and kept its token's hash. The accept page posts the link as before, and a matching token
    answers `approvalRequestUrl`, the request's page, where the page goes. Nothing else is revealed: any
    other token gets the one refusal.
- **Single use**: accepting deletes the invitation, with optimistic concurrency, so of two concurrent
  accepts one fails. A second invitation to the same address replaces the first.
- **Expiry**: `OrganizationInvitation` is `IExpiring` with `expiresAfter` 14 days in the library's
  model. Change it in your own model layer. An expired one is hidden everywhere and refused on accept
  (X1: RavenDB deletes it at its next sweep, up to 36 h later).
- Every refusal (unknown, forged, used, expired) answers the same words,
  `reason: "invalid"`. The one exception is the wrong account for a valid invitation (above): it needs
  the link's token, so it tells its holder nothing they did not know.

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
- **Deleting an account** (`ISparkAccountDeletionHandler<TUser>`) deletes the user's own personal
  organization, with its other members' access (D42; someone else's personal organization they joined is
  left like any other), and
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
| `Spark:Organizations:CustomRoles:{name}` | none | Declares a custom role (value: its description); same as `spark.AddOrganizationRole(name, description)`. |

## Testing

`tests/MintPlayer.Spark.Tests/Organizations/` boots a host with Authorization, the package and a
recording `ISparkMailer`, and signs requests in with a header. `OrganizationsTestHost.LastLink(address)`
reads the token and invitation id back from the last mail's link.
