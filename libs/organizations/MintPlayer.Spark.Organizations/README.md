# MintPlayer.Spark.Organizations

Organizations for MintPlayer.Spark (`docs/seo_analyzer_PRD.md` X11): users work together in
organizations, and documents that carry an `OrganizationId` are visible only to the members of that
organization, with the role each action needs. Depends on `MintPlayer.Spark.Authorization` only.

- **Roles** `Owner > Admin > Member > Viewer`, ordered. What each may do is declared per type with
  `[RequiresOrganizationRole(action, role)]`; defaults: Viewer reads, Member creates and edits, Admin deletes.
- **Every user has a personal organization**, created on first sign-in: their default, owned by them for
  good. Others join it like any other organization (D42); its Owner role never changes hands.
- **Invitations by email** (Admin and up) and **join requests** raised by application code
  (`ISparkOrganizations.RequestToJoinAsync`), both mailed with a single-use acceptance link that expires
  after 14 days (`expiresAfter` on `OrganizationInvitation`). Only the token's SHA-256 is stored. The
  mail goes out on the sender's behalf, so the inviter (or the join requester) needs a confirmed email
  address (D43). An invitation is accepted only by the account whose address is the invited one,
  confirmed or not; a join request is approved by an Owner or Admin.
- **The last Owner cannot leave**; ownership is transferred to a member.
- **Deleting an organization cascades**: every `IOrganizationDataHandler` deletes the application's data
  first, then memberships and invitations go. **Deleting an account** deletes the personal organization and
  every membership. The account's personal-data export gains an `organizations` section.

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddMailManager();                 // the invitation mails; without it they are discarded with a warning
    spark.AddOrganizations();
});
```

`App_Data/security.json` of the application (startup is refused without the binding):

```json
"bindings": { "organizations:users": [ "Users" ] }
```

Set `Spark:Auth:PublicBaseUrl` (the mailed links use it; outside Development nothing is mailed without it).

## Organization-owned types

```csharp
[RequiresOrganizationRole("New", OrganizationRole.Admin)]
[RequiresOrganizationRole("Delete", OrganizationRole.Admin)]
public class Site : IOrganizationOwned
{
    public string? Id { get; set; }
    public string? OrganizationId { get; set; }
    public string Origin { get; set; } = "";
}
```

That is the whole row rule. A non-member gets the missing-row 404 on a load and never sees the row in a
list, a sub-query, a reference picker or a custom-action selection.

## Cascade and export

```csharp
public sealed class SiteData(IDocumentStore store) : IOrganizationDataHandler
{
    public string Name => "sites";
    public async Task<object?> ExportAsync(Organization organization, CancellationToken ct) { … }
    public async Task DeleteAsync(Organization organization, CancellationToken ct) { … }   // idempotent
}

builder.Services.AddScoped<IOrganizationDataHandler, SiteData>();
```

## The acceptance page

The link opens `{PublicBaseUrl}/organizations/accept/{token}?id={invitationId}`. ng-spark ships the page:
add `withOrganizationRoutes()` to `sparkAuthRoutes(...)`. It posts the two values to
`POST /spark/organizations/accept` on an explicit click. Change the path with `Spark:Organizations:AcceptPath`.

Guide: [docs/guide-organizations.md](../../../docs/guide-organizations.md).
