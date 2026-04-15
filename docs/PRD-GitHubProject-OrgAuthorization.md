# PRD: Organization-Level Authorization for GitHubProject Entities

**Status:** Draft
**Date:** 2026-04-14
**Scope:** `Demo/WebhooksDemo/` + targeted change in `MintPlayer.Spark.Authorization`

---

## 1. Problem Statement

Any authenticated user in WebhooksDemo can read, create, modify, and delete **any** `GitHubProject` entity and its `EventMappings`, regardless of whether they are a member or administrator of the GitHub organization that owns the project. The current authorization model only supports **entity-type-level** permissions (e.g., "can this user access GitHubProject entities at all?") but has **no row-level filtering** (e.g., "can this user access *this specific* GitHubProject?").

### Attack Scenario

1. User A creates a `GitHubProject` for `org-alpha` (they are an admin of `org-alpha`).
2. User B authenticates via GitHub OAuth (they are **not** a member of `org-alpha`).
3. User B calls `GET /spark/GitHubProject` and sees all projects, including `org-alpha`'s.
4. User B calls `PUT /spark/GitHubProject/{id}` and modifies `org-alpha`'s event mappings.
5. User B calls `POST /api/github/projects/{id}/sync-columns` and syncs the project.
6. User B calls `DELETE /spark/GitHubProject/{id}` and deletes the project entirely.

No authorization check prevents any of these operations.

---

## 2. Root Cause Analysis

There are **four independent gaps** that combine to create this vulnerability:

### Gap 1: GitHub organization memberships are never extracted during login

**File:** `MintPlayer.Spark.Authorization/Extensions/GitHubAuthenticationExtensions.cs:34-37`

The OAuth setup requests the `read:org` scope but only maps three claims:
```csharp
options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
```
No call is made to the GitHub Organizations API (`GET /user/orgs`) and no organization claims are stored. Without this data, no downstream layer can distinguish which orgs a user belongs to.

### Gap 2: Spark's authorization is entity-type-level only, not row-level

**File:** `MintPlayer.Spark/Services/PermissionService.cs:16`

The permission check constructs a resource string like `"Read/GitHubProject"` — it checks whether the user can access the *entity type*, not whether they can access a *specific document*. The `IAccessControl` / `AccessControlService` pipeline processes `security.json` for type-level grants and denials. It has no concept of document-level filtering, nor should it — row-level security is inherently entity-specific and belongs in the per-entity Actions class.

### Gap 3: Actions classes have no authorization overrides

**File:** `Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs`

`DefaultPersistentObjectActions<T>` provides virtual hooks that run during every CRUD operation:

| Hook | Called by | Purpose |
|------|-----------|---------|
| `OnQueryAsync` | `DatabaseAccess.GetPersistentObjectsAsync` | Filter list results |
| `OnLoadAsync` | `DatabaseAccess.GetPersistentObjectAsync` | Gate single-entity access |
| `OnBeforeSaveAsync` | `DatabaseAccess.SavePersistentObjectAsync` | Validate before create/edit |
| `OnBeforeDeleteAsync` | `DatabaseAccess.DeletePersistentObjectAsync` | Validate before delete |

`GitHubProjectActions` only overrides `OnBeforeSaveAsync` to auto-fetch columns. None of the hooks validate that the current user belongs to the project's organization.

### Gap 4: Custom controller endpoints have no org checks

**File:** `Demo/WebhooksDemo/WebhooksDemo/Controllers/GitHubProjectsController.cs`

- `ListProjects()` (line 29): Lists all projects across all GitHub App installations — no filtering by the current user's org memberships.
- `GetColumns()` (line 94): Fetches columns for any project by node ID — no ownership check.
- `SyncColumns()` (line 104): Loads and modifies any project by document ID — no ownership check.

---

## 3. Architectural Decision: Actions Overrides vs. Generic RLS Service

**Decision: Use `DefaultPersistentObjectActions<T>` overrides, backed by a shared `IOrganizationAccessService`.**

Row-level security is inherently entity-specific — you can't generalize "which rows can this user see" without knowing the entity's data model (which property is the owner? what does "membership" mean?). A generic `IRowLevelAccessControl<T>` framework service would:

- Require the framework to discover and resolve a second per-entity-type service alongside the existing Actions class
- Add indirection for something the Actions hooks already handle naturally
- Still need entity-specific implementations that know about `OwnerLogin`, org claims, etc.

