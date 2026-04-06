# PRD: GitHub Project Board Automation for WebhooksDemo

## Overview

Extend the WebhooksDemo application to allow users to log in with their GitHub account, select GitHub Projects (V2) they want to automate, and configure rules that map webhook events to project board column movements. When a matching webhook event fires, the system automatically moves the related issue/PR to the configured column on the project board.

## Background

The `C:\Repos\ProjectDashboard` project implements similar functionality but with hard-coded project IDs and column mappings (see `Constants.cs` and `ProjectBoardHelper.cs`). This feature makes that pattern user-configurable through the Spark framework's entity model, using:
- **AsDetail** for the list of event-to-column mappings (embedded collection on the project entity)
- **TransientLookupReference** for the well-known set of GitHub webhook event types
- **GitHub GraphQL API** (via `Octokit.GraphQL`) for project board operations

## Goals

1. Users authenticate via GitHub OAuth and grant access to their projects
2. Users select which GitHub Projects (V2) to automate
3. Users configure event-to-column mapping rules per project
4. Incoming webhook events trigger automatic issue/PR movements on the configured projects
5. Leverage existing Spark framework patterns (entities, AsDetail, LookupReference, messaging)

## Non-Goals

- Managing GitHub Project fields other than "Status" (the column field)
- Creating/deleting GitHub Projects from within the app
- Handling GitHub Projects (classic) — only Projects V2
- Multi-tenant isolation (single GitHub App installation assumed)

---

## Architecture

### 1. Authentication: GitHub OAuth

**Current state:** WebhooksDemo has no authentication. Spark Authorization supports Google, Microsoft, Facebook, Twitter but not GitHub.

**Changes needed:**

#### 1a. Add GitHub to `ExternalLoginOptions`

File: `MintPlayer.Spark.Authorization/Configuration/ExternalLoginOptions.cs`

```csharp
public class ExternalLoginOptions
{
    public ExternalProviderOptions? Google { get; set; }
    public ExternalProviderOptions? Microsoft { get; set; }
    public ExternalProviderOptions? Facebook { get; set; }
    public ExternalProviderOptions? Twitter { get; set; }
    public ExternalProviderOptions? GitHub { get; set; }  // NEW — same pattern as existing providers
}
```

#### 1b. Create `AddGitHub()` Extension Method

File: `MintPlayer.Spark.Authorization/Extensions/GitHubAuthenticationExtensions.cs`

**No additional NuGet packages needed.** The built-in `AuthenticationBuilder.AddOAuth()` from `Microsoft.AspNetCore.Authentication` (included via `Microsoft.AspNetCore.Identity`) is sufficient. We create a convenience `AddGitHub()` extension that wraps `AddOAuth()` with GitHub's well-known endpoints, matching the pattern of the built-in `AddGoogle()`, `AddFacebook()`, etc.

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class GitHubAuthenticationExtensions
{
    public static AuthenticationBuilder AddGitHub(
        this AuthenticationBuilder builder,
        Action<OAuthOptions> configureOptions)
    {
        return builder.AddGitHub("GitHub", configureOptions);
    }

    public static AuthenticationBuilder AddGitHub(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<OAuthOptions> configureOptions)
    {
        return builder.AddOAuth(authenticationScheme, authenticationScheme, options =>
        {
            // GitHub OAuth defaults
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.CallbackPath = "/signin-github";

            // Map GitHub user info to standard claims
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");

            // Allow consumer to override/extend
            configureOptions(options);
        });
    }
}
```

#### 1c. Register GitHub OAuth in Program.cs

```csharp
spark.AddAuthentication<SparkUser>(configureProviders: identity =>
{
    identity.AddGitHub(options =>
    {
        options.ClientId = builder.Configuration["GitHub:ClientId"]!;
        options.ClientSecret = builder.Configuration["GitHub:ClientSecret"]!;
        options.Scope.Add("read:user");
        options.Scope.Add("read:project");
        options.SaveTokens = true;  // Store access token for API calls
    });
});
```

> **Note:** The same GitHub App already configured for webhooks can serve as the OAuth provider. GitHub Apps support OAuth flows using their Client ID + Client Secret (separate from the webhook secret and private key).

#### 1d. External Login Endpoints (Prerequisite)

Currently, Spark Authorization has no external login challenge/callback endpoints — the `UserStore` supports storing external logins (`IUserLoginStore<TUser>`), but the OAuth flow initiation and callback handling are not yet wired up. This needs to be implemented as part of Phase 1:

- `GET /spark/auth/external-login?provider=GitHub` — initiates OAuth challenge (redirect to GitHub)
- `GET /signin-github` (callback) — handled by ASP.NET Core's OAuth middleware, links to Identity

#### 1e. Store GitHub Access Token

When a user logs in via GitHub OAuth, the access token must be persisted so we can call the GitHub API on their behalf (to list projects and columns). The token is stored via ASP.NET Core Identity's `SparkUserToken` mechanism, accessible through `UserManager<SparkUser>.GetAuthenticationTokenAsync()`.

#### 1f. Configuration (`appsettings.json`)

```json
{
  "GitHub": {
    "ClientId": "<github-app-client-id>",
    "ClientSecret": "<github-app-client-secret>",
    "WebhookSecret": "<webhook-secret>",
    "PrivateKeyPath": "<path-to-pem>",
    "ProductionAppId": 12345
  }
}
```

---

### 2. Entity Model

#### 2a. `GitHubProject` (Root Entity)

A user-selected GitHub Project V2 that should be automated. Stored in RavenDB.

File: `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/GitHubProject.cs`

```csharp
public class GitHubProject
{
    public string? Id { get; set; }

