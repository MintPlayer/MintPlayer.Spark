# Product Requirements Document: MintPlayer.Spark.Authorization

## Overview

This document outlines the requirements for adding optional login/authentication/authorization functionality to the MintPlayer.Spark framework. The authorization system is designed as a separate NuGet package (`MintPlayer.Spark.Authorization`) that developers can optionally include when access control is needed, while keeping the core Spark framework lightweight for simple CRUD applications.

## Goals

1. **Optional Integration**: The authorization feature must not affect existing Spark applications that don't require access control
2. **Group-Based Permissions**: Use "groups" (not "roles") for organizing users and assigning permissions
3. **File-Based Configuration**: Store groups and permissions in `App_Data/security.json` for easy configuration and version control
4. **Flexible Resource Definitions**: Support permissions at PersistentObject, Query, and custom action levels
5. **Extensibility**: Allow developers to integrate with their own authentication providers (ASP.NET Identity, OAuth, etc.)

## Architecture

### Package Structure

```
MintPlayer.Spark (existing)
├── Abstractions/
│   └── IAccessControl.cs (new interface)
├── Actions/
│   └── DefaultPersistentObjectActions.cs (add IsAllowed method)

MintPlayer.Spark.Authorization (new package)
├── Services/
│   ├── AccessControlService.cs (implements IAccessControl)
│   ├── SecurityConfigurationLoader.cs
│   └── GroupMembershipResolver.cs
├── Models/
│   ├── SecurityConfiguration.cs
│   ├── Group.cs
│   ├── Right.cs
│   └── UserGroupMembership.cs
├── Configuration/
│   └── AuthorizationOptions.cs
└── Extensions/
    └── ServiceCollectionExtensions.cs
```

### Key Components

#### 1. IAccessControl Interface (in MintPlayer.Spark.Abstractions)

```csharp
namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Interface for access control services. Implement this to provide
/// custom authorization logic for PersistentObject operations.
/// </summary>
public interface IAccessControl
{
    /// <summary>
    /// Checks if the current user has permission to perform an action on a resource.
    /// </summary>
    /// <param name="resource">The resource identifier (format: "{action}/{persistentObjectType}"
    /// or "{action}/{persistentObjectType}/{property}" or "{action}/{queryName}")</param>
    /// <param name="groupNames">The groups the current user belongs to</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    Task<bool> IsAllowedAsync(string resource, IEnumerable<string> groupNames);

    /// <summary>
    /// Gets the groups the current user belongs to.
    /// </summary>
    /// <returns>Collection of group names</returns>
    Task<IEnumerable<string>> GetCurrentUserGroupsAsync();
}
```

#### 2. DefaultPersistentObjectActions Modification

Add a virtual `IsAllowedAsync` method that checks for `IAccessControl` service registration:

```csharp
public class DefaultPersistentObjectActions<T> : IPersistentObjectActions<T> where T : class
{
    // Injected via constructor or service locator pattern
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Checks if the current user has permission to perform an action.
    /// When no IAccessControl service is registered, always returns true.
    /// </summary>
    /// <param name="action">The action being performed (e.g., "Read", "Edit", "New", "Delete")</param>
    /// <param name="entityTypeName">The entity type name (e.g., "DemoApp.Person")</param>
    /// <param name="propertyName">Optional property name for field-level security</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    protected virtual async Task<bool> IsAllowedAsync(string action, string entityTypeName, string? propertyName = null)
    {
        var accessControl = _serviceProvider?.GetService<IAccessControl>();
        if (accessControl is null)
        {
            // No authorization configured - allow all operations
            return await Task.FromResult(true);
        }

        var groups = await accessControl.GetCurrentUserGroupsAsync();
        var resource = string.IsNullOrEmpty(propertyName)
            ? $"{action}/{entityTypeName}"
            : $"{action}/{entityTypeName}/{propertyName}";

        return await accessControl.IsAllowedAsync(resource, groups);
    }

    // Existing methods updated to call IsAllowedAsync before operations...
}
```

#### 3. Security Configuration File Format

File location: `App_Data/security.json`