The Actions hooks are the idiomatic Spark extension point. They already run at exactly the right moment in the CRUD pipeline. The authorization logic stays clean because the actual claim-reading is extracted into `IOrganizationAccessService` — each Actions override is a single-line `_orgAccess.IsOwnerAllowed(...)` call, clearly separated from business logic.

---

## 4. Goals

1. **A user may only see, create, modify, and delete `GitHubProject` entities belonging to GitHub organizations they are a member of** (or their own user account projects).
2. **The `ListProjects` endpoint must only return projects from organizations the user belongs to**, so they cannot discover projects they shouldn't see.
3. **Webhook event handlers are unaffected** — they run as system operations (no user context) and must continue to process all projects.
4. **The solution is implementable within the WebhooksDemo app**, leveraging existing Spark framework hooks, with one targeted addition to `MintPlayer.Spark.Authorization` (org claim extraction).

---

## 5. Non-Goals

- Adding a generic row-level security framework to Spark core. The existing Actions hooks provide the needed extension points.
- Protecting other entity types in WebhooksDemo (currently only `GitHubProject` and its nested children need protection).
- Fine-grained role differentiation within an org (e.g., "admin can edit, member can only read"). All org members get full CRUD access to that org's projects. This can be layered on later.
- Protecting the webhook ingestion path — webhooks are signed with `WebhookSecret` and arrive server-to-server without user context.

---

## 6. Design

### 6.1 Overview

The fix spans three layers:

```
[Login] ──→ Fetch GitHub org memberships, store as claims
               │
[Actions] ──→ GitHubProjectActions overrides filter/block by org
               │        (uses IOrganizationAccessService to read claims)
               │
[Controller] ──→ GitHubProjectsController filters by user's orgs
                         (uses IOrganizationAccessService to read claims)
```

### 6.2 Phase 1: Extract GitHub Organization Memberships at Login

**What changes:**

In `GitHubAuthenticationExtensions.cs`, extend the `OnCreatingTicket` handler to also fetch the user's GitHub organizations and add them as claims.

**Implementation:**

After the existing user-info fetch, add a second request when `read:org` is in the requested scopes:

```
GET https://api.github.com/user/orgs
Authorization: Bearer {access_token}
```

This returns an array of organizations. For each org, add a claim:

- **Claim type:** `"urn:github:org"` (custom claim type, defined as `SparkGitHubClaimTypes.Organization`)
- **Claim value:** The org's `login` field (e.g., `"MintPlayer"`)

**Where:**
- `MintPlayer.Spark.Authorization/Extensions/GitHubAuthenticationExtensions.cs` — modify `OnCreatingTicket`
- `MintPlayer.Spark.Authorization/Extensions/SparkGitHubClaimTypes.cs` — **new file**, defines the claim type constant

**Edge case — user added to/removed from org:** Organization membership changes on GitHub are not reflected until the user logs out and back in. This is acceptable for a first iteration. A future enhancement could refresh org claims periodically using the stored OAuth access token.

### 6.3 Phase 2: Shared `IOrganizationAccessService`

To avoid duplicating claim-reading logic across Actions classes and the controller, extract it into a scoped service in WebhooksDemo.

```csharp
// Demo/WebhooksDemo/WebhooksDemo/Services/IOrganizationAccessService.cs

public interface IOrganizationAccessService
{
    /// <summary>
    /// Returns the GitHub owner logins (org logins + personal username)
    /// the current authenticated user is allowed to access.
    /// Returns empty if not authenticated.
    /// </summary>
    string[] GetAllowedOwners();

    /// <summary>
    /// Returns true if the current user is allowed to access entities
    /// owned by the given owner login.
    /// </summary>
    bool IsOwnerAllowed(string ownerLogin);
}
```

```csharp
// Demo/WebhooksDemo/WebhooksDemo/Services/OrganizationAccessService.cs

[Register(typeof(IOrganizationAccessService), ServiceLifetime.Scoped)]
public partial class OrganizationAccessService : IOrganizationAccessService
{
    [Inject] private readonly IHttpContextAccessor _httpContextAccessor;

    private string[]? _cachedOwners;

    public string[] GetAllowedOwners()
    {
        if (_cachedOwners is not null) return _cachedOwners;

        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return _cachedOwners = [];

        var orgs = user.FindAll(SparkGitHubClaimTypes.Organization).Select(c => c.Value);
        var username = user.FindFirstValue(ClaimTypes.Name);

        _cachedOwners = orgs
            .Concat(username is not null ? [username] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return _cachedOwners;
    }

    public bool IsOwnerAllowed(string ownerLogin)
    {
        return GetAllowedOwners()
            .Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    }
}
```