    /// <summary>Display name of the GitHub project.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>GitHub GraphQL node ID (e.g., "PVT_kwDO...").</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Owner login (user or organization).</summary>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>Project number (visible in GitHub URL).</summary>
    public int Number { get; set; }

    /// <summary>GraphQL ID of the "Status" single-select field.</summary>
    public string StatusFieldId { get; set; } = string.Empty;

    /// <summary>
    /// Cached column options from the project's Status field.
    /// Synced when the project is added or refreshed.
    /// </summary>
    public ProjectColumn[] Columns { get; set; } = [];

    /// <summary>
    /// User-configured rules: which webhook events move issues to which columns.
    /// AsDetail — stored as embedded documents.
    /// </summary>
    public EventColumnMapping[] EventMappings { get; set; } = [];
}
```

#### 2b. `ProjectColumn` (AsDetail — cached from GitHub)

Embedded type representing a column (status option) on the project board.

File: `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/ProjectColumn.cs`

```csharp
public class ProjectColumn
{
    /// <summary>GitHub single-select option ID (e.g., "f75ad846").</summary>
    public string OptionId { get; set; } = string.Empty;

    /// <summary>Display name (e.g., "Todo", "In Progress", "Done").</summary>
    public string Name { get; set; } = string.Empty;
}
```

#### 2c. `EventColumnMapping` (AsDetail with LookupReference)

Embedded type representing a single rule: "when this event fires, move to this column."

File: `Demo/WebhooksDemo/WebhooksDemo.Library/Entities/EventColumnMapping.cs`

```csharp
using MintPlayer.Spark.Abstractions;

public class EventColumnMapping
{
    /// <summary>The webhook event type (e.g., "IssuesOpened").</summary>
    [LookupReference(typeof(LookupReferences.WebhookEventType))]
    public string? WebhookEvent { get; set; }

    /// <summary>The target column option ID on the project board.</summary>
    public string? TargetColumnOptionId { get; set; }

    /// <summary>
    /// For pull request events: also move the issues that the PR closes/references.
    /// Ignored for issue events.
    /// </summary>
    public bool MoveLinkedIssues { get; set; }
}
```

> **No `TargetColumnName`**: The column name is not stored on the mapping. The UI resolves the display name at render time from the parent `GitHubProject.Columns` array using the `TargetColumnOptionId`.

#### 2d. `WebhookEventType` (TransientLookupReference)

A static, code-defined list of GitHub webhook event+action combinations that can trigger column movements.

File: `Demo/WebhooksDemo/WebhooksDemo.Library/LookupReferences/WebhookEventType.cs`

```csharp
using MintPlayer.Spark.Abstractions;