```json
{
  "Groups": {
    "a76a9b99-225d-4b3c-8985-cd29a9ddbd4e": "Admins",
    "1032335a-6eb1-4d6c-bcf4-ae10dbc26b1b": "Readers",
    "24d5aeb4-7c33-4be3-9a7f-cd4169133835": "Editors",
    "d3bd3312-0730-43d9-9bf4-9e14c75b00f7": "Users"
  },
  "GroupComments": {
    "a76a9b99-225d-4b3c-8985-cd29a9ddbd4e": "Full system administrators with all permissions",
    "1032335a-6eb1-4d6c-bcf4-ae10dbc26b1b": "Basic read-only access to all entities",
    "24d5aeb4-7c33-4be3-9a7f-cd4169133835": "Can create, edit, and delete entities",
    "d3bd3312-0730-43d9-9bf4-9e14c75b00f7": "Standard user access"
  },
  "Rights": [
    {
      "Id": "8535933f-0a24-4718-85ef-4962632ed864",
      "Resource": "Read/DemoApp.Person",
      "GroupId": "1032335a-6eb1-4d6c-bcf4-ae10dbc26b1b"
    },
    {
      "Id": "67ab5672-cacb-4a0b-8e9c-98df2d2863fc",
      "Resource": "EditNewDelete/DemoApp.Person",
      "GroupId": "24d5aeb4-7c33-4be3-9a7f-cd4169133835"
    },
    {
      "Id": "ea88b133-e34e-4b49-9296-25904203e879",
      "Resource": "Delete/DemoApp.SystemConfig",
      "IsDenied": true,
      "GroupId": "d3bd3312-0730-43d9-9bf4-9e14c75b00f7"
    },
    {
      "Id": "20b70b1f-fc20-4a2b-ba60-776b3dc14acb",
      "Resource": "Edit/DemoApp.Person/Salary",
      "IsImportant": true,
      "GroupId": "a76a9b99-225d-4b3c-8985-cd29a9ddbd4e"
    },
    {
      "Id": "c429a4b2-5d6d-4911-84d8-014c9b802769",
      "Resource": "Execute/GetActiveEmployees",
      "GroupId": "1032335a-6eb1-4d6c-bcf4-ae10dbc26b1b"
    }
  ]
}
```

## Resource Format Specification

Resources follow the format: `{action_or_combined_actions}/{persistentobject_or_query}[/{property}]`

### Standard Actions for PersistentObjects

| Action | Description |
|--------|-------------|
| `Read` | View/query entities |
| `New` | Create new entities |
| `Edit` | Modify existing entities |
| `Delete` | Remove entities |
| `EditNew` | Combined: Edit + New |
| `EditNewDelete` | Combined: Edit + New + Delete |

### Query Actions

| Action | Description |
|--------|-------------|
| `Execute` | Run a query |
| `ExportToExcel` | Export query results to Excel |

### Custom Actions

Developers can define custom actions for specific operations:

| Example Action | Description |
|----------------|-------------|
| `Approve/{PO}` | Approve an entity |
| `Download/{PO}` | Download associated files |
| `Import/{PO}` | Import data for entity type |

### Property-Level Permissions

For field-level security, append the property name:

- `Edit/DemoApp.Person/Salary` - Permission to edit the Salary field
- `Read/DemoApp.Person/SSN` - Permission to view the SSN field

## Rights Model

```csharp
public class Right
{
    /// <summary>
    /// Unique identifier for this right assignment
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The resource this right applies to
    /// Format: "{action}/{entityType}" or "{action}/{entityType}/{property}" or "{action}/{queryName}"
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// The group this right is assigned to
    /// </summary>
    public Guid GroupId { get; set; }

    /// <summary>
    /// When true, this explicitly denies the permission (overrides grants)
    /// </summary>
    public bool IsDenied { get; set; }

    /// <summary>
    /// When true, marks this as an important/sensitive permission for auditing
    /// </summary>
    public bool IsImportant { get; set; }
}
```

## Permission Resolution Logic

1. **No IAccessControl registered**: Return `true` (allow all)
2. **User has no groups**: Return `false` (deny all)
3. **Check explicit denials first**: If any group has `IsDenied=true` for the resource, deny
4. **Check explicit grants**: If any group has a grant for the resource, allow
5. **Check combined actions**: If requesting `Edit/Person` and group has `EditNewDelete/Person`, allow
6. **Wildcard support** (optional): `*/Person` grants all actions on Person
7. **Default**: Deny if no matching permission found