The service is scoped (one instance per request), caches the result for the duration of the request, and is the single source of truth for "which owners can this user access". All downstream consumers inject this instead of reading claims directly.

### 6.4 Phase 3: Row-Level Filtering in GitHubProjectActions

This is the core security layer. Override the virtual methods in `DefaultPersistentObjectActions<GitHubProject>` to enforce organization membership.

**What changes in `GitHubProjectActions.cs`:**

#### 3a. OnQueryAsync — filter list results

Override `OnQueryAsync` to only return projects whose `OwnerLogin` matches one of the current user's GitHub organizations (or their own username).

```csharp
public override async Task<IEnumerable<GitHubProject>> OnQueryAsync(IAsyncDocumentSession session)
{
    var owners = _orgAccess.GetAllowedOwners();
    if (owners.Length == 0) return [];

    return await session.Query<GitHubProject>()
        .Where(p => p.OwnerLogin.In(owners))
        .ToListAsync();
}
```

This applies the filter at the RavenDB query level — unauthorized documents are never loaded.

#### 3b. OnLoadAsync — block access to individual documents

Override `OnLoadAsync` to load the document and then verify the user has access:

```csharp
public override async Task<GitHubProject?> OnLoadAsync(IAsyncDocumentSession session, string id)
{
    var project = await session.LoadAsync<GitHubProject>(id);
    if (project is null) return null;

    if (!_orgAccess.IsOwnerAllowed(project.OwnerLogin))
        throw new SparkAccessDeniedException("Read/GitHubProject");

    return project;
}
```

This prevents direct access via `GET /spark/GitHubProject/{id}`.

#### 3c. OnBeforeSaveAsync — block unauthorized creates/edits

Extend the existing `OnBeforeSaveAsync` to validate before the existing column-fetch logic:

```csharp
public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
{
    if (!_orgAccess.IsOwnerAllowed(entity.OwnerLogin))
        throw new SparkAccessDeniedException("Edit/GitHubProject");

    // Existing column-fetch logic (unchanged)
    if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
    {
        var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(
            entity.InstallationId, entity.NodeId);
        entity.StatusFieldId = statusFieldId;
        entity.Columns = columns;
    }

    await base.OnBeforeSaveAsync(obj, entity);
}
```

#### 3d. OnBeforeDeleteAsync — block unauthorized deletes

```csharp
public override Task OnBeforeDeleteAsync(GitHubProject entity)
{
    if (!_orgAccess.IsOwnerAllowed(entity.OwnerLogin))
        throw new SparkAccessDeniedException("Delete/GitHubProject");

    return Task.CompletedTask;
}
```

**New dependency added to the class:** `[Inject] private readonly IOrganizationAccessService _orgAccess;`

### 6.5 Phase 4: Secure ProjectColumnActions Custom Query

**File:** `Demo/WebhooksDemo/WebhooksDemo/Actions/ProjectColumnActions.cs`

The `GetProjectColumns` custom query loads columns from a parent `GitHubProject`. It must verify the user has access to the parent project before returning data.

```csharp
public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    public async Task<IEnumerable<ProjectColumn>> GetProjectColumns(CustomQueryArgs args)
    {
        if (args.Parent is null) return [];

        var project = await args.Session.LoadAsync<GitHubProject>(args.Parent.Id);
        if (project is null) return [];

        if (!_orgAccess.IsOwnerAllowed(project.OwnerLogin))
            return [];

        return project.Columns;
    }
}
```

### 6.6 Phase 5: Secure GitHubProjectsController

**File:** `Demo/WebhooksDemo/WebhooksDemo/Controllers/GitHubProjectsController.cs`

**New dependency:** `[Inject] private readonly IOrganizationAccessService _orgAccess;`

#### 5a. ListProjects — filter by user's organizations

The `ListProjects` method currently iterates all GitHub App installations and returns all projects. Filter to only show installations for organizations the user belongs to:

```csharp
[HttpGet]
public async Task<IActionResult> ListProjects()
{
    var allowedOwners = _orgAccess.GetAllowedOwners();
    if (allowedOwners.Length == 0)
        return Ok(Array.Empty<ProjectInfo>());

    // ... existing: create app client, list installations ...

    foreach (var installation in installations)
    {
        var ownerLogin = installation.Account.Login;

        // Skip installations for organizations the user is not a member of
        if (!_orgAccess.IsOwnerAllowed(ownerLogin))
            continue;

        // ... rest of existing logic unchanged ...
    }

    return Ok(results);
}
```

