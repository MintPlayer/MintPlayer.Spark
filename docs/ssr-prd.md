# PRD: Server-Side Rendering (SSR) for Demo Apps

## Overview

Add server-side rendering support to all 3 demo apps (DemoApp, HR, Fleet) using MintPlayer's ASP.NET SPA prerendering infrastructure. This enables pages to be rendered on the server before being sent to the client, improving SEO and initial load performance.

## Architecture

The SSR approach uses MintPlayer's aspnet-prerendering bridge:

1. **.NET side**: `MintPlayer.AspNetCore.SpaServices.Routing` intercepts requests and calls a Node.js process to render Angular
2. **Node.js bridge**: `aspnet-prerendering` npm package provides `createServerRenderer` which bridges .NET <-> Node.js
3. **Angular side**: `@angular/platform-server`'s `renderApplication` renders the Angular app to HTML on the server
4. **Data passing**: `OnSupplyData` in `ISpaPrerenderingService` loads PersistentObjects from the database and passes them to Angular via `DATA_FROM_SERVER` injection token

## Current State

- All 3 apps use `@angular/build:application` builder (Angular 21)
- All 3 apps use `MintPlayer.AspNetCore.SpaServices` v10.4.0 with `UseSpaImproved`
- No SSR files exist in any app
- npm workspaces monorepo with shared root `node_modules/`

## Changes Required

### 1. Root package.json (npm dependencies)

Add to `dependencies`:
- `@angular/platform-server` (same version as other Angular packages: 21.0.6)
- `reflect-metadata` (required for decorator metadata in server rendering)

Add to `devDependencies`:
- `@angular-devkit/build-angular` (same version as `@angular/build`: 21.0.4) - provides the `server` builder for creating CommonJS server bundles
- `aspnet-prerendering` - Node.js bridge for ASP.NET prerendering

### 2. Per-App .NET Changes

#### 2a. csproj modifications
- Add NuGet packages: `MintPlayer.AspNetCore.SpaServices.Routing` (v10.0.0-rc.6), `MintPlayer.AspNetCore.Hsts` (v10.0.0-rc.1)
- Add `<BuildServerSideRenderer>true</BuildServerSideRenderer>` property

#### 2b. Program.cs modifications
- Add `builder.Services.AddSpaPrerenderingService<SpaPrerenderingService>()`
- Add `app.UseImprovedHsts()` before `UseHttpsRedirection()`
- Add `UseSpaPrerendering` inside the `UseSpaImproved` block:
  ```csharp
  spa.UseSpaPrerendering(options =>
  {
      options.BootModuleBuilder = builder.Environment.IsDevelopment()
          ? new AngularPrerendererBuilder(npmScript: "build:ssr", @"Build at\:", 1)
          : null;
      options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
      options.ExcludeUrls = new[] { "/sockjs-node" };
  });
  ```
- Conditionally use `UseSpaStaticFilesImproved` only in non-Development:
  ```csharp
  if (!builder.Environment.IsDevelopment())
  {
      app.UseSpaStaticFilesImproved();
  }
  ```

#### 2c. SpaPrerenderingService.cs (new file per app)
```csharp
using MintPlayer.AspNetCore.SpaServices.Routing;

namespace <AppNamespace>.Services;

public class SpaPrerenderingService : ISpaPrerenderingService
{
    private readonly ISpaRouteService spaRouteService;
    public SpaPrerenderingService(ISpaRouteService spaRouteService)
    {
        this.spaRouteService = spaRouteService;
    }

    public Task BuildRoutes(ISpaRouteBuilder routeBuilder)
    {
        return Task.CompletedTask;
    }

    public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
    {
        var route = await spaRouteService.GetCurrentRoute(context);
        switch (route?.Name)
        {
            default:
                break;
        }
    }
}
```

### 3. Per-App Angular Changes

#### 3a. angular.json - Add `server` architect target
```json
"server": {
  "builder": "@angular-devkit/build-angular:server",
  "options": {
    "outputPath": "dist/server",
    "main": "src/main.server.ts",
    "tsConfig": "tsconfig.server.json",
    "sourceMap": true,
    "optimization": false
  },
  "configurations": {
    "production": {
      "sourceMap": false,
      "optimization": true
    }
  }
}
```