```csharp
public async Task<bool> IsAllowedAsync(string resource, IEnumerable<string> groupNames)
{
    var groupIds = await ResolveGroupIdsAsync(groupNames);
    var relevantRights = GetRightsForGroups(groupIds);

    // 1. Check explicit denials
    if (relevantRights.Any(r => r.Resource == resource && r.IsDenied))
        return false;

    // 2. Check exact match
    if (relevantRights.Any(r => r.Resource == resource && !r.IsDenied))
        return true;

    // 3. Check combined actions (e.g., EditNewDelete includes Edit, New, Delete)
    var (action, target) = ParseResource(resource);
    foreach (var right in relevantRights.Where(r => !r.IsDenied))
    {
        if (IsCombinedActionMatch(right.Resource, action, target))
            return true;
    }

    return false;
}
```

## Integration with Authentication

The authorization package is authentication-agnostic. Developers must:

1. Implement their own authentication (ASP.NET Identity, JWT, OAuth, etc.)
2. Provide group membership through `IGroupMembershipProvider`:

```csharp
public interface IGroupMembershipProvider
{
    /// <summary>
    /// Gets the group names for the specified user.
    /// </summary>
    Task<IEnumerable<string>> GetUserGroupsAsync(ClaimsPrincipal user);
}
```

### Example: ASP.NET Identity Integration

```csharp
public class IdentityGroupMembershipProvider : IGroupMembershipProvider
{
    private readonly UserManager<ApplicationUser> _userManager;

    public async Task<IEnumerable<string>> GetUserGroupsAsync(ClaimsPrincipal user)
    {
        var appUser = await _userManager.GetUserAsync(user);
        if (appUser is null) return Enumerable.Empty<string>();

        // Groups stored as claims or in a related table
        return user.Claims
            .Where(c => c.Type == "group")
            .Select(c => c.Value);
    }
}
```

## Service Registration

### Basic Setup (Authorization Only)

```csharp
// Program.cs
builder.Services.AddSparkAuthorization(options =>
{
    options.SecurityFilePath = "App_Data/security.json";
    options.DefaultBehavior = DefaultAccessBehavior.DenyAll; // or AllowAll
    options.CacheRights = true;
    options.CacheExpirationMinutes = 5;
});
```

### With Custom Group Membership

```csharp
builder.Services.AddSparkAuthorization(options =>
{
    options.SecurityFilePath = "App_Data/security.json";
})
.AddGroupMembershipProvider<IdentityGroupMembershipProvider>();
```

## Endpoint Protection

The authorization checks should be integrated at the endpoint level in `MintPlayer.Spark`:

```csharp
// In CreatePersistentObject.cs
public async Task HandleAsync(HttpContext httpContext, Guid objectTypeId)
{
    var entityTypeName = GetEntityTypeName(objectTypeId);

    // Check authorization
    var accessControl = httpContext.RequestServices.GetService<IAccessControl>();
    if (accessControl is not null)
    {
        var groups = await accessControl.GetCurrentUserGroupsAsync();
        if (!await accessControl.IsAllowedAsync($"New/{entityTypeName}", groups))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Access denied" });
            return;
        }
    }

    // Continue with existing logic...
}
```

## User Stories

### US-1: Basic Access Control
**As a** developer
**I want to** add basic read/write permissions to my Spark application
**So that** I can control which users can access specific PersistentObjects

**Acceptance Criteria:**
- Can install `MintPlayer.Spark.Authorization` package
- Can configure groups in `security.json`
- Read operations check `Read/{EntityType}` permission
- Write operations check `Edit/{EntityType}` or `New/{EntityType}` permission
- Delete operations check `Delete/{EntityType}` permission

### US-2: No Authorization Required
**As a** developer building a simple CRUD app
**I want to** use Spark without any authorization
**So that** I don't have overhead for internal tools or prototypes

**Acceptance Criteria:**
- Core Spark package works without authorization package
- All operations allowed when no `IAccessControl` is registered
- No security.json required

### US-3: Field-Level Security
**As a** developer
**I want to** restrict access to specific fields on entities
**So that** sensitive data (like salary, SSN) is only visible to authorized users

**Acceptance Criteria:**
- Can define property-level permissions: `Edit/Person/Salary`
- API responses exclude/mask unauthorized fields
- Edit requests reject changes to unauthorized fields

### US-4: Query Permissions
**As a** developer
**I want to** control who can execute specific queries
**So that** sensitive reports are protected