#### 5b. GetColumns — verify org access

Look up the installation to find the owner, then check:

```csharp
[HttpGet("{nodeId}/columns")]
public async Task<IActionResult> GetColumns(string nodeId, [FromQuery] long installationId)
{
    var appClient = await _installationService.CreateAppClientAsync();
    var installation = await appClient.GitHubApps.GetInstallation(installationId);
    if (!_orgAccess.IsOwnerAllowed(installation.Account.Login))
        return Forbid();

    var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(installationId, nodeId);
    return Ok(new { statusFieldId, columns });
}
```

#### 5c. SyncColumns — verify org access via the loaded project

```csharp
[HttpPost("{documentId}/sync-columns")]
public async Task<IActionResult> SyncColumns(string documentId)
{
    var project = await _session.LoadAsync<GitHubProject>(documentId);
    if (project == null)
        return NotFound();

    if (!_orgAccess.IsOwnerAllowed(project.OwnerLogin))
        return Forbid();

    var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(
        project.InstallationId, project.NodeId);
    project.StatusFieldId = statusFieldId;
    project.Columns = columns;
    await _session.SaveChangesAsync();

    return Ok(new { statusFieldId, columns });
}
```

### 6.7 Webhook Handlers (No Change Required)

**Files:**
- `Demo/WebhooksDemo/WebhooksDemo/Recipients/HandleIssuesEvent.cs`
- `Demo/WebhooksDemo/WebhooksDemo/Recipients/HandlePullRequestEvent.cs`

