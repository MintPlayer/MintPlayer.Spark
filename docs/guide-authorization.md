# Authorization

Spark authorization is an **optional** package (`MintPlayer.Spark.Authorization`) that adds permission-based access control to your Spark application. When enabled, Spark checks permissions before allowing any CRUD operation on entities and queries. Permissions are defined in a `security.json` file and mapped to groups.

## Overview

Without the authorization package, all Spark endpoints are open to any caller. When you add `MintPlayer.Spark.Authorization`, the framework checks every request against the `security.json` configuration. If no matching permission is found, access is denied (by default).

The authorization model is based on:
- **Groups** -- named sets of users (e.g. "Administrators", "Viewers", "Everyone")
- **Rights** -- permission assignments linking a group to a resource (e.g. "Administrators can Read/Edit/New/Delete Person")
- **Resources** -- action/entity pairs (e.g. `Query/Person`, `Edit/Car`, `New/Company`)

## Step 1: Install the Package

Add the `MintPlayer.Spark.Authorization` NuGet package to your project.

## Step 2: Register Authorization Services

In `Program.cs`, add the authorization services:

```csharp
using MintPlayer.Spark.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddSparkActions();
builder.Services.AddScoped<SparkContext, MySparkContext>();

// Add authorization
builder.Services.AddSparkAuthorization();
```

The `AddSparkAuthorization()` method accepts optional configuration:

```csharp
builder.Services.AddSparkAuthorization(options =>
{
    options.SecurityFilePath = "App_Data/security.json";   // default
    options.DefaultBehavior = DefaultAccessBehavior.DenyAll; // default
    options.CacheRights = true;                             // default
    options.CacheExpirationMinutes = 5;                     // default
    options.EnableHotReload = true;                         // default
});
```

| Option | Default | Description |
|---|---|---|
| `SecurityFilePath` | `App_Data/security.json` | Path to the security configuration file (relative to content root) |
| `DefaultBehavior` | `DenyAll` | What happens when no explicit permission matches. `AllowAll` is useful during development |
| `CacheRights` | `true` | Cache parsed rights in memory |
| `CacheExpirationMinutes` | `5` | How long cached rights remain valid |
| `EnableHotReload` | `true` | Watch `security.json` for changes and auto-reload |

## Step 3: Create security.json

Create `App_Data/security.json` in your project. This file defines groups and their permissions:

```json
{
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Everyone", "fr": "Tout le monde", "nl": "Iedereen" },
    "a1b2c3d4-0000-0000-0000-000000000001": { "en": "Administrators" },
    "a1b2c3d4-0000-0000-0000-000000000002": { "en": "Managers" },
    "a1b2c3d4-0000-0000-0000-000000000003": { "en": "Viewers" }
  },
  "rights": [
    {
      "id": "00000000-0000-0000-0000-000000000001",
      "resource": "QueryRead/Company",
      "groupId": "00000000-0000-0000-0000-000000000000",
      "isDenied": false
    },
    {
      "id": "f0000001-0000-0000-0000-000000000001",
      "resource": "QueryReadEditNewDelete/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000001",
      "isDenied": false
    },
    {
      "id": "f0000002-0000-0000-0000-000000000001",
      "resource": "QueryReadEditNew/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000002",
      "isDenied": false
    },
    {
      "id": "f0000003-0000-0000-0000-000000000001",
      "resource": "QueryRead/Car",
      "groupId": "a1b2c3d4-0000-0000-0000-000000000003",
      "isDenied": false
    }
  ]
}
```

### Groups

Groups are keyed by GUID. The group name is a `TranslatedString` (supports multiple languages). You assign users to groups through the `IGroupMembershipProvider` interface.

### The Everyone Group