public enum EWebhookEventType
{
    // Issues
    IssuesOpened,
    IssuesClosed,
    IssuesReopened,
    IssuesLabeled,
    IssuesUnlabeled,
    IssuesAssigned,
    IssuesUnassigned,

    // Pull Requests
    PullRequestOpened,
    PullRequestClosed,
    PullRequestMerged,
    PullRequestReadyForReview,
    PullRequestConvertedToDraft,
    PullRequestReviewRequested,

    // Pull Request Reviews
    PullRequestReviewApproved,
    PullRequestReviewChangesRequested,
    PullRequestReviewDismissed,

    // Check Runs
    CheckRunCompleted,

    // Issue Comments
    IssueCommentCreated,
}

public sealed class WebhookEventType : TransientLookupReference<EWebhookEventType>
{
    private WebhookEventType() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    /// <summary>
    /// The Octokit event type name (matches WebhookHeaders.Event).
    /// </summary>
    public string EventName { get; init; } = string.Empty;

    /// <summary>
    /// The Octokit action value (matches the event's Action property).
    /// Null means "any action" (for events without sub-actions).
    /// </summary>
    public string? ActionValue { get; init; }

    public static IReadOnlyCollection<WebhookEventType> Items { get; } =
    [
        // Issues
        new() { Key = EWebhookEventType.IssuesOpened,       EventName = "issues",        ActionValue = "opened",       Values = _TS("Issue opened") },
        new() { Key = EWebhookEventType.IssuesClosed,       EventName = "issues",        ActionValue = "closed",        Values = _TS("Issue closed") },
        new() { Key = EWebhookEventType.IssuesReopened,     EventName = "issues",        ActionValue = "reopened",      Values = _TS("Issue reopened") },
        new() { Key = EWebhookEventType.IssuesLabeled,      EventName = "issues",        ActionValue = "labeled",       Values = _TS("Issue labeled") },
        new() { Key = EWebhookEventType.IssuesUnlabeled,    EventName = "issues",        ActionValue = "unlabeled",     Values = _TS("Issue unlabeled") },
        new() { Key = EWebhookEventType.IssuesAssigned,     EventName = "issues",        ActionValue = "assigned",      Values = _TS("Issue assigned") },
        new() { Key = EWebhookEventType.IssuesUnassigned,   EventName = "issues",        ActionValue = "unassigned",    Values = _TS("Issue unassigned") },

        // Pull Requests
        new() { Key = EWebhookEventType.PullRequestOpened,            EventName = "pull_request", ActionValue = "opened",              Values = _TS("Pull request opened") },
        new() { Key = EWebhookEventType.PullRequestClosed,            EventName = "pull_request", ActionValue = "closed",              Values = _TS("Pull request closed") },
        new() { Key = EWebhookEventType.PullRequestMerged,            EventName = "pull_request", ActionValue = "closed",              Values = _TS("Pull request merged") },
        new() { Key = EWebhookEventType.PullRequestReadyForReview,    EventName = "pull_request", ActionValue = "ready_for_review",    Values = _TS("PR ready for review") },
        new() { Key = EWebhookEventType.PullRequestConvertedToDraft,  EventName = "pull_request", ActionValue = "converted_to_draft",  Values = _TS("PR converted to draft") },
        new() { Key = EWebhookEventType.PullRequestReviewRequested,   EventName = "pull_request", ActionValue = "review_requested",    Values = _TS("PR review requested") },

        // Pull Request Reviews
        new() { Key = EWebhookEventType.PullRequestReviewApproved,          EventName = "pull_request_review", ActionValue = "submitted",  Values = _TS("PR review: approved") },
        new() { Key = EWebhookEventType.PullRequestReviewChangesRequested,  EventName = "pull_request_review", ActionValue = "submitted",  Values = _TS("PR review: changes requested") },
        new() { Key = EWebhookEventType.PullRequestReviewDismissed,         EventName = "pull_request_review", ActionValue = "dismissed",  Values = _TS("PR review dismissed") },

        // Check Runs
        new() { Key = EWebhookEventType.CheckRunCompleted, EventName = "check_run", ActionValue = "completed", Values = _TS("Check run completed") },

        // Issue Comments
        new() { Key = EWebhookEventType.IssueCommentCreated, EventName = "issue_comment", ActionValue = "created", Values = _TS("Issue comment created") },
    ];
}
```

> **Note on "merged":** GitHub sends `pull_request.closed` with `merged: true` for merged PRs. The recipient handler must check the `Merged` flag to distinguish between `PullRequestClosed` and `PullRequestMerged`.

#### 2e. SparkContext Registration

File: `Demo/WebhooksDemo/WebhooksDemo/WebhooksDemoSparkContext.cs`

```csharp
public class WebhooksDemoSparkContext : SparkContext
{
    public IRavenQueryable<GitHubProject> GitHubProjects => Session.Query<GitHubProject>();
}
```

---

### 3. GitHub GraphQL API Integration

#### 3a. New Package Dependency

Add `Octokit.GraphQL` NuGet package to WebhooksDemo (or create a shared service in `MintPlayer.Spark.Webhooks.GitHub`).

#### 3b. `GitHubProjectService` — API Operations

File: `Demo/WebhooksDemo/WebhooksDemo/Services/GitHubProjectService.cs`

This service wraps the GitHub GraphQL API for project board operations. It mirrors the patterns from `ProjectDashboard/Service/CustomCode/ProjectBoardHelper.cs`.

**Key operations:**

| Method | Purpose | Token Used |
|--------|---------|------------|
| `ListUserProjectsAsync()` | List GitHub Projects V2 for the authenticated user | User OAuth token |
| `GetProjectColumnsAsync(projectNodeId)` | Fetch Status field ID + column options | User OAuth token |
| `GetIssueProjectItemIdAsync(owner, repo, issueNumber, projectNodeId)` | Find issue's item ID on a specific project | Installation token |
| `GetPullRequestProjectItemIdAsync(owner, repo, prNumber, projectNodeId)` | Find PR's item ID on a specific project | Installation token |
| `MoveToColumnAsync(projectNodeId, statusFieldId, itemId, columnOptionId)` | Update item's Status field | Installation token |

**GraphQL queries (from ProjectDashboard patterns):**

```csharp
// List user's projects
var projects = await graphQL.Run(
    new Query()
        .Viewer
        .ProjectsV2(first: 50)
        .Select(projects => projects.Select(p => new { p.Id, p.Title, p.Number })));