**Acceptance Criteria:**
- Can define query permissions: `Execute/GetSalaryReport`
- Query execution endpoint checks permission
- 403 returned for unauthorized query access

### US-5: Explicit Denials
**As a** developer
**I want to** explicitly deny permissions that would otherwise be granted
**So that** I can create exceptions to broad group permissions

**Acceptance Criteria:**
- Can set `IsDenied: true` on a right
- Denials take precedence over grants
- Can deny specific action while allowing combined action group

## API Endpoints (for security.json management - optional)

These endpoints could be added for runtime security management:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/spark/security/groups` | List all groups |
| POST | `/spark/security/groups` | Create a new group |
| PUT | `/spark/security/groups/{id}` | Update a group |
| DELETE | `/spark/security/groups/{id}` | Delete a group |
| GET | `/spark/security/rights` | List all rights |
| POST | `/spark/security/rights` | Create a new right |
| PUT | `/spark/security/rights/{id}` | Update a right |
| DELETE | `/spark/security/rights/{id}` | Delete a right |
| GET | `/spark/security/users/{userId}/groups` | Get user's groups |
| PUT | `/spark/security/users/{userId}/groups` | Set user's groups |

## Migration Path

1. **Phase 1**: Add `IAccessControl` interface to `MintPlayer.Spark.Abstractions`
2. **Phase 2**: Add virtual `IsAllowedAsync` method to `DefaultPersistentObjectActions`
3. **Phase 3**: Create `MintPlayer.Spark.Authorization` package with core functionality
4. **Phase 4**: Update Spark endpoints to check authorization
5. **Phase 5**: Add security management endpoints (optional)

## Non-Goals

- **Authentication**: The package does not handle user authentication (login, passwords, tokens)
- **User Management**: Users are managed externally; this package only manages groups and permissions
- **Session Management**: Sessions/tokens are handled by the authentication layer
- **Multi-tenancy**: Not in scope for initial release

## Technical Considerations

### Performance
- Cache security.json in memory with configurable expiration
- Support file watcher for hot-reload of configuration changes
- Index rights by resource for fast lookup

### Security
- Validate security.json format on load
- Log authorization failures for audit
- Support `IsImportant` flag for enhanced logging of sensitive operations

### Backwards Compatibility
- Existing Spark applications continue to work without changes
- `IsAllowedAsync` defaults to `true` when no `IAccessControl` is registered

## Success Metrics

1. Zero breaking changes to existing Spark applications
2. Authorization check adds < 1ms latency to requests (cached)
3. security.json supports 1000+ rights without performance degradation
4. Clear error messages for authorization failures

## Open Questions

1. Should we support hierarchical groups (group inheritance)?
2. Should we provide a UI for managing security.json?
3. Should we support storing security config in RavenDB instead of/in addition to JSON file?
4. Should we support resource wildcards (e.g., `*/Person` for all actions on Person)?

---

## Appendix A: Full security.json Schema

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["Groups", "Rights"],
  "properties": {
    "Groups": {
      "type": "object",
      "description": "Map of group ID (GUID) to group name",
      "additionalProperties": {
        "type": "string"
      }
    },
    "GroupComments": {
      "type": "object",
      "description": "Optional descriptions for groups",
      "additionalProperties": {
        "type": "string"
      }
    },
    "Rights": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["Id", "Resource", "GroupId"],
        "properties": {
          "Id": {
            "type": "string",
            "format": "uuid"
          },
          "Resource": {
            "type": "string",
            "pattern": "^[A-Za-z]+/[A-Za-z0-9_.]+(/[A-Za-z0-9_]+)?$"
          },
          "GroupId": {
            "type": "string",
            "format": "uuid"
          },
          "IsDenied": {
            "type": "boolean",
            "default": false
          },
          "IsImportant": {
            "type": "boolean",
            "default": false
          }
        }
      }
    }
  }
}
```

## Appendix B: Example Usage in Custom Actions

```csharp
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    public async Task<bool> ApproveAsync(IAsyncDocumentSession session, string id)
    {
        // Check custom action permission
        if (!await IsAllowedAsync("Approve", "DemoApp.Person"))
        {
            throw new UnauthorizedAccessException("Not authorized to approve persons");
        }

        var person = await session.LoadAsync<Person>(id);
        person.IsApproved = true;
        await session.SaveChangesAsync();
        return true;
    }
}
```