The group with the name `"Everyone"` (case-insensitive, matches any translation) is automatically applied to **all requests**, including unauthenticated users. Use it to grant public/anonymous access to specific resources:

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "resource": "QueryRead/Company",
  "groupId": "00000000-0000-0000-0000-000000000000",
  "isDenied": false
}
```

This grants all users (including anonymous) the ability to list and view Company entities.

### Rights

Each right has:

| Field | Type | Description |
|---|---|---|
| `id` | GUID | Unique identifier for this permission |
| `resource` | string | Action/target pair (e.g. `"Read/Person"`) |
| `groupId` | GUID | References a group key from the `groups` section |
| `isDenied` | boolean | When `true`, explicitly denies this permission (denials take precedence) |

### Resource Format

Resources follow the pattern `{Action}/{EntityName}`:

| Action | Description | HTTP Method |
|---|---|---|
| `Query` | List entities via query | GET `/spark/query/{id}` |
| `Read` | View a single entity | GET `/spark/po/{typeId}/{id}` |
| `Edit` | Update an entity | PUT `/spark/po/{typeId}/{id}` |
| `New` | Create a new entity | POST `/spark/po/{typeId}` |
| `Delete` | Delete an entity | DELETE `/spark/po/{typeId}/{id}` |

The entity name in the resource matches the entity type's `Name` field in the model JSON (e.g. `Person`, `Car`, `Company`).

### Combined Actions

To avoid repeating individual permissions, use combined action patterns:

| Combined Pattern | Includes |
|---|---|
| `QueryRead` | Query, Read |
| `QueryReadEdit` | Query, Read, Edit |
| `QueryReadEditNew` | Query, Read, Edit, New |
| `QueryReadEditNewDelete` | Query, Read, Edit, New, Delete |
| `EditNew` | Edit, New |
| `EditNewDelete` | Edit, New, Delete |
| `NewDelete` | New, Delete |
| `ReadEdit` | Read, Edit |
| `ReadEditNew` | Read, Edit, New |
| `ReadEditNewDelete` | Read, Edit, New, Delete |

For example, `"QueryReadEditNewDelete/Car"` grants full CRUD access to the Car entity.

### Custom Action Permissions

Custom actions (defined in Actions classes) can also be controlled through `security.json`. The resource format is `{ActionName}/{EntityName}`:

```json
{
  "id": "ca000001-0000-0000-0000-000000000001",
  "resource": "CarCopy/Car",
  "groupId": "a1b2c3d4-0000-0000-0000-000000000001",
  "isDenied": false
}
```

### Permission Evaluation Order

When checking a request:

1. Explicit **denials** are checked first (denials always take precedence)
2. **Exact match** against the resource string
3. **Combined action match** (e.g. `QueryReadEditNewDelete/Car` matches a `Read/Car` request)
4. **Default behavior** (`DenyAll` or `AllowAll` from options)

## Step 4: Add Authentication (Optional)

The authorization package includes built-in ASP.NET Core Identity support with RavenDB-backed user and role stores. To enable authentication:

```csharp
using MintPlayer.Spark.Authorization;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddSparkActions();
builder.Services.AddScoped<SparkContext, MySparkContext>();

// Authorization + Authentication
builder.Services.AddSparkAuthorization();
builder.Services.AddSparkAuthentication<SparkUser>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.MyApp";
});
```

And in the middleware pipeline:

```csharp
var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();       // Must come before UseAuthorization
app.UseAuthorization();
app.UseSparkAntiforgery();     // XSRF protection for cookie auth
app.UseSpark();
app.CreateSparkIndexes();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    endpoints.MapSparkIdentityApi<SparkUser>();  // Maps /spark/auth/* endpoints
});
```

### Identity Endpoints

`MapSparkIdentityApi<TUser>()` maps the following endpoints under `/spark/auth/`:

| Endpoint | Method | Description |
|---|---|---|
| `/spark/auth/register` | POST | Register a new user |
| `/spark/auth/login` | POST | Log in (returns auth cookie) |
| `/spark/auth/logout` | POST | Log out (requires XSRF token) |
| `/spark/auth/me` | GET | Get current user info |
| `/spark/auth/refresh` | POST | Refresh authentication token |
| `/spark/auth/forgotPassword` | POST | Start password reset flow |
| `/spark/auth/resetPassword` | POST | Complete password reset |
| `/spark/auth/manage/2fa` | POST | Configure two-factor authentication |
| `/spark/auth/manage/info` | GET/POST | Get or update user profile |
| `/spark/auth/csrf-refresh` | POST | Get a fresh CSRF token |

### Custom Group Membership Provider

By default, Spark resolves user groups from ASP.NET Core Identity roles. To integrate with a different authentication system, implement `IGroupMembershipProvider`:

```csharp
public class MyGroupProvider : IGroupMembershipProvider
{
    public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        // Return the group names the current user belongs to
        // These names are matched against group translations in security.json
        return Task.FromResult<IEnumerable<string>>(["Administrators"]);
    }
}
```

Register it after `AddSparkAuthorization()`:

```csharp
builder.Services.AddSparkAuthorization()
    .AddGroupMembershipProvider<MyGroupProvider>();