// Get project columns (Status field options)
var columns = await graphQL.Run(
    new Query()
        .Node(new ID(projectNodeId))
        .Cast<ProjectV2>()
        .Field(name: "Status")           // or iterate Fields to find SingleSelect type
        .Cast<ProjectV2SingleSelectField>()
        .Options
        .Select(o => new { o.Id, o.Name }));

// Move item to column (mutation)
await graphQL.Run(
    new Mutation()
        .UpdateProjectV2ItemFieldValue(new UpdateProjectV2ItemFieldValueInput
        {
            ProjectId = new ID(projectNodeId),
            ItemId = itemId,
            FieldId = new ID(statusFieldId),
            Value = new() { SingleSelectOptionId = columnOptionId },
        })
        .Select(r => r.ClientMutationId));
```

> **Important:** Use the `GraphQLEx` helper pattern from ProjectDashboard to sanitize GraphQL queries (handle null values, newline formatting issues).

---

### 4. Actions Class

File: `Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs`

```csharp
public partial class GitHubProjectActions : DefaultPersistentObjectActions<GitHubProject>
{
    [Inject] private readonly IGitHubProjectService _projectService;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, GitHubProject entity)
    {
        // When a new project is saved and has a NodeId but no columns,
        // auto-fetch the Status field and column options from GitHub.
        if (entity.Columns.Length == 0 && !string.IsNullOrEmpty(entity.NodeId))
        {
            var (statusFieldId, columns) = await _projectService.GetProjectColumnsAsync(entity.NodeId);
            entity.StatusFieldId = statusFieldId;
            entity.Columns = columns;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
```

---

### 5. Webhook Event Processing

#### 5a. Movement Logic

The handler determines **what to move** based on the webhook event category:

**Issue events** (`issues.*`):
1. Extract the issue number from the webhook payload
2. For each configured `GitHubProject` that has a matching `EventMapping`:
   - Look up the issue's project item ID on that board (via GraphQL)
   - If found, move it to the configured `TargetColumnOptionId`
   - `MoveLinkedIssues` is ignored (not applicable to issue events)

**Pull request events** (`pull_request.*`, `pull_request_review.*`):
1. Extract the PR number from the webhook payload
2. For each configured `GitHubProject` that has a matching `EventMapping`:
   - Look up the PR's project item ID on that board (via GraphQL)
   - If found, move the PR to the configured `TargetColumnOptionId`
   - **If `MoveLinkedIssues` is true**: query the PR's closing issues references (via GraphQL `ClosingIssuesReferences`), and for each linked issue that is on the same project board, also move it to the same target column

This mirrors the proven pattern from ProjectDashboard's `MoveLinkedIssuesToColumnHandler`, which queries `PullRequest.ClosingIssuesReferences` and moves each linked issue.

#### 5b. New Recipient: `MoveItemOnProjectBoard`

File: `Demo/WebhooksDemo/WebhooksDemo/Recipients/MoveItemOnProjectBoard.cs`

```csharp
public partial class MoveItemOnProjectBoard : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession _session;
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly ILogger<MoveItemOnProjectBoard> _logger;

    public async Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken)
    {
        // 1. Determine the event type key (e.g., "IssuesOpened")
        var matchingEventTypes = ResolveEventTypes(message.EventType, message.EventJson);
        if (matchingEventTypes.Count == 0) return;

        var isIssueEvent = message.EventType == "issues";
        var isPullRequestEvent = message.EventType is "pull_request" or "pull_request_review";

        // 2. Extract issue/PR number and repo info from event JSON
        var (owner, repo, number) = ExtractEventTarget(message.EventType, message.EventJson);
        if (number == 0) return;

        // 3. Query all GitHubProject documents with matching EventMappings
        var projects = await _session.Query<GitHubProject>()
            .ToListAsync(cancellationToken);

        foreach (var project in projects)
        {
            foreach (var mapping in project.EventMappings)
            {
                if (!matchingEventTypes.Contains(mapping.WebhookEvent)) continue;

                if (isIssueEvent)
                {
                    // Move the issue itself
                    await _projectService.MoveIssueToColumnAsync(
                        project, owner, repo, number, mapping.TargetColumnOptionId!);
                }
                else if (isPullRequestEvent)
                {
                    // Move the PR itself
                    await _projectService.MovePullRequestToColumnAsync(
                        project, owner, repo, number, mapping.TargetColumnOptionId!);

                    // Optionally move linked issues (issues the PR closes)
                    if (mapping.MoveLinkedIssues)
                    {
                        var linkedIssues = await _projectService
                            .GetClosingIssuesAsync(owner, repo, number);

                        foreach (var linkedIssue in linkedIssues)
                        {
                            await _projectService.MoveIssueToColumnAsync(
                                project, owner, linkedIssue.Repo, linkedIssue.Number,
                                mapping.TargetColumnOptionId!);
                        }
                    }
                }
            }
        }
    }

    private List<string> ResolveEventTypes(string eventType, string eventJson)
    {
        // Map raw event type + action to WebhookEventType enum keys
        // Special case: pull_request.closed + merged=true → PullRequestMerged
        // Special case: pull_request_review.submitted + state → Approved/ChangesRequested
    }
}
```

**Processing flow:**

```
GitHub Webhook
  → SparkWebhookEventProcessor (existing)
    → GitHubWebhookMessage broadcast (catch-all queue)
      → MoveItemOnProjectBoard recipient
        → Resolve event type key(s) from raw event + action
        → Query GitHubProject documents for matching event mappings
          → Issue event: move the issue on each matching project board
          → PR event: move the PR on each matching project board
            → If MoveLinkedIssues: query ClosingIssuesReferences, move those too