Webhook handlers run in a server-to-server context (triggered by GitHub's webhook POST, not by an authenticated user). They inject `IAsyncDocumentSession` directly and query all `GitHubProject` documents — this is correct behavior. They must see all projects to process incoming events for any installation.

The `OnQueryAsync` override in `GitHubProjectActions` only applies when the Spark generic CRUD endpoints call `DatabaseAccess.GetPersistentObjectsAsync`, which delegates to the Actions class. The webhook handlers use `_session.Query<GitHubProject>()` directly, bypassing the Actions layer entirely. **No change needed.**

---

## 7. Files to Change

| # | File | Change |
|---|------|--------|
| 1 | `MintPlayer.Spark.Authorization/Extensions/GitHubAuthenticationExtensions.cs` | Extend `OnCreatingTicket` to fetch `/user/orgs` and add `urn:github:org` claims |
| 2 | `MintPlayer.Spark.Authorization/Extensions/SparkGitHubClaimTypes.cs` | **New file** — `SparkGitHubClaimTypes.Organization` constant |
| 3 | `Demo/WebhooksDemo/WebhooksDemo/Services/IOrganizationAccessService.cs` | **New file** — interface |
| 4 | `Demo/WebhooksDemo/WebhooksDemo/Services/OrganizationAccessService.cs` | **New file** — scoped implementation reading org claims |
| 5 | `Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs` | Override `OnQueryAsync`, `OnLoadAsync`, extend `OnBeforeSaveAsync`, add `OnBeforeDeleteAsync` |
| 6 | `Demo/WebhooksDemo/WebhooksDemo/Actions/ProjectColumnActions.cs` | Add org validation in `GetProjectColumns` |
| 7 | `Demo/WebhooksDemo/WebhooksDemo/Controllers/GitHubProjectsController.cs` | Inject `IOrganizationAccessService`; filter `ListProjects`, add checks in `GetColumns` and `SyncColumns` |

**No changes to:** `Program.cs`, `DatabaseAccess.cs`, `PermissionService.cs`, `AccessControlService.cs`, `security.json`, webhook handlers.

---

## 8. Detailed File Changes

### 8.1 GitHubAuthenticationExtensions.cs

**Current** (lines 41-53):
```csharp
options.Events.OnCreatingTicket = async context =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

    using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
    response.EnsureSuccessStatusCode();

    using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    context.RunClaimActions(user.RootElement);
};
```

**After** — append org-fetching after `RunClaimActions`:
```csharp
options.Events.OnCreatingTicket = async context =>
{
    // Existing: fetch user info
    using var userRequest = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
    userRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
    userRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

    using var userResponse = await context.Backchannel.SendAsync(userRequest, context.HttpContext.RequestAborted);
    userResponse.EnsureSuccessStatusCode();

    using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
    context.RunClaimActions(user.RootElement);

    // New: fetch GitHub organization memberships (requires read:org scope)
    if (context.Options.Scope.Contains("read:org"))
    {
        using var orgRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs");
        orgRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        orgRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        orgRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SparkAuth", "1.0"));

        using var orgResponse = await context.Backchannel.SendAsync(orgRequest, context.HttpContext.RequestAborted);
        if (orgResponse.IsSuccessStatusCode)
        {
            using var orgs = JsonDocument.Parse(await orgResponse.Content.ReadAsStringAsync());
            foreach (var org in orgs.RootElement.EnumerateArray())
            {
                var login = org.GetProperty("login").GetString();
                if (!string.IsNullOrEmpty(login))
                {
                    context.Identity?.AddClaim(new Claim(
                        SparkGitHubClaimTypes.Organization, login));
                }
            }
        }
    }
};
```

### 8.2 SparkGitHubClaimTypes.cs (new)

```csharp
// MintPlayer.Spark.Authorization/Extensions/SparkGitHubClaimTypes.cs
namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkGitHubClaimTypes
{
    /// <summary>
    /// Claim type for GitHub organization membership.
    /// Each org the user belongs to is stored as a separate claim with this type.
    /// Value is the organization's login (e.g., "MintPlayer").
    /// Populated automatically during GitHub OAuth when the read:org scope is present.
    /// </summary>
    public const string Organization = "urn:github:org";
}
```

### 8.3 GitHubProjectActions.cs

**Current** (lines 1-27): Only overrides `OnBeforeSaveAsync`.

**After**: Four overrides + injected `IOrganizationAccessService`:

```csharp
public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    public override async Task<IEnumerable<GitHubProject>> OnQueryAsync(IAsyncDocumentSession session)
    {
        var owners = _orgAccess.GetAllowedOwners();
        if (owners.Length == 0) return [];

        return await session.Query<GitHubProject>()
            .Where(p => p.OwnerLogin.In(owners))
            .ToListAsync();
    }

    public override async Task<GitHubProject?> OnLoadAsync(IAsyncDocumentSession session, string id)
    {
        var project = await session.LoadAsync<GitHubProject>(id);
        if (project is null) return null;

        if (!_orgAccess.IsOwnerAllowed(project.OwnerLogin))
            throw new SparkAccessDeniedException("Read/GitHubProject");

        return project;
    }

    public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        if (!_orgAccess.IsOwnerAllowed(entity.OwnerLogin))
            throw new SparkAccessDeniedException("Edit/GitHubProject");

        // Existing column-fetch logic
        if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
        {
            var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(
                entity.InstallationId, entity.NodeId);
            entity.StatusFieldId = statusFieldId;
            entity.Columns = columns;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }

    public override Task OnBeforeDeleteAsync(GitHubProject entity)
    {
        if (!_orgAccess.IsOwnerAllowed(entity.OwnerLogin))
            throw new SparkAccessDeniedException("Delete/GitHubProject");

        return Task.CompletedTask;
    }
}
```

### 8.4 ProjectColumnActions.cs

**Current** (lines 1-17): No org validation.

**After**:
```csharp
public partial class ProjectColumnActions : DefaultPersistentObjectActions<ProjectColumn>
{
    [Inject] private readonly IOrganizationAccessService _orgAccess;

    public async Task<IEnumerable<ProjectColumn>> GetProjectColumns(CustomQueryArgs args)
    {
        if (args.Parent is null) return [];

        var project = await args.Session.LoadAsync<GitHubProject>(args.Parent.Id);
        if (project is null) return [];

        if (!_orgAccess.IsOwnerAllowed(project.OwnerLogin))
            return [];

        return project.Columns;
    }
}
```

### 8.5 GitHubProjectsController.cs

**Key changes:**
- Add `[Inject] private readonly IOrganizationAccessService _orgAccess;`
- `ListProjects`: Add `if (!_orgAccess.IsOwnerAllowed(ownerLogin)) continue;` inside the installations loop
- `GetColumns`: Look up installation owner and check `_orgAccess.IsOwnerAllowed`, return `Forbid()` if not
- `SyncColumns`: After loading the project, check `_orgAccess.IsOwnerAllowed(project.OwnerLogin)`, return `Forbid()` if not

---

## 9. Security Considerations

### 9.1 Defense in Depth

The design applies authorization at **two independent layers**:

1. **Actions layer** (Phase 3): Row-level filtering in `OnQueryAsync`/`OnLoadAsync`/`OnBeforeSaveAsync`/`OnBeforeDeleteAsync` — protects all Spark generic CRUD endpoints and custom queries.
2. **Controller layer** (Phase 5): Explicit checks in custom endpoints — protects non-Spark routes.

Both layers read from the same data source (org claims via `IOrganizationAccessService`), but enforce independently. Bypassing the Spark endpoints doesn't bypass the controller checks, and vice versa.

### 9.2 Stale Org Membership

GitHub org membership changes are not pushed to the app. The claims are captured at login time. A user removed from an org retains access until their session expires or they re-authenticate.

**Mitigation options (future enhancement):**
- Add a GitHub webhook handler for `organization.member_removed` events to invalidate sessions.
- Periodically refresh org claims using the stored OAuth access token (stored via `SaveTokens = true`).
- Set shorter session/cookie lifetimes.

### 9.3 OwnerLogin Trustworthiness

`GitHubProject.OwnerLogin` is set by the frontend when a user toggles a project on. It comes from the GitHub App installation data (via `ListProjects`), which is authoritative. After Phase 5, `ListProjects` only returns projects from orgs the user belongs to, so the `OwnerLogin` value in the create payload is constrained to valid values.

An attacker could attempt to `PUT` a project with a forged `OwnerLogin`. The `OnBeforeSaveAsync` check prevents this: if the user isn't a member of the target org, the save is rejected. Additionally, the existing entity's `OwnerLogin` should not be updatable via the generic endpoint — consider making it read-only after creation.

### 9.4 Webhook Handlers

`HandleIssuesEvent` and `HandlePullRequestEvent` query `_session.Query<GitHubProject>()` directly, bypassing the Actions layer. This is intentional — webhooks are system operations that must process all projects. The webhook endpoint itself is protected by signature validation (`WebhookSecret`).

---

## 10. Implementation Order

| Phase | Description | Depends On | Risk |
|-------|-------------|------------|------|
| 1 | Org claim extraction in `GitHubAuthenticationExtensions` | — | Low (additive change) |
| 2 | `IOrganizationAccessService` shared service | Phase 1 (reads the claims) | Low |
| 3 | `GitHubProjectActions` overrides | Phase 2 | Medium (core security logic) |
| 4 | `ProjectColumnActions` org check | Phase 2 | Low |
| 5 | `GitHubProjectsController` filtering | Phase 2 | Low |
| 6 | Testing & verification | All | — |

Phases 3, 4, 5 can be done in parallel once Phase 2 is complete.

---

## 11. Testing Plan

### 11.1 Manual Testing

1. **Login as user in org-alpha** → Should see only `org-alpha` projects in `GET /api/github/projects`.
2. **`GET /spark/GitHubProject`** → Should return only projects with `OwnerLogin` matching user's orgs.
3. **`GET /spark/GitHubProject/{id}` for an org-beta project** → Should return 403.
4. **`PUT /spark/GitHubProject/{id}` for an org-beta project** → Should return 403.
5. **`DELETE /spark/GitHubProject/{id}` for an org-beta project** → Should return 403.
6. **`POST /api/github/projects/{id}/sync-columns` for an org-beta project** → Should return 403.
7. **`GET /api/github/projects`** → Should only list installations for user's orgs.
8. **Create a project for user's own account** (not an org) → Should work (username is in allowed owners).
9. **Webhook arrives for org-beta project** → Should still process correctly (no user context).

### 11.2 Automated Testing

- Unit test `OrganizationAccessService` with mocked `IHttpContextAccessor` claims:
  - Authenticated user with org claims → returns orgs + username
  - Authenticated user with no org claims → returns username only
  - Unauthenticated → returns empty
  - Caching: multiple calls return same array instance
- Unit test `GitHubProjectActions.OnQueryAsync` with a mocked session containing projects from multiple orgs — verify only matching projects returned.
- Unit test `GitHubProjectActions.OnLoadAsync` — allowed vs. denied (throws `SparkAccessDeniedException`).
- Integration test: authenticate as user, verify API responses are filtered.

---

## 12. Future Enhancements

1. **Org membership refresh** — Periodically re-fetch org memberships using stored OAuth tokens.
2. **Org webhook invalidation** — Listen for `organization.member_removed` GitHub webhook events to invalidate sessions.
3. **Role-based access within orgs** — Differentiate between org admins (full CRUD) and org members (read-only).
4. **Audit logging** — Log all authorization denials for security monitoring.
5. **OwnerLogin immutability** — Mark `OwnerLogin` as read-only after initial creation to prevent ownership transfer via the generic edit endpoint.
