# PRD: Login Page via PersistentObject Alias

## Problem Statement

MintPlayer.Spark.Authorization already has a complete backend authentication infrastructure:
- RavenDB-backed ASP.NET Core Identity stores (`UserStore`, `RoleStore`)
- `SparkUser` model with embedded roles, claims, logins, tokens, 2FA
- `AddSparkAuthentication<TUser>()` extension registering Identity services
- `MapSparkIdentityApi<TUser>()` mapping the built-in Identity API endpoints (`/register`, `/login`, `/refresh`, etc.)
- `ClaimsGroupMembershipProvider` bridging Identity roles to authorization groups

**What's missing**: There is no UI for authentication. The Angular frontend has no login page, registration page, or user session management. Users cannot sign in through the web application.

### Why PersistentObject Alias?

With the PO alias system (PR #21), entity types can be accessed via friendly URLs like `/po/car` instead of `/po/{guid}`. This means authentication pages can be modeled as special PersistentObject types with aliases:

- **`/po/login`** - Login page (username + password form)
- **`/po/register`** - Registration page (username + email + password form)

This approach is consistent with how Vidyano handles authentication - the login page _is_ a PersistentObject with a specific type. The framework already knows how to render forms from PO attribute definitions, so the login/register pages get form rendering for free.

### Desired State

1. Navigate to `/po/login` and see a login form (username, password, remember me)
2. Navigate to `/po/register` and see a registration form (username, email, password, confirm password)
3. After successful login, the user is authenticated and can access protected resources
4. Unauthorized access to protected pages redirects to `/po/login`
5. The shell component shows the current user and a logout option when authenticated

## Current Architecture

### Backend Authentication (Already Implemented)

| Component | Location | Purpose |
|-----------|----------|---------|
| `SparkUser` | `Authorization/Identity/SparkUser.cs` | User model (single RavenDB document) |
| `UserStore<TUser>` | `Authorization/Identity/UserStore.cs` | 14 ASP.NET Identity store interfaces |
| `RoleStore` | `Authorization/Identity/RoleStore.cs` | Role CRUD |
| `AddSparkAuthentication<TUser>()` | `Authorization/Extensions/SparkAuthenticationExtensions.cs` | DI registration |
| `MapSparkIdentityApi<TUser>()` | Same file | Maps Identity API endpoints |
| `ClaimsGroupMembershipProvider` | `Authorization/Services/ClaimsGroupMembershipProvider.cs` | Reads groups from user claims |

### Identity API Endpoints (Already Mapped)

These are provided by ASP.NET Core's `MapIdentityApi<TUser>()`:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/register` | POST | User registration |
| `/login` | POST | Login (cookie + bearer + 2FA) |
| `/refresh` | POST | Refresh bearer token |
| `/confirmEmail` | GET | Email confirmation |
| `/resendConfirmationEmail` | POST | Resend confirmation |
| `/forgotPassword` | POST | Initiate password reset |
| `/resetPassword` | POST | Complete password reset |
| `/manage/2fa` | POST | Configure 2FA |
| `/manage/info` | GET/POST | Account info |

### PO Alias System (Already Implemented)

- Entity types have optional `Alias` property (auto-generated from `Name` if not set)
- Routes accept string (GUID or alias): `GET /spark/po/{objectTypeId}`
- `ModelLoader.ResolveEntityType(string idOrAlias)` resolves alias to entity type
- Angular routes: `po/:type`, `po/:type/new`, `po/:type/:id`, `po/:type/:id/edit`
- Shell component prefers aliases in navigation links

### Authorization (Already Implemented)

- `security.json` defines groups and rights
- `PermissionService` checks permissions before CRUD operations
- `SparkAccessDeniedException` caught by endpoints returning 403
- `ClaimsGroupMembershipProvider` reads roles from authenticated user's claims

## Proposed Solution

### Approach: System PersistentObject Types for Auth Pages

Define special "system" PersistentObject types that the Angular frontend recognizes and renders as auth forms instead of standard PO detail views. These system types:

1. Are **not backed by RavenDB entities** - they are transient (no CLR type mapping, no database storage)
2. Have **attributes that define form fields** - the frontend uses these to render the form
3. Have **special handling in the Angular frontend** - when the PO type is a system type, the component renders a purpose-built form instead of the generic PO detail view
4. **Submit to the Identity API endpoints** - not to the standard PO CRUD endpoints

### Why This Approach

1. **Framework-consistent** - authentication pages are just POs, following the same model-driven pattern
2. **Alias-powered** - `/po/login` and `/po/register` are natural URLs using the alias system
3. **Attribute-driven forms** - the PO model JSON defines what fields appear on the form
4. **No new routing concepts** - the existing `po/:type` route handles everything
5. **Customizable** - developers can add/modify attributes on the login/register POs to customize the form

### Alternative Considered: Dedicated Auth Routes

Could add dedicated `/login` and `/register` routes with hardcoded components. **Rejected** because:
- Breaks the "everything is a PO" philosophy
- Requires separate routing logic
- Not customizable through the JSON model system
- Doesn't leverage the existing alias infrastructure

## Detailed Design

### 1. System PO Type JSON Definitions

#### Login PO (`App_Data/Model/Login.json`)

```json
{
  "id": "00000000-0000-0000-0000-000000000001",
  "name": "Login",
  "alias": "login",
  "clrType": "System.Login",
  "system": true,
  "systemAction": "login",
  "displayAttribute": "UserName",
  "attributes": [
    {
      "id": "00000001-0000-0000-0000-000000000001",
      "name": "UserName",
      "label": "Username or email",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 1,
      "showedOn": "PersistentObject",
      "rules": [{ "type": "required", "message": "Username is required" }]
    },
    {
      "id": "00000001-0000-0000-0000-000000000002",
      "name": "Password",
      "label": "Password",
      "dataType": "password",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 2,
      "showedOn": "PersistentObject",
      "rules": [{ "type": "required", "message": "Password is required" }]
    },
    {
      "id": "00000001-0000-0000-0000-000000000003",
      "name": "RememberMe",
      "label": "Remember me",
      "dataType": "boolean",
      "isRequired": false,
      "isVisible": true,
      "isReadOnly": false,
      "order": 3,
      "showedOn": "PersistentObject",
      "rules": []
    },
    {
      "id": "00000001-0000-0000-0000-000000000004",
      "name": "TwoFactorCode",
      "label": "Two-factor code",
      "dataType": "string",
      "isRequired": false,
      "isVisible": false,
      "isReadOnly": false,
      "order": 4,
      "showedOn": "PersistentObject",
      "rules": []
    }
  ]
}
```

#### Register PO (`App_Data/Model/Register.json`)

```json
{
  "id": "00000000-0000-0000-0000-000000000002",
  "name": "Register",
  "alias": "register",
  "clrType": "System.Register",
  "system": true,
  "systemAction": "register",
  "displayAttribute": "Email",
  "attributes": [
    {
      "id": "00000002-0000-0000-0000-000000000001",
      "name": "Email",
      "label": "Email address",
      "dataType": "string",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 1,
      "showedOn": "PersistentObject",
      "rules": [
        { "type": "required", "message": "Email is required" },
        { "type": "email", "message": "Please enter a valid email address" }
      ]
    },
    {
      "id": "00000002-0000-0000-0000-000000000002",
      "name": "Password",
      "label": "Password",
      "dataType": "password",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 2,
      "showedOn": "PersistentObject",
      "rules": [{ "type": "required", "message": "Password is required" }]
    },
    {
      "id": "00000002-0000-0000-0000-000000000003",
      "name": "ConfirmPassword",
      "label": "Confirm password",
      "dataType": "password",
      "isRequired": true,
      "isVisible": true,
      "isReadOnly": false,
      "order": 3,
      "showedOn": "PersistentObject",
      "rules": [{ "type": "required", "message": "Please confirm your password" }]
    }
  ]
}
```

### 2. C# Model Changes

#### `EntityTypeDefinition` - Add System PO Fields

```csharp
public sealed class EntityTypeDefinition
{
    // ... existing fields ...

    /// <summary>
    /// When true, this is a system PO type (login, register, etc.)
    /// that is not backed by a RavenDB entity.
    /// System types are excluded from CRUD operations.
    /// </summary>
    public bool System { get; set; }

    /// <summary>
    /// For system PO types, identifies what action this PO performs.
    /// Values: "login", "register", "forgotPassword", "resetPassword"
    /// </summary>
    public string? SystemAction { get; set; }
}
```

#### `EntityAttributeDefinition` - Add Password DataType

Add `"password"` as a recognized `DataType` value. No code change needed - the attribute already stores `DataType` as a string. The frontend will render `<input type="password">` when `dataType === "password"`.

### 3. Backend Changes

#### System PO Type Handling in Endpoints

System PO types should be **excluded from standard CRUD operations**. When a request targets a system PO type for create/update/delete, the endpoint should return 400:

```csharp
// In CreatePersistentObject, UpdatePersistentObject, DeletePersistentObject:
if (entityType.System)
{
    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
    await httpContext.Response.WriteAsJsonAsync(new { error = "System types do not support CRUD operations" });
    return;
}
```

The `GET /spark/po/{objectTypeId}` (list) and `GET /spark/po/{objectTypeId}/{id}` (get) endpoints should also return 400 for system types since there are no entities to list/get.

#### New Endpoint: Current User Info

Add a new endpoint to get the currently authenticated user:

```
GET /spark/auth/me
```

Response when authenticated:
```json
{
  "isAuthenticated": true,
  "userName": "john@example.com",
  "email": "john@example.com",
  "roles": ["Administrators"],
  "claims": [...]
}
```

Response when not authenticated:
```json
{
  "isAuthenticated": false
}
```

This endpoint is needed so the Angular frontend can check auth state on app load.

#### Identity API Prefix

The existing `MapSparkIdentityApi<TUser>()` maps endpoints at the root (`/register`, `/login`). These should be grouped under `/spark/auth/` for consistency:

```csharp
public static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
    this IEndpointRouteBuilder endpoints)
    where TUser : SparkUser, new()
{
    var authGroup = endpoints.MapGroup("/spark/auth");
    authGroup.MapIdentityApi<TUser>();
    return endpoints;
}
```

This gives us:
- `POST /spark/auth/register`
- `POST /spark/auth/login`
- `POST /spark/auth/refresh`
- etc.

#### Logout Endpoint

ASP.NET Core's `MapIdentityApi` does not include a logout endpoint. Add one:

```
POST /spark/auth/logout
```

Implementation: call `HttpContext.SignOutAsync()` and return 200.

### 4. Angular Frontend Changes

#### Auth Service (`core/services/auth.service.ts`)

New service handling authentication state and API calls:

```typescript
@Injectable({ providedIn: 'root' })
export class AuthService {
  private currentUser = signal<AuthUser | null>(null);
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly user = computed(() => this.currentUser());

  // POST /spark/auth/login
  login(credentials: { email: string; password: string; twoFactorCode?: string }): Observable<LoginResult>

  // POST /spark/auth/register
  register(data: { email: string; password: string }): Observable<void>

  // POST /spark/auth/logout
  logout(): Observable<void>

  // GET /spark/auth/me
  checkAuth(): Observable<AuthUser | null>
}
```

#### Auth Models (`core/models/auth.ts`)

```typescript
export interface AuthUser {
  isAuthenticated: boolean;
  userName: string;
  email: string;
  roles: string[];
}

export interface LoginResult {
  tokenType: string;
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
}
```

#### EntityType Model Update

```typescript
export interface EntityType {
  // ... existing fields ...
  system?: boolean;
  systemAction?: string;
}
```

#### PO Detail Component - System PO Detection

The `po-detail` component (at route `po/:type/:id`) currently renders a standard PO view. Update it to detect system PO types and delegate to the appropriate component:

```typescript
// po-detail.component.ts
if (this.entityType?.system) {
  // System PO type - render special form based on systemAction
  this.isSystemType = true;
  this.systemAction = this.entityType.systemAction;
}
```

However, login/register pages don't have an `:id` in the URL. They use `po/:type` (the list route). So the `query-list` component needs to handle system types too:

```typescript
// query-list.component.ts - when loaded via po/:type
if (this.entityType?.system) {
  // Don't load a query - render the system form instead
  this.isSystemType = true;
  this.systemAction = this.entityType.systemAction;
}
```

#### System PO Form Component (`components/system-po-form/`)

A reusable component that renders the appropriate form based on `systemAction`:

```typescript
@Component({
  selector: 'app-system-po-form',
  template: `
    @switch (systemAction()) {
      @case ('login') { <app-login-form [attributes]="attributes()" /> }
      @case ('register') { <app-register-form [attributes]="attributes()" /> }
    }
  `
})
export class SystemPoFormComponent {
  systemAction = input.required<string>();
  attributes = input.required<EntityAttributeDefinition[]>();
}
```

#### Login Form Component (`components/login-form/`)

Renders a login form based on the PO attributes:

- Reads attributes from the Login PO type definition
- Renders input fields matching attribute definitions (text for UserName, password for Password, checkbox for RememberMe)
- Submits to `AuthService.login()`
- On success: redirects to the return URL or home
- On 2FA required: shows the TwoFactorCode field (initially hidden via `isVisible: false`)
- Shows error messages from the API
- Includes a "Register" link pointing to `/po/register`

#### Register Form Component (`components/register-form/`)

Renders a registration form:

- Reads attributes from the Register PO type definition
- Renders input fields (email, password, confirm password)
- Client-side validation (password match, email format)
- Submits to `AuthService.register()`
- On success: redirects to login page with success message
- Shows error messages from the API
- Includes a "Login" link pointing to `/po/login`

#### Shell Component Updates

Show the current user info and logout button:

```html
<!-- In shell header -->
@if (authService.isAuthenticated()) {
  <span>{{ authService.user()?.userName }}</span>
  <button (click)="logout()">Logout</button>
} @else {
  <a routerLink="/po/login">Login</a>
}
```

#### Auth Guard

Add an Angular route guard that redirects unauthenticated users to `/po/login`:

```typescript
export const authGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  if (authService.isAuthenticated()) {
    return true;
  }
  const router = inject(Router);
  return router.createUrlTree(['/po/login'], {
    queryParams: { returnUrl: state.url }
  });
};
```

This guard should **not** be applied by default - it should be opt-in per route or configurable via the program units.

#### Route Changes

No new routes needed. The existing routes handle everything:

- `po/:type` (login/register - system PO types detected by the component)
- `po/:type/:id` (standard PO detail - unchanged)

### 5. Authentication Flow

#### Login Flow

```
1. User navigates to /po/login
2. Angular route matches po/:type (type = "login")
3. Component loads entity type definition for "login" alias
4. Detects system = true, systemAction = "login"
5. Renders LoginFormComponent with attributes from the PO definition
6. User fills in username + password and submits
7. POST /spark/auth/login { email, password }
8a. Success: receive access token + refresh token
    - Store tokens (cookie or localStorage based on useCookies param)
    - Redirect to returnUrl or /
8b. 2FA required: API returns requiresTwoFactor
    - Show TwoFactorCode field
    - User enters code and resubmits
8c. Failure: display error message
```

#### Registration Flow

```
1. User navigates to /po/register
2. Angular route matches po/:type (type = "register")
3. Component loads entity type definition for "register" alias
4. Detects system = true, systemAction = "register"
5. Renders RegisterFormComponent with attributes from the PO definition
6. User fills in email + password + confirm password
7. Client-side validation (password match, format)
8. POST /spark/auth/register { email, password }
9a. Success: redirect to /po/login with success message
9b. Failure: display validation errors
```

#### Token Strategy

Use **cookie-based authentication** as the primary strategy:
- The Identity API supports `?useCookies=true` on the login endpoint
- Cookies are automatically sent with every request (no interceptor needed)
- Secure, HttpOnly, SameSite cookies prevent XSS token theft
- The `/spark/auth/me` endpoint uses the cookie to return user info

Bearer tokens remain available for API consumers (external integrations, mobile apps) via the standard `?useCookies=false` flow.

### 6. Program Units for Auth Pages

Auth pages should **not** appear in the sidebar navigation. They are system-level pages that the framework knows about, not user-facing program units.

The framework should however expose the login/register entity types through the `/spark/types` endpoint so the Angular app can load their definitions. The Angular app can then detect system types and render them appropriately.

### 7. Handling Unauthorized Access

When a user tries to access a protected resource:

1. **Backend**: Returns 403 (SparkAccessDeniedException) or 401 (no authentication)
2. **Angular HTTP interceptor**: Catches 401 responses and redirects to `/po/login?returnUrl=...`
3. **After login**: User is redirected back to the originally requested URL

```typescript
// auth.interceptor.ts
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401) {
        const router = inject(Router);
        router.navigate(['/po/login'], {
          queryParams: { returnUrl: router.url }
        });
      }
      return throwError(() => error);
    })
  );
};
```

## Implementation Plan

### Phase 1: Backend - System PO Types

1. Add `System` and `SystemAction` properties to `EntityTypeDefinition`
2. Update `ModelLoader` to load system PO types (but skip CLR type resolution for them)
3. Add system type guard to CRUD endpoints (return 400 for system types)
4. Move Identity API endpoints under `/spark/auth/` prefix
5. Add `POST /spark/auth/logout` endpoint
6. Add `GET /spark/auth/me` endpoint

### Phase 2: Frontend - Auth Service & Infrastructure

1. Add `AuthService` with login, register, logout, checkAuth methods
2. Add auth models (`AuthUser`, `LoginResult`)
3. Add `EntityType.system` and `EntityType.systemAction` to TypeScript model
4. Add HTTP interceptor for 401 handling
5. Update shell component to show login/logout based on auth state

### Phase 3: Frontend - Login & Register Forms

1. Create `LoginFormComponent` that reads PO attributes and renders a login form
2. Create `RegisterFormComponent` that reads PO attributes and renders a registration form
3. Create `SystemPoFormComponent` as the router for system PO types
4. Update `query-list` component to detect system PO types and render `SystemPoFormComponent`
5. Support `dataType: "password"` in attribute rendering (input type=password)

### Phase 4: Demo App Configuration

1. Add `Login.json` and `Register.json` to Fleet `App_Data/Model/`
2. Add `Login.json` and `Register.json` to HR `App_Data/Model/`
3. Test complete login/register flow
4. Test authorization (security.json groups matching Identity roles)

### Phase 5: Polish

1. Auth guard for protected routes (opt-in)
2. 2FA flow (show/hide TwoFactorCode field)
3. "Forgot password" flow (additional system PO type)
4. Loading states and error handling
5. Session persistence (remember me / refresh token)

## Files to Create

| File | Purpose |
|------|---------|
| `Demo/Fleet/Fleet/App_Data/Model/Login.json` | Login PO type definition |
| `Demo/Fleet/Fleet/App_Data/Model/Register.json` | Register PO type definition |
| `Demo/HR/HR/App_Data/Model/Login.json` | Login PO type definition |
| `Demo/HR/HR/App_Data/Model/Register.json` | Register PO type definition |
| `MintPlayer.Spark/Endpoints/Auth/GetCurrentUser.cs` | `/spark/auth/me` endpoint |
| `MintPlayer.Spark/Endpoints/Auth/Logout.cs` | `/spark/auth/logout` endpoint |
| Angular: `core/services/auth.service.ts` | Auth service |
| Angular: `core/models/auth.ts` | Auth models |
| Angular: `core/interceptors/auth.interceptor.ts` | 401 interceptor |
| Angular: `components/system-po-form/` | System PO form router |
| Angular: `components/login-form/` | Login form component |
| Angular: `components/register-form/` | Register form component |

## Files to Modify

| File | Change |
|------|--------|
| `MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs` | Add `System`, `SystemAction` properties |
| `MintPlayer.Spark/Services/ModelLoader.cs` | Skip CLR type resolution for system types |
| `MintPlayer.Spark/Endpoints/PersistentObject/Create.cs` | Guard against system types |
| `MintPlayer.Spark/Endpoints/PersistentObject/Update.cs` | Guard against system types |
| `MintPlayer.Spark/Endpoints/PersistentObject/Delete.cs` | Guard against system types |
| `MintPlayer.Spark/Endpoints/PersistentObject/Get.cs` | Guard against system types |
| `MintPlayer.Spark/Endpoints/PersistentObject/List.cs` | Guard against system types |
| `MintPlayer.Spark/SparkMiddleware.cs` | Add `/spark/auth/` routes |
| `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs` | Prefix Identity API with `/spark/auth` |
| Angular: `core/models/entity-type.ts` | Add `system`, `systemAction` |
| Angular: `pages/query-list/query-list.component.ts` | Detect system PO types |
| Angular: `shell/shell.component.ts` | Show login/logout in header |
| Angular: `shell/shell.component.html` | Add auth UI |
| Angular: `app.config.ts` | Register auth interceptor |

## Resolved Design Decisions

1. **Login/register as PersistentObject types**: Yes. This follows the Vidyano pattern where auth pages are POs. The PO model JSON defines form fields, and the Angular app detects `system: true` to render a special form.

2. **Cookie-based auth as primary strategy**: Yes. Cookies are secure (HttpOnly, SameSite), require no client-side token management, and are automatically sent with requests. Bearer tokens remain available for API consumers.

3. **Identity API under `/spark/auth/` prefix**: Yes. Keeps all Spark endpoints under the `/spark/` namespace and avoids route conflicts.

4. **Auth pages not in sidebar**: Correct. Login/register are system-level pages, not user navigation targets. They appear in entity types (for the frontend to load definitions) but not in program units.

5. **System PO types blocked from CRUD**: Yes. System types define form structure only - they don't map to database entities. CRUD endpoints return 400 for system types.

## Open Questions

1. **Should system PO types be auto-generated by the framework (no JSON files needed)?** The Authorization package could register login/register types programmatically when `AddSparkAuthentication` is called, removing the need for JSON files in each demo app.

2. **External login providers (Google, Microsoft)?** The `ExternalLoginOptions` class already exists. Should the login form include social login buttons? This could be driven by a configuration endpoint that tells the frontend which providers are configured.

3. **Email confirmation flow?** The Identity API supports email confirmation. Should there be a system PO type for the confirmation page, or is a simple redirect to the API sufficient?
