# PRD: Customizable Authentication Page Routing

## Status: Draft

## Supersedes
This PRD supersedes the "System PO Type" approach from `docs/login-page-prd.md`. That approach locked auth page URLs to the `/po/{alias}` pattern. This PRD introduces fully customizable URLs via a dedicated Angular library.

## Problem Statement

MintPlayer.Spark.Authorization has a complete backend authentication infrastructure (RavenDB-backed Identity stores, `MapSparkIdentityApi`, cookie + bearer auth). However:

1. **No authentication UI exists** - The Angular frontend has zero auth components, services, guards, or interceptors.
2. **The previously proposed approach (system PO types)** models auth pages as PersistentObjects with aliases (`/po/login`, `/po/register`). This:
   - Locks URLs to the `/po/{alias}` pattern - no way to have `/login` or `/account/signin`
   - Cannot support multi-segment URLs (`/login/two-factor`)
   - Conflates authentication pages with PersistentObjects (they aren't objects)
   - Makes the form rendering depend on backend JSON model definitions, adding complexity

### Desired Outcome

A developer who installs `MintPlayer.Spark.Authorization` (NuGet) and `@mintplayer/ng-spark-auth` (npm) should be able to add authentication pages with **2 lines of frontend code** and **fully customizable URLs**.

## Constraints

- **Package split preserved**: Auth pages only exist when the optional packages are installed
- **Low-code**: Minimal configuration needed for the common case
- **Custom Angular routing**: Developers can override default URLs, add route guards, or provide custom components
- **No "system PO types"**: Auth pages are first-class Angular routes, not PO hacks

## Proposed Solution: Angular Library Package

### Architecture Overview

```
NuGet: MintPlayer.Spark.Authorization (already exists)
  - Backend: Identity stores, AddSparkAuthentication<T>(), MapSparkIdentityApi<T>()
  - Backend: NEW - /spark/auth/me, /spark/auth/logout endpoints
  - Backend: NEW - /spark/auth/ prefix for identity endpoints

npm: @mintplayer/ng-spark-auth (NEW)
  - Angular library with pre-built auth components
  - provideSparkAuth() - registers services, interceptor, guard
  - sparkAuthRoutes() - returns configurable route definitions
```

The two packages are independent deliverables that work together:
- The NuGet package provides the API (already mostly done)
- The npm package provides the UI (new)

### Developer Experience

#### Backend (C# - no changes for most developers)

```csharp
// Program.cs - Already works today
builder.Services.AddSparkAuthentication<SparkUser>();
// ...
endpoints.MapSparkIdentityApi<SparkUser>();
```

#### Frontend (Angular - 2 lines added)

```typescript
// app.config.ts
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withSparkAuth()),  // <-- ADD withSparkAuth()
    provideAnimations(),
    provideSparkAuth(),                  // <-- ADD THIS
  ]
};
```

```typescript
// app.routes.ts
import { sparkAuthRoutes } from '@mintplayer/ng-spark-auth';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      ...sparkAuthRoutes(),      // <-- ADD THIS (adds /login, /register, etc.)
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      // ... existing routes ...
    ]
  }
];
```

**That's it.** Default URLs are:

| Page | Default URL | Component |
|------|-------------|-----------|
| Login | `/login` | `SparkLoginComponent` |
| Two-factor verification | `/login/two-factor` | `SparkTwoFactorComponent` |
| Register | `/register` | `SparkRegisterComponent` |
| Forgot password | `/forgot-password` | `SparkForgotPasswordComponent` |
| Reset password | `/reset-password` | `SparkResetPasswordComponent` |

#### Custom URLs

```typescript
...sparkAuthRoutes({
  login: 'signin',                    // /signin
  twoFactor: 'signin/verify',         // /signin/verify
  register: 'signup',                 // /signup
  forgotPassword: 'account/forgot',   // /account/forgot
  resetPassword: 'account/reset',     // /account/reset
})
```

#### Custom Components (Advanced)

```typescript
...sparkAuthRoutes({
  login: {
    path: 'signin',
    component: MyCustomLoginComponent,  // Use your own component
  },
  // other pages use defaults
})
```

#### Route Guards (Opt-in)

```typescript
import { sparkAuthGuard } from '@mintplayer/ng-spark-auth';

// Protect specific routes:
{ path: 'admin', loadComponent: () => import('./pages/admin/admin.component'), canActivate: [sparkAuthGuard] },
```

## Detailed Design

### 1. npm Package: `@mintplayer/ng-spark-auth`

#### Project Structure

```
node_packages/ng-spark-auth/
  ng-package.json
  package.json
  src/
    public-api.ts                          # Library entry point
    lib/
      providers/
        provide-spark-auth.ts              # provideSparkAuth() + withSparkAuth()
      routes/
        spark-auth-routes.ts               # sparkAuthRoutes() function
      services/
        spark-auth.service.ts              # AuthService (login, register, logout, checkAuth)
      interceptors/
        spark-auth.interceptor.ts          # 401 -> redirect to login
      guards/
        spark-auth.guard.ts                # canActivate guard
      components/
        login/
          spark-login.component.ts         # Login form (email + password)
          spark-login.component.html
        two-factor/
          spark-two-factor.component.ts    # 2FA code entry
          spark-two-factor.component.html
        register/
          spark-register.component.ts      # Registration form
          spark-register.component.html
        forgot-password/
          spark-forgot-password.component.ts
          spark-forgot-password.component.html
        reset-password/
          spark-reset-password.component.ts
          spark-reset-password.component.html
      models/
        auth-user.ts                       # AuthUser interface
        auth-config.ts                     # SparkAuthConfig type
        auth-route-config.ts               # Route customization options
```

#### `provideSparkAuth(options?)`

Registers the authentication infrastructure as Angular providers:

```typescript
export interface SparkAuthConfig {
  /** Base URL for auth API endpoints. Default: '/spark/auth' */
  apiBasePath?: string;

  /** URL to redirect to after successful login. Default: '/' */
  defaultRedirectUrl?: string;

  /** URL of the login page (for 401 redirects). Auto-detected from sparkAuthRoutes(). */
  loginUrl?: string;
}

export function provideSparkAuth(config?: SparkAuthConfig): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: SPARK_AUTH_CONFIG, useValue: { ...defaultConfig, ...config } },
  ]);
}
```

#### `withSparkAuth()`

An `HttpFeature` that adds the auth interceptor to the app's `provideHttpClient()` call. This avoids calling `provideHttpClient()` twice (once by the app, once by the library):

```typescript
export function withSparkAuth(): HttpFeature<HttpFeatureKind.Interceptors> {
  return withInterceptors([sparkAuthInterceptor]);
}
```

#### `sparkAuthRoutes(options?)`

Returns Angular `Routes` array with lazy-loaded auth components:

```typescript
export interface SparkAuthRouteConfig {
  login?: string | { path: string; component?: Type<any> };
  twoFactor?: string | { path: string; component?: Type<any> };
  register?: string | { path: string; component?: Type<any> };
  forgotPassword?: string | { path: string; component?: Type<any> };
  resetPassword?: string | { path: string; component?: Type<any> };
}

export function sparkAuthRoutes(config?: SparkAuthRouteConfig): Routes {
  const c = { ...defaultRouteConfig, ...config };
  return [
    {
      path: resolvePath(c.login),
      loadComponent: resolveComponent(c.login, () => import('../components/login/spark-login.component')),
    },
    {
      path: resolvePath(c.twoFactor),
      loadComponent: resolveComponent(c.twoFactor, () => import('../components/two-factor/spark-two-factor.component')),
    },
    {
      path: resolvePath(c.register),
      loadComponent: resolveComponent(c.register, () => import('../components/register/spark-register.component')),
    },
    {
      path: resolvePath(c.forgotPassword),
      loadComponent: resolveComponent(c.forgotPassword, () => import('../components/forgot-password/spark-forgot-password.component')),
    },
    {
      path: resolvePath(c.resetPassword),
      loadComponent: resolveComponent(c.resetPassword, () => import('../components/reset-password/spark-reset-password.component')),
    },
  ];
}
```

Default paths:
```typescript
const defaultRouteConfig: Required<SparkAuthRouteConfig> = {
  login: 'login',
  twoFactor: 'login/two-factor',
  register: 'register',
  forgotPassword: 'forgot-password',
  resetPassword: 'reset-password',
};
```

#### `SparkAuthService`

Core service managing authentication state and API communication:

```typescript
@Injectable({ providedIn: 'root' })
export class SparkAuthService {
  private currentUser = signal<AuthUser | null>(null);
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly user = this.currentUser.asReadonly();

  /** POST /spark/auth/login?useCookies=true */
  login(credentials: { email: string; password: string }): Observable<void>;

  /** POST /spark/auth/login?useCookies=true (with twoFactorCode) */
  loginTwoFactor(code: string): Observable<void>;

  /** POST /spark/auth/register */
  register(data: { email: string; password: string }): Observable<void>;

  /** POST /spark/auth/logout */
  logout(): Observable<void>;

  /** GET /spark/auth/me */
  checkAuth(): Observable<AuthUser | null>;

  /** POST /spark/auth/forgotPassword */
  forgotPassword(email: string): Observable<void>;

  /** POST /spark/auth/resetPassword */
  resetPassword(data: { email: string; resetCode: string; newPassword: string }): Observable<void>;
}
```

Cookie-based authentication is the default strategy (`?useCookies=true`). Cookies are HttpOnly, Secure, SameSite - no client-side token management needed.

#### Auth Interceptor

Catches 401 responses and redirects to the login page:

```typescript
export const sparkAuthInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401 && !req.url.includes('/spark/auth/')) {
        const router = inject(Router);
        const config = inject(SPARK_AUTH_CONFIG);
        router.navigate([config.loginUrl], {
          queryParams: { returnUrl: router.url }
        });
      }
      return throwError(() => error);
    })
  );
};
```

#### Auth Guard

Opt-in route guard:

```typescript
export const sparkAuthGuard: CanActivateFn = (route, state) => {
  const auth = inject(SparkAuthService);
  if (auth.isAuthenticated()) return true;

  const router = inject(Router);
  const config = inject(SPARK_AUTH_CONFIG);
  return router.createUrlTree([config.loginUrl], {
    queryParams: { returnUrl: state.url }
  });
};
```

#### Pre-built Components

All components are **standalone** (Angular 21, no NgModules) and use the host app's styling (no embedded styles beyond layout). They render semantic HTML with Bootstrap-compatible CSS classes to match the existing `@mintplayer/ng-bootstrap` usage.

**SparkLoginComponent:**
- Email + password fields
- "Remember me" checkbox
- Submit calls `SparkAuthService.login()`
- On success: redirect to `returnUrl` query param or `defaultRedirectUrl`
- On 2FA required: redirect to two-factor page
- Links to register and forgot-password pages
- Error display for invalid credentials

**SparkTwoFactorComponent:**
- 6-digit code input
- "Use recovery code" toggle
- Submit calls `SparkAuthService.loginTwoFactor()`
- On success: redirect to return URL
- Back link to login page

**SparkRegisterComponent:**
- Email + password + confirm password fields
- Client-side validation (password match, required fields)
- Submit calls `SparkAuthService.register()`
- On success: redirect to login page with success message
- Link to login page

**SparkForgotPasswordComponent:**
- Email field
- Submit calls `SparkAuthService.forgotPassword()`
- Success message with instructions

**SparkResetPasswordComponent:**
- Reads `email` and `code` from query params (from email link)
- New password + confirm password fields
- Submit calls `SparkAuthService.resetPassword()`
- On success: redirect to login page

### 2. Backend Changes (NuGet: `MintPlayer.Spark.Authorization`)

#### Prefix Identity Endpoints Under `/spark/auth/`

```csharp
public static IEndpointRouteBuilder MapSparkIdentityApi<TUser>(
    this IEndpointRouteBuilder endpoints)
    where TUser : SparkUser, new()
{
    var authGroup = endpoints.MapGroup("/spark/auth");
    authGroup.MapIdentityApi<TUser>();

    // Additional Spark auth endpoints
    authGroup.MapGet("/me", GetCurrentUser.Handle);
    authGroup.MapPost("/logout", Logout.Handle);

    return endpoints;
}
```

This gives us:
```
POST /spark/auth/register
POST /spark/auth/login
POST /spark/auth/refresh
GET  /spark/auth/confirmEmail
POST /spark/auth/resendConfirmationEmail
POST /spark/auth/forgotPassword
POST /spark/auth/resetPassword
POST /spark/auth/manage/2fa
GET  /spark/auth/manage/info
POST /spark/auth/manage/info
GET  /spark/auth/me              (NEW)
POST /spark/auth/logout          (NEW)
```

#### `GET /spark/auth/me` - Current User Info

```csharp
public static class GetCurrentUser
{
    public static async Task Handle(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            await httpContext.Response.WriteAsJsonAsync(new
            {
                isAuthenticated = true,
                userName = httpContext.User.Identity.Name,
                email = httpContext.User.FindFirstValue(ClaimTypes.Email),
                roles = httpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value),
            });
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync(new { isAuthenticated = false });
        }
    }
}
```

#### `POST /spark/auth/logout`

```csharp
public static class Logout
{
    public static async Task Handle(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
    }
}
```

### 3. Shell Component Integration

The host app's shell component can optionally show login/logout state. The npm package exports a `SparkAuthBarComponent` for this:

```typescript
// In shell.component.html - developer adds this where they want auth UI:
<spark-auth-bar />
```

`SparkAuthBarComponent` renders:
- When authenticated: username + "Logout" button
- When not authenticated: "Login" link (pointing to configured login URL)

Alternatively, developers can use `SparkAuthService` signals directly in their own shell template:

```html
@if (authService.isAuthenticated()) {
  <span>{{ authService.user()?.userName }}</span>
  <button (click)="logout()">Logout</button>
} @else {
  <a routerLink="/login">Login</a>
}
```

## Implementation Plan

### Phase 1: Backend - Auth Endpoint Improvements
1. Prefix Identity endpoints under `/spark/auth/`
2. Add `GET /spark/auth/me` endpoint
3. Add `POST /spark/auth/logout` endpoint
4. Update demo apps to use new endpoint paths

### Phase 2: npm Package Scaffolding
1. Create `node_packages/ng-spark-auth/` Angular library project
2. Implement `SparkAuthService` (login, register, logout, checkAuth, forgotPassword, resetPassword)
3. Implement `provideSparkAuth()` provider function
4. Implement `sparkAuthInterceptor` (401 redirect)
5. Implement `sparkAuthGuard` (canActivate)
6. Implement `sparkAuthRoutes()` route helper with URL customization

### Phase 3: Auth Components
1. Create `SparkLoginComponent` (email + password form, error handling)
2. Create `SparkTwoFactorComponent` (code entry, recovery code toggle)
3. Create `SparkRegisterComponent` (email + password + confirm form)
4. Create `SparkForgotPasswordComponent` (email form)
5. Create `SparkResetPasswordComponent` (new password form)
6. Create `SparkAuthBarComponent` (shell integration)

### Phase 4: Demo App Integration
1. `npm install` the local library in Fleet and HR apps
2. Add `provideSparkAuth()` to `app.config.ts`
3. Add `...sparkAuthRoutes()` to `app.routes.ts`
4. Add `<spark-auth-bar />` to shell component
5. Test complete flows: register, login, 2FA, forgot password, reset password, logout
6. Test 401 interceptor redirect
7. Test custom URL configuration

### Phase 5: Polish
1. Loading states and animations
2. Form validation UX (inline errors, disable submit while loading)
3. "Remember me" support
4. Return URL redirect after login
5. Configurable password requirements display
6. External login provider buttons (if configured)

## Files to Create

| File | Purpose |
|------|---------|
| `node_packages/ng-spark-auth/ng-package.json` | Angular library config |
| `node_packages/ng-spark-auth/package.json` | npm package config |
| `node_packages/ng-spark-auth/src/public-api.ts` | Library exports |
| `node_packages/ng-spark-auth/src/lib/providers/provide-spark-auth.ts` | `provideSparkAuth()` |
| `node_packages/ng-spark-auth/src/lib/routes/spark-auth-routes.ts` | `sparkAuthRoutes()` |
| `node_packages/ng-spark-auth/src/lib/services/spark-auth.service.ts` | Auth service |
| `node_packages/ng-spark-auth/src/lib/interceptors/spark-auth.interceptor.ts` | 401 interceptor |
| `node_packages/ng-spark-auth/src/lib/guards/spark-auth.guard.ts` | Route guard |
| `node_packages/ng-spark-auth/src/lib/components/login/spark-login.component.ts` | Login page |
| `node_packages/ng-spark-auth/src/lib/components/two-factor/spark-two-factor.component.ts` | 2FA page |
| `node_packages/ng-spark-auth/src/lib/components/register/spark-register.component.ts` | Register page |
| `node_packages/ng-spark-auth/src/lib/components/forgot-password/spark-forgot-password.component.ts` | Forgot password page |
| `node_packages/ng-spark-auth/src/lib/components/reset-password/spark-reset-password.component.ts` | Reset password page |
| `node_packages/ng-spark-auth/src/lib/components/auth-bar/spark-auth-bar.component.ts` | Shell auth bar |
| `node_packages/ng-spark-auth/src/lib/models/auth-user.ts` | AuthUser interface |
| `node_packages/ng-spark-auth/src/lib/models/auth-config.ts` | Config types |
| `MintPlayer.Spark.Authorization/Endpoints/GetCurrentUser.cs` | `/spark/auth/me` |
| `MintPlayer.Spark.Authorization/Endpoints/Logout.cs` | `/spark/auth/logout` |

## Files to Modify

| File | Change |
|------|--------|
| `MintPlayer.Spark.Authorization/Extensions/SparkAuthenticationExtensions.cs` | Prefix endpoints under `/spark/auth/`, add `/me` and `/logout` |
| `Demo/Fleet/Fleet/ClientApp/src/app/app.config.ts` | Add `provideSparkAuth()` |
| `Demo/Fleet/Fleet/ClientApp/src/app/app.routes.ts` | Add `...sparkAuthRoutes()` |
| `Demo/Fleet/Fleet/ClientApp/src/app/shell/shell.component.html` | Add `<spark-auth-bar />` |
| `Demo/HR/HR/ClientApp/src/app/app.config.ts` | Add `provideSparkAuth()` |
| `Demo/HR/HR/ClientApp/src/app/app.routes.ts` | Add `...sparkAuthRoutes()` |
| `Demo/HR/HR/ClientApp/src/app/shell/shell.component.html` | Add `<spark-auth-bar />` |

## Design Decisions

### 1. npm package instead of system PO types
Auth pages are real Angular routes with pre-built components, not PersistentObject hacks. This gives full URL control and cleaner architecture. The "everything is a PO" philosophy applies to domain data, not framework infrastructure.

### 2. Cookie-based auth as primary strategy
The Identity API supports `?useCookies=true`. Cookies are HttpOnly, Secure, SameSite - no client-side token management. Bearer tokens remain available for API consumers via `?useCookies=false`.

### 3. `provideSparkAuth()` + `sparkAuthRoutes()` separation
Providers and routes are separate because a developer might want the auth service/interceptor without the default route components (e.g., fully custom pages that still use `SparkAuthService`).

### 4. All components are standalone and lazy-loaded
Follows Angular 21 best practices. No NgModules. Components are tree-shakeable and only loaded when the user navigates to them.

### 5. No auth pages in sidebar navigation
Auth pages are framework infrastructure, not program units. They don't appear in the sidebar.

### 6. Bootstrap-compatible styling
Components use Bootstrap CSS classes to match the existing `@mintplayer/ng-bootstrap` design system used by all demo apps. No component-scoped styles that would fight the host app's theme.

### 7. Identity endpoints prefixed under `/spark/auth/`
All Spark API endpoints live under `/spark/`. Moving Identity endpoints there avoids root-level route conflicts and is consistent with the existing convention.

## Comparison with Previous Approach

| Aspect | System PO Types (old) | Angular Library (new) |
|--------|----------------------|----------------------|
| URL format | `/po/login`, `/po/register` | `/login`, `/register` (customizable) |
| Multi-segment URLs | Not possible | Supported (`/login/two-factor`) |
| Custom URLs | Change PO alias only | Any path string |
| Custom components | Not possible | Pass component to route config |
| Package boundary | JSON model files in each app | npm install + 2 lines |
| Form rendering | Backend-driven (PO attributes) | Angular components |
| Conceptual fit | Auth pages pretend to be POs | Auth pages are auth pages |

## Open Questions

1. **Should the npm package also export a `SparkService` for core Spark operations?** Currently the demo apps each have their own copy of `SparkService`. A shared `@mintplayer/ng-spark` package could be a future effort.

2. **External login providers (Google, Microsoft)?** The backend already supports `AddGoogle()`, `AddMicrosoftAccount()` etc. via the `IdentityBuilder`. The login component could show social login buttons if a `/spark/auth/external-providers` endpoint lists configured providers.

3. **Email confirmation flow?** The Identity API supports email confirmation. Should `SparkRegisterComponent` show a "check your email" message after registration? This depends on whether `options.SignIn.RequireConfirmedEmail` is set.

4. **Should auth pages render inside or outside the ShellComponent?** Current proposal: inside (as children of ShellComponent, so sidebar is visible). Alternative: outside (full-page login without sidebar). Could be configurable.