```

## Step 5: XSRF/Antiforgery Protection

When using cookie-based authentication, mutation endpoints (POST, PUT, DELETE) are protected with XSRF tokens.

The `UseSpark()` middleware generates a `XSRF-TOKEN` cookie on every response. The Angular frontend reads this cookie and sends the value as an `X-XSRF-TOKEN` header on mutation requests.

When using the authorization package with cookie auth, call `UseSparkAntiforgery()` in the middleware pipeline to validate the XSRF token on incoming requests:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseSparkAntiforgery();  // Validates X-XSRF-TOKEN header
app.UseSpark();              // Generates XSRF-TOKEN cookie
```

## How Authorization Integrates with Spark

When `IAccessControl` is registered (by calling `AddSparkAuthorization()`), Spark's core `PermissionService` delegates authorization checks to it. If `IAccessControl` is **not** registered, all operations are permitted:

```csharp
// From MintPlayer.Spark/Services/PermissionService.cs
public async Task EnsureAuthorizedAsync(string action, string target, ...)
{
    if (accessControl is null)
        return; // No authorization package = allow everything

    var resource = $"{action}/{target}";
    if (!await accessControl.IsAllowedAsync(resource, cancellationToken))
        throw new SparkAccessDeniedException(resource);
}
```

This means authorization is entirely opt-in. Add the package and call `AddSparkAuthorization()` when you are ready to restrict access.

## Example: Role-Based Access Control

A typical setup with three roles:

| Group | Companies | Cars | People |
|---|---|---|---|
| Administrators | Full CRUD | Full CRUD | Full CRUD |
| Managers | Read | Create/Edit (no delete) | Create/Edit (no delete) |
| Viewers | Read | Read | Read |
| Everyone (anonymous) | Read | -- | -- |

The corresponding `security.json`:

```json
{
  "groups": {
    "00000000-0000-0000-0000-000000000000": { "en": "Everyone" },
    "a1b2c3d4-0000-0000-0000-000000000001": { "en": "Administrators" },
    "a1b2c3d4-0000-0000-0000-000000000002": { "en": "Managers" },
    "a1b2c3d4-0000-0000-0000-000000000003": { "en": "Viewers" }
  },
  "rights": [
    { "id": "...", "resource": "QueryRead/Company", "groupId": "00000000-0000-0000-0000-000000000000", "isDenied": false },

    { "id": "...", "resource": "QueryReadEditNewDelete/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNewDelete/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNewDelete/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000001", "isDenied": false },

    { "id": "...", "resource": "QueryReadEditNew/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },
    { "id": "...", "resource": "QueryReadEditNew/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000002", "isDenied": false },

    { "id": "...", "resource": "QueryRead/Car", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Person", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false },
    { "id": "...", "resource": "QueryRead/Company", "groupId": "a1b2c3d4-0000-0000-0000-000000000003", "isDenied": false }
  ]
}
```

## Complete Example

See the Fleet and HR demo apps for working authorization setups:
- `Demo/Fleet/Fleet/Program.cs` -- full setup with `AddSparkAuthorization`, `AddSparkAuthentication`, and `UseSparkAntiforgery`
- `Demo/Fleet/Fleet/App_Data/security.json` -- role-based permissions including custom action permissions
- `Demo/HR/HR/App_Data/security.json` -- role-based permissions for HR entities
- `MintPlayer.Spark.Authorization/Services/AccessControlService.cs` -- permission evaluation logic
- `MintPlayer.Spark.Authorization/Configuration/AuthorizationOptions.cs` -- configuration options
