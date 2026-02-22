# MintPlayer.Spark.Authorization

Optional authorization package for MintPlayer.Spark. Adds ASP.NET Core Identity backed by RavenDB, group-based access control via `security.json`, and automatic Angular frontend integration.

## Installation

```bash
dotnet add package MintPlayer.Spark.Authorization
```

## Backend Setup

### 1. Register Services

```csharp
// Program.cs
builder.Services.AddSparkAuthorization();
builder.Services.AddSparkAuthentication<SparkUser>();
```

### 2. Map Identity Endpoints

```csharp
app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    endpoints.MapSparkIdentityApi<SparkUser>();
});
```

This maps the following endpoints under `/spark/auth`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/spark/auth/login` | POST | Log in (cookie-based) |
| `/spark/auth/register` | POST | Register a new user |
| `/spark/auth/me` | GET | Get current user info |
| `/spark/auth/logout` | POST | Log out |
| `/spark/auth/forgotPassword` | POST | Request password reset |
| `/spark/auth/resetPassword` | POST | Reset password with token |
| `/spark/auth/manage/2fa` | POST | Two-factor authentication |

### 3. Configure Access Control (security.json)

Create `App_Data/security.json` to define group-based permissions:

```json
{
  "defaultBehavior": "DenyAll",
  "groups": [
    {
      "name": "Administrators",
      "rights": [
        { "action": "ReadEditNewDelete", "resource": "*" }
      ]
    },
    {
      "name": "Viewers",
      "rights": [
        { "action": "Read", "resource": "Person" },
        { "action": "Read", "resource": "Company" }
      ]
    }
  ]
}
```

## Angular Frontend Setup

### Automatic npm Package Installation

When you reference `MintPlayer.Spark.Authorization` (via NuGet), the package includes MSBuild targets that automatically:

1. **Install `@mintplayer/ng-spark-auth`** via npm on first build (if your project has a `package.json` in the SPA root)
2. **Generate `spark-auth.setup.ts`** - a TypeScript scaffolding file with documented auth helpers

Both happen automatically during `dotnet build`. No manual npm install needed.

### Wire Up Your Angular App

After the first build, a `spark-auth.setup.ts` file appears in your SPA's `src/` directory. Use it to wire up authentication:

**app.config.ts:**

```typescript
import { ApplicationConfig } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { setupSparkAuthProviders, setupSparkAuthHttp } from './spark-auth.setup';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(setupSparkAuthHttp()),
    ...setupSparkAuthProviders(),
  ]
};
```

**app.routes.ts:**

```typescript
import { Routes } from '@angular/router';
import { setupSparkAuthRoutes, sparkAuthGuard } from './spark-auth.setup';

export const routes: Routes = [
  {
    path: '',
    children: [
      ...setupSparkAuthRoutes(),
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'protected', loadComponent: () => import('./pages/protected/protected.component'), canActivate: [sparkAuthGuard] },
    ]
  }
];
```

The generated setup file includes the following helpers:

| Export | Description |
|--------|-------------|
| `setupSparkAuthProviders(config?)` | Returns providers array for `app.config.ts` |
| `setupSparkAuthHttp()` | Returns `HttpFeature` with auth interceptor (handles 401 redirects) |
| `setupSparkAuthRoutes(config?)` | Returns route array with login, register, forgot-password, reset-password pages |
| `sparkAuthGuard` | Route guard that redirects unauthenticated users to login |
| `SparkAuthBarComponent` | Auth bar component (`<spark-auth-bar>`) for login/logout UI |
| `SparkAuthService` | Injectable service with `login()`, `register()`, `logout()`, `user` signal, etc. |

### Customizing the Generated File

The `spark-auth.setup.ts` file is generated **once** and never overwritten. You can freely customize it - for example, to change default configuration:

```typescript
export function setupSparkAuthProviders(config?: Partial<SparkAuthConfig>) {
  return [provideSparkAuth({
    apiBasePath: '/spark/auth',
    defaultRedirectUrl: '/dashboard',
    loginUrl: '/sign-in',
    ...config,
  })];
}
```

### Importing Directly

You can also skip the generated file and import directly from the npm package:

```typescript
import { provideSparkAuth, withSparkAuth, sparkAuthRoutes } from '@mintplayer/ng-spark-auth';
```

## MSBuild Properties

Customize the build targets by setting these properties in your `.csproj`:

| Property | Default | Description |
|----------|---------|-------------|
| `EnableSparkAuthSpa` | `true` | Master switch for all SPA-related targets |
| `GenerateSparkAuthSetupFile` | `true` | Set to `false` to skip generating the TypeScript setup file |
| `SpaRoot` | `ClientApp\` | Path to the SPA source directory |
| `SparkAuthSetupFile` | `$(SpaRoot)src\spark-auth.setup.ts` | Path for the generated TypeScript file |
| `SparkAuthNpmPackage` | `@mintplayer/ng-spark-auth` | npm package to install |

Example - disable automatic frontend setup:

```xml
<PropertyGroup>
    <EnableSparkAuthSpa>false</EnableSparkAuthSpa>
</PropertyGroup>
```

## Local Development (ProjectReference)

When referencing the Authorization project directly (instead of via NuGet), add explicit imports to your `.csproj` since `buildTransitive` only applies to NuGet package references:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

    <Import Project="..\path\to\MintPlayer.Spark.Authorization\Targets\spark-authorization.props" />

    <!-- ... your project content ... -->

    <Import Project="..\path\to\MintPlayer.Spark.Authorization\Targets\spark-authorization.targets" />

</Project>
```

## Requirements

- .NET 10.0+
- RavenDB 6.2+
- Node.js (for automatic npm integration)
- Angular 21+ (for frontend components)

## License

MIT License