```

#### 5c. `GitHubProjectService` — Linked Issues Query

Added to the service from section 3b:

```csharp
/// <summary>
/// Gets the issues that a pull request closes (via "Closes #123" references).
/// Uses the GitHub GraphQL ClosingIssuesReferences connection.
/// Pattern from ProjectDashboard's MoveLinkedIssuesToColumnHandler.
/// </summary>
public async Task<List<(string Repo, int Number)>> GetClosingIssuesAsync(
    string owner, string repo, int prNumber)
{
    var results = await graphQL.Run(
        new Query()
            .Repository(owner: owner, name: repo)
            .PullRequest(prNumber)
            .ClosingIssuesReferences(first: 50)
            .Select(issues => issues.Select(issue => new
            {
                issue.Number,
                RepoName = issue.Repository.Name,
            })));

    return results.Select(r => (r.RepoName, r.Number)).ToList();
}
```

#### 5d. Retry & Eventual Consistency

Following the pattern from ProjectDashboard's `IssuePlaceOnKanbanboard`:
- GitHub's project board API has eventual consistency; an issue may not appear on the board immediately after being added
- Use delayed message re-broadcasting (existing Spark Messaging feature) to retry if the project item ID is not found
- Cap retries at ~1 minute, then log a warning

---

### 6. Custom API Endpoints

Two custom endpoints are needed for the frontend to interact with GitHub's API:

#### 6a. `GET /api/github/projects` — List User's GitHub Projects

Returns the authenticated user's GitHub Projects V2, fetched live from the GitHub API using the user's stored OAuth token.

#### 6b. `GET /api/github/projects/{nodeId}/columns` — Get Project Columns

Returns the Status field options (column names + IDs) for a specific project. Used when the user configures event mappings.

#### 6c. `POST /api/github/projects/{nodeId}/sync-columns` — Refresh Cached Columns

Re-fetches columns from GitHub and updates the stored `GitHubProject.Columns` and `StatusFieldId`.

---

### 7. Frontend (Angular)

#### 7a. Authentication

- Add login page using `@mintplayer/ng-spark-auth` library components
- Configure GitHub as an external login provider
- After login, redirect to the project management page

#### 7b. Project Selection Page

- Call `GET /api/github/projects` to list the user's GitHub projects
- Display as a list with checkboxes
- When a project is selected, create/save a `GitHubProject` entity via Spark CRUD (`POST /spark/persistent-objects/GitHubProject`)
- The `OnBeforeSaveAsync` hook auto-fetches columns

#### 7c. Project Detail / Mapping Configuration

- Standard Spark entity detail page for `GitHubProject`
- **Columns** section: read-only AsDetail table showing cached column names (with "Refresh" button calling sync endpoint)
- **Event Mappings** section: editable AsDetail table
  - "Webhook Event" column: dropdown populated by `WebhookEventType` TransientLookupReference
  - "Target Column" column: dropdown populated from the parent entity's `Columns` array (resolves `TargetColumnOptionId` to display name at render time)
  - "Move Linked Issues" column: checkbox (only meaningful for PR events)

> **Challenge:** The "Target Column" dropdown is context-dependent (options come from the parent's `Columns`, not a global lookup). This will require a custom attribute presenter that reads the parent entity's `Columns` array to populate the dropdown options.

---

### 8. Configuration Summary

**`appsettings.json` additions:**

```json
{
  "GitHub": {
    "ClientId": "<github-app-client-id>",
    "ClientSecret": "<github-app-client-secret>",
    "WebhookSecret": "<webhook-secret>",
    "PrivateKeyPath": "<path-to-pem-file>",
    "ProductionAppId": 12345,
    "SmeeChannelUrl": "<optional-for-dev>"
  }
}
```

**`Program.cs` changes:**

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<WebhooksDemoSparkContext>();
    spark.AddActions();                        // NEW: discover GitHubProjectActions
    spark.AddAuthorization();                  // NEW: enable access control
    spark.AddAuthentication<SparkUser>(        // NEW: GitHub OAuth
        configureProviders: identity =>
        {
            identity.AddGitHub(options =>
            {
                options.ClientId = builder.Configuration["GitHub:ClientId"]!;
                options.ClientSecret = builder.Configuration["GitHub:ClientSecret"]!;
                options.Scope.Add("read:user");
                options.Scope.Add("read:project");
                options.SaveTokens = true;
            });
        });
    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddGithubWebhooks(options => { /* existing config */ });
});
```