#### 3b. tsconfig.server.json (new file per app)
```json
{
  "extends": "./tsconfig.app.json",
  "compilerOptions": {
    "outDir": "./out-tsc/server",
    "module": "CommonJS",
    "moduleResolution": "node",
    "types": ["node"]
  },
  "files": [
    "src/main.server.ts"
  ],
  "include": [
    "src/**/*.d.ts"
  ]
}
```

#### 3c. src/main.server.ts (new file per app)
Server entry point that uses `createServerRenderer` from `aspnet-prerendering` to render the Angular app. Receives data from .NET via `params.data` and injects it via `DATA_FROM_SERVER` token.

#### 3d. src/app/providers/data-from-server.ts (new file per app)
Simple `InjectionToken<any>` for receiving server-supplied data.

#### 3e. src/app/app.config.server.ts (new file per app)
Merges the shared `appConfig` with server-specific providers:
- `provideServerRendering()` from `@angular/platform-server`

#### 3f. src/app/app.config.browser.ts (new file per app)
Merges the shared `appConfig` with browser-specific providers:
- `provideBrowserGlobalErrorListeners()`
- `provideAnimations()`
- `APP_BASE_HREF` derived from the document's `<base>` tag

#### 3g. src/app/app.config.ts modifications
Remove browser-only providers (`provideBrowserGlobalErrorListeners`, `provideAnimations`) since they move to `app.config.browser.ts`. Keep shared providers:
- `provideRouter(routes)`
- `provideHttpClient(...)`
- `provideZonelessChangeDetection()`
- App-specific providers (auth, attribute renderers, etc.)

#### 3h. src/main.ts modifications
Update to use `browserConfig` from `app.config.browser.ts` instead of `appConfig` directly.

#### 3i. package.json - Add `build:ssr` script
```json
"build:ssr": "ng build && ng run ClientApp:server"
```

## Redundancies from Guide (NOT needed)

1. **`MESSAGE` provider** - Demo/test only, not relevant to Spark apps
2. **Double `APP_BASE_HREF` setup** - Only needed in `app.config.browser.ts`, not duplicated in `main.ts`
3. **`moduleResolution: "node"` in base tsconfig.json** - Only needed in `tsconfig.server.json`; the browser build uses Angular CLI's module resolution
4. **`PublishAot` / `InvariantGlobalization`** - Not needed; existing apps don't use AOT publishing
5. **`MapControllerRoute` change** - Existing `UseEndpoints` + `MapControllers` pattern is equivalent

## App-Specific Notes

### DemoApp
- No auth, simplest case
- `appConfig` uses `withXsrfConfiguration(...)` for XSRF - keep in shared config

### HR
- Has `provideSparkAuth()` and `withSparkAuth()` - keep in shared config
- Has authentication (`UseAuthentication`/`UseAuthorization`)

### Fleet
- Has `provideSparkAuth()`, `withSparkAuth()`, and `provideSparkAttributeRenderers(...)` - keep in shared config
- Has authentication + custom attribute renderers

## File Inventory (per app)

### New Files
| File | Description |
|------|-------------|
| `Services/SpaPrerenderingService.cs` | .NET prerendering service |
| `ClientApp/tsconfig.server.json` | TypeScript config for server build |
| `ClientApp/src/main.server.ts` | Server entry point |
| `ClientApp/src/app/app.config.server.ts` | Angular server config |
| `ClientApp/src/app/app.config.browser.ts` | Angular browser config |
| `ClientApp/src/app/providers/data-from-server.ts` | Injection token for server data |

### Modified Files
| File | Change |
|------|--------|
| `*.csproj` | Add NuGet packages + BuildServerSideRenderer |
| `Program.cs` | Add prerendering service + middleware |
| `ClientApp/angular.json` | Add server architect target |
| `ClientApp/package.json` | Add build:ssr script |
| `ClientApp/src/main.ts` | Use browserConfig |
| `ClientApp/src/app/app.config.ts` | Remove browser-only providers |

## Task Breakdown

1. **Task 1**: Update root `package.json` with SSR npm dependencies + run `npm install`
2. **Task 2**: Implement SSR for DemoApp (all .NET + Angular changes)
3. **Task 3**: Implement SSR for HR (all .NET + Angular changes)
4. **Task 4**: Implement SSR for Fleet (all .NET + Angular changes)

Tasks 2-4 are independent and can be parallelized.