---

## Implementation Phases

### Phase 1: Authentication & Entity Model
1. Add `GitHub` to `ExternalLoginOptions` and create `AddGitHub()` extension method in `MintPlayer.Spark.Authorization`
2. Implement external login challenge/callback endpoints in Spark Authorization (`/spark/auth/external-login`, callback handling)
3. Configure authentication in WebhooksDemo's `Program.cs`
4. Create `WebhooksDemo.Library` project with entity classes (`GitHubProject`, `ProjectColumn`, `EventColumnMapping`)
5. Create `WebhookEventType` TransientLookupReference
6. Register entities in `WebhooksDemoSparkContext`
7. Create `GitHubProjectActions` class
8. Add `@mintplayer/ng-spark-auth` to the frontend with login page

### Phase 2: GitHub API Integration
1. Add `Octokit.GraphQL` package dependency
2. Implement `GitHubProjectService` with GraphQL operations (modeled after ProjectDashboard's `ProjectBoardHelper`)
3. Add `GraphQLEx` helper for query sanitization
4. Implement custom API endpoints (`/api/github/projects`, `/api/github/projects/{nodeId}/columns`)
5. Auto-fetch columns in `OnBeforeSaveAsync`

### Phase 3: Webhook Processing
1. Implement `MoveIssueOnProjectBoard` catch-all recipient
2. Add event type resolution logic (event + action → `WebhookEventType` key, with special cases for merged PRs and review states)
3. Add retry logic with delayed message re-broadcasting
4. Test end-to-end with smee.io dev tunnel

### Phase 4: Frontend Polish
1. Project selection page (list GitHub projects, select/deselect)
2. Event mapping configuration on project detail page
3. Column refresh button
4. Activity/audit log showing recent webhook-triggered movements

---

## Package Dependencies

| Package | Purpose | Added To |
|---------|---------|----------|
| `Octokit.GraphQL` | GitHub Projects V2 GraphQL API | `WebhooksDemo` |
| `MintPlayer.Spark.Authorization` | Auth framework (existing, not yet referenced) | `WebhooksDemo` |

> **No additional auth packages needed.** `AddGitHub()` wraps the built-in `AuthenticationBuilder.AddOAuth()` from `Microsoft.AspNetCore.Authentication`, which is already available via `Microsoft.AspNetCore.Identity`.

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| GitHub API rate limits (GraphQL: 5000 points/hour) | Cache column definitions in RavenDB; only re-fetch on explicit sync |
| Eventual consistency: issue not on board yet when webhook fires | Retry with delayed message (5s intervals, max 1 minute) — pattern proven in ProjectDashboard |
| OAuth token expiration | GitHub OAuth tokens don't expire by default for GitHub Apps; monitor for 401s and prompt re-auth |
| "Target Column" dropdown is per-project, not a global LookupReference | UI resolves display name from parent `GitHubProject.Columns` at render time; custom presenter for the dropdown |
| User must have admin/write access to the GitHub Project for column operations | Validate permissions when selecting a project; use installation token for mutations |

---

## Design Decisions

1. **Column movements use the GitHub App's installation token** (same as ProjectDashboard). The webhook handler runs without a user context, so the installation token is the natural choice. The user's OAuth token is only needed for browsing projects/columns during setup.

2. **`WebhookEventType` is a TransientLookupReference** (fixed, code-defined). The set of GitHub webhook events is well-defined. New event types require code changes anyway (to handle deserialization and extract issue/PR numbers).

3. **No additional NuGet auth packages.** GitHub OAuth is implemented via a convenience `AddGitHub()` extension method that wraps the built-in `AddOAuth()` handler with GitHub's well-known endpoints. Follows the same pattern as `AddGoogle()`, `AddFacebook()`, etc.

4. **No `TargetColumnName` on `EventColumnMapping`.** The UI resolves display names from the parent `GitHubProject.Columns` array at render time. Avoids denormalization drift when columns are renamed on GitHub.

5. **`MoveLinkedIssues` flag on `EventColumnMapping`** controls whether PR events also move closing issues. Issue events always just move the issue itself.
