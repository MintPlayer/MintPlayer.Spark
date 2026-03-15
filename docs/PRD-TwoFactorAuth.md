# PRD: Two-Factor Authentication & Popup External Login

## Problem Statement

MintPlayer.Spark's authentication system currently has two gaps:

1. **No 2FA setup UI or management** — The backend infrastructure exists (UserStore implements `IUserTwoFactorStore`, `IUserAuthenticatorKeyStore`, `IUserTwoFactorRecoveryCodeStore`; the Angular `spark-two-factor` component handles code entry), but users cannot enable/disable 2FA or manage recovery codes because:
   - No custom endpoints expose TOTP secret generation or 2FA enable/disable
   - No Angular component renders a QR code or recovery codes
   - The Identity Provider's MVC login page blocks 2FA with a TODO comment

2. **External login uses full-page redirect** — When a user clicks "Login with SparkId", the browser navigates away from the app entirely. This is jarring compared to the popup-window approach used in the existing MintPlayer project, where a small window opens for the OAuth flow and posts the result back via `window.opener.postMessage()`.

---

## Solution Overview

### Part A: Two-Factor Authentication

Add TOTP-based 2FA (authenticator app) with recovery codes to Spark apps, covering setup, login, external login, and the OIDC Identity Provider flow.

### Part B: Popup External Login

Replace the full-page redirect for external logins with a popup window flow. The popup opens the external login URL, completes the OAuth flow, and sends the result back to the opener via `postMessage`.

---

## Part A: Two-Factor Authentication

### What Already Exists

| Layer | Component | Status |
|---|---|---|
| **Backend model** | `SparkUser.TwoFactorEnabled`, `AuthenticatorKey`, `TwoFactorRecoveryCodes` | Ready |
| **Backend store** | `UserStore<TUser>` implements all 2FA interfaces | Ready |
| **Backend Identity API** | `MapIdentityApi<TUser>()` maps `/manage/2fa` and `/manage/info` | Ready (but limited) |
| **Backend login** | `SignInManager` returns `RequiresTwoFactor` when 2FA is enabled | Ready |
| **Angular login** | Login component catches `RequiresTwoFactor` 401, navigates to 2FA page | Ready |
| **Angular 2FA page** | `SparkTwoFactorComponent` with code + recovery code toggle | Ready |
| **Angular auth service** | `loginTwoFactor(code, recoveryCode)` method | Ready |
| **Angular routes** | `/login/two-factor` route in `sparkAuthRoutes()` | Ready |
| **IdP login page** | MVC login page blocks 2FA with `// TODO` comment | Blocked |

### What Needs to Be Built

#### Phase 1: Backend 2FA Management Endpoints

Add custom endpoints to `MintPlayer.Spark.Authorization` under `/spark/auth/2fa/`:

| Endpoint | Method | Purpose |
|---|---|---|
| `/spark/auth/2fa/setup` | GET | Generate TOTP secret, return as `otpauth://` URI for QR code |
| `/spark/auth/2fa/enable` | POST | Verify TOTP code and enable 2FA; return recovery codes |
| `/spark/auth/2fa/disable` | POST | Verify TOTP code and disable 2FA |
| `/spark/auth/2fa/recovery-codes` | POST | Regenerate recovery codes (requires TOTP verification) |
| `/spark/auth/2fa/status` | GET | Return `{ enabled, recoveryCodesLeft }` |

**Implementation notes:**
- All endpoints require `[Authorize]` (user must be logged in)
- Enable/disable/regenerate require a valid TOTP code as confirmation
- The setup endpoint calls `UserManager.ResetAuthenticatorKeyAsync()` then `GetAuthenticatorKeyAsync()`, formats the key into an `otpauth://totp/` URI containing the app name and user email
- Enable endpoint calls `UserManager.VerifyTwoFactorTokenAsync()`, then `SetTwoFactorEnabledAsync(true)`, then `GenerateNewTwoFactorRecoveryCodesAsync(10)`
- Recovery code regeneration also requires TOTP verification to prevent abuse

**File:** `MintPlayer.Spark.Authorization/Endpoints/TwoFactor.cs`

**Request/Response models:**

```csharp
// GET /spark/auth/2fa/setup
record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

// POST /spark/auth/2fa/enable
record TwoFactorEnableRequest(string Code);
record TwoFactorEnableResponse(IEnumerable<string> RecoveryCodes);

// POST /spark/auth/2fa/disable
record TwoFactorDisableRequest(string Code);

// POST /spark/auth/2fa/recovery-codes
record TwoFactorRegenerateRequest(string Code);
record TwoFactorRegenerateResponse(IEnumerable<string> RecoveryCodes);

// GET /spark/auth/2fa/status
record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft);
```

#### Phase 2: Angular 2FA Management UI

Add a `SparkTwoFactorSetupComponent` to `@mintplayer/ng-spark-auth`:

**Component: `spark-two-factor-setup`**
- Route: `/login/two-factor/setup` (added to `sparkAuthRoutes()`)
- Guarded by `sparkAuthGuard` (must be logged in)
- Flow:
  1. On init, call `GET /spark/auth/2fa/status`
  2. If disabled: call `GET /spark/auth/2fa/setup` to get secret + URI
  3. Display QR code (render `otpauth://` URI as QR image using a small library or inline SVG generator)
  4. Display shared key as text fallback (formatted in groups of 4 chars)
  5. User enters 6-digit code from authenticator app
  6. Call `POST /spark/auth/2fa/enable` with code
  7. Display recovery codes with copy-to-clipboard button
  8. If already enabled: show status, option to disable (requires code) or regenerate recovery codes

**Service methods to add to `SparkAuthService`:**

```typescript
getTwoFactorStatus(): Promise<{ enabled: boolean; recoveryCodesLeft: number }>
getTwoFactorSetup(): Promise<{ sharedKey: string; authenticatorUri: string }>
enableTwoFactor(code: string): Promise<{ recoveryCodes: string[] }>
disableTwoFactor(code: string): Promise<void>
regenerateRecoveryCodes(code: string): Promise<{ recoveryCodes: string[] }>
```

**QR Code rendering:** Use a lightweight client-side QR generator (e.g., `qrcode` npm package or inline canvas rendering). The `otpauth://` URI format is:
```
otpauth://totp/{AppName}:{UserEmail}?secret={Base32Key}&issuer={AppName}&digits=6&period=30
```

#### Phase 3: Identity Provider 2FA (MVC Pages)

The OIDC Identity Provider's MVC login page must support 2FA when a user with 2FA enabled logs in during an OAuth flow.

**Current state** (`MintPlayer.Spark.IdentityProvider/Endpoints/Login.cs`, line ~109):
```csharp
if (result.RequiresTwoFactor) {
    // TODO: MVC two-factor page
    RedirectWithError(context, returnUrl, "Two-factor authentication is not yet supported in this flow.");
    return;
}
```

**Implementation:**

1. Add a new endpoint pair: `GET /connect/two-factor` and `POST /connect/two-factor`
2. When `SignInManager.PasswordSignInAsync()` returns `RequiresTwoFactor`:
   - Store the `returnUrl` in a query parameter or encrypted cookie
   - Redirect to `GET /connect/two-factor?returnUrl={encoded}`
3. `GET /connect/two-factor` renders inline HTML (same pattern as login/consent pages):
   - 6-digit code input field
   - "Use recovery code" toggle with recovery code input
   - "Remember this device" checkbox
   - Submit button
   - Hidden field for `returnUrl`
4. `POST /connect/two-factor`:
   - If authenticator code: call `SignInManager.TwoFactorAuthenticatorSignInAsync()`
   - If recovery code: call `SignInManager.TwoFactorRecoveryCodeSignInAsync()`
   - On success: redirect to `returnUrl` (which re-enters `/connect/authorize`)
   - On failure: re-render form with error message

**File:** `MintPlayer.Spark.IdentityProvider/Endpoints/TwoFactor.cs`

**Route registration** (in `SparkIdentityProviderExtensions.cs`):
```csharp
app.MapGet("/connect/two-factor", TwoFactor.HandleGet);
app.MapPost("/connect/two-factor", TwoFactor.HandlePost);
```

**SPA exclusion:** Already covered — `/connect/*` is excluded from SPA fallback.

#### Phase 4: Bypass 2FA for External Logins (Optional)

Inspired by MintPlayer's `Bypass2faForExternalLogin` property:

1. Add `Bypass2faForExternalLogin` property to `SparkUser`
2. In `OidcCallback.cs` `FindOrCreateUserAndSignInAsync()`, pass the bypass flag to `SignInManager.ExternalLoginSignInAsync()`
3. If 2FA is required and bypass is disabled, the external login callback needs to handle the `RequiresTwoFactor` result — redirect to a 2FA verification step before completing the login
4. Add a toggle in the Angular 2FA setup component to configure this preference

**Note:** This phase is optional and can be deferred. The primary 2FA flow (Phases 1-3) works without it.

---

## Part B: Popup External Login

### Current Behavior

When the user clicks "Login with SparkId" on the HR/Fleet login page:
1. The button is an `<a href="/spark/auth/external-login/sparkid?returnUrl=...">` link
2. The browser navigates away from the Angular app entirely
3. After OAuth completes, the callback redirects back to the app
4. The Angular app cold-boots and the user lands on the return URL

### Desired Behavior (MintPlayer Pattern)

1. User clicks "Login with SparkId"
2. A popup window (600x500) opens to `/spark/auth/external-login/sparkid?returnUrl=...&popup=true`
3. The OAuth flow happens entirely within the popup
4. After the callback processes the login, the popup renders a small HTML page that calls `window.opener.postMessage({ status, scheme }, origin)`
5. The parent window receives the message, refreshes auth state, and navigates
6. The popup auto-closes

### Implementation

#### Phase 5: Backend — Popup Callback Page

**Modify `OidcCallback.cs`:**

Currently, `OidcCallback.Handle()` returns `Results.Redirect(stateCookie.ReturnUrl)` on success. For popup mode:

1. Store a `popup` flag in the `OidcStateCookie` (set when `/spark/auth/external-login/{scheme}` receives `?popup=true`)
2. If `popup == true`, instead of redirecting, return an HTML page:

```csharp
if (stateCookie.Popup)
{
    var origin = $"{request.Scheme}://{request.Host}";
    return Results.Content($"""
        <!DOCTYPE html>
        <html><head><title>Login complete</title>
        <script>
            window.opener.postMessage(JSON.stringify({{
                status: "success",
                scheme: "{stateCookie.Scheme}"
            }}), "{origin}");
            window.close();
        </script>
        </head><body><p>Login successful. This window will close automatically.</p></body></html>
        """, "text/html");
}
```

3. For error cases, post an error message instead:

```csharp
window.opener.postMessage(JSON.stringify({
    status: "error",
    error: "{error}",
    description: "{errorDescription}"
}), "{origin}");
```

**Modify `ExternalLogin.cs`:**

Pass `popup` through to the state cookie:

```csharp
var popup = httpContext.Request.Query["popup"].FirstOrDefault() == "true";
// Include in OidcStateCookie
```

**New model field:**

```csharp
internal class OidcStateCookie
{
    // ... existing fields
    public bool Popup { get; set; }
}
```

#### Phase 6: Angular — Popup Window Management

**Modify the login component in `@mintplayer/ng-spark-auth`:**

Replace the `<a href>` links for OIDC providers with buttons that open popups:

```typescript
// In spark-login.component.ts
openExternalLogin(provider: SparkOidcProvider): void {
    const returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/';
    const url = `${this.config.apiBasePath}/external-login/${provider.scheme}?returnUrl=${encodeURIComponent(returnUrl)}&popup=true`;

    const popup = window.open(url, '_blank', 'width=600,height=500');

    const listener = (event: MessageEvent) => {
        if (event.origin !== window.location.origin) return;

        let data: { status: string; scheme?: string; error?: string };
        try {
            data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        } catch {
            return; // ignore non-JSON messages (browser extensions, etc.)
        }

        if (data.status === 'success') {
            window.removeEventListener('message', listener);
            this.authService.checkAuth().then(() => {
                this.router.navigateByUrl(returnUrl);
            });
        } else if (data.status === 'error') {
            window.removeEventListener('message', listener);
            this.errorMessage.set(data.error || 'External login failed');
        }
    };

    window.addEventListener('message', listener);
}
```

**Template change:**

```html
<!-- Before (full-page redirect) -->
<a [href]="'/spark/auth/external-login/' + provider.scheme + '?returnUrl=' + returnUrl">

<!-- After (popup) -->
<button class="btn btn-outline-{{provider.buttonClass}} w-100 mb-2"
        (click)="openExternalLogin(provider)">
    <i class="bi bi-{{provider.icon}}"></i> {{ provider.displayName }}
</button>
```

#### Phase 7: 2FA in Popup Flow

When the external login triggers 2FA (Phase 4), the 2FA form must render inside the popup:

1. `OidcCallback.cs` detects `RequiresTwoFactor` result
2. If `popup == true`, redirect within popup to `/spark/auth/two-factor?popup=true&returnUrl=...`
3. Add a new endpoint `GET /spark/auth/two-factor` that renders an inline HTML form (same pattern as IdP login/consent)
4. `POST /spark/auth/two-factor` processes the code:
   - On success + popup: render postMessage HTML page (same as Phase 5)
   - On success + no popup: redirect to returnUrl
   - On failure: re-render form with error

---

## Implementation Order

| Phase | Scope | Package | Effort |
|---|---|---|---|
| **Phase 1** | Backend 2FA management endpoints | `MintPlayer.Spark.Authorization` | Small |
| **Phase 2** | Angular 2FA setup component + service methods | `@mintplayer/ng-spark-auth` | Medium |
| **Phase 3** | Identity Provider MVC 2FA page | `MintPlayer.Spark.IdentityProvider` | Small |
| **Phase 4** | Bypass 2FA for external logins (optional) | `MintPlayer.Spark.Authorization` | Small |
| **Phase 5** | Backend popup callback page | `MintPlayer.Spark.Authorization` | Small |
| **Phase 6** | Angular popup window management | `@mintplayer/ng-spark-auth` | Small |
| **Phase 7** | 2FA inside popup flow | `MintPlayer.Spark.Authorization` | Small |

**Recommended approach:** Phases 1-3 first (core 2FA), then Phases 5-6 (popup login), then Phase 7 (2FA in popup), then Phase 4 (bypass) last.

---

## Security Considerations

1. **TOTP secrets** — Generated by `UserManager.ResetAuthenticatorKeyAsync()`, stored encrypted in RavenDB's `SparkUser.AuthenticatorKey` field
2. **Recovery codes** — 10 single-use codes generated by ASP.NET Core Identity, stored hashed in `SparkUser.TwoFactorRecoveryCodes`
3. **Popup origin validation** — `postMessage` target origin must be the exact app origin, never `"*"`
4. **State cookie** — Already encrypted via `IDataProtectionProvider`; the `Popup` flag does not affect security
5. **Rate limiting** — Consider adding rate limiting to 2FA verification endpoints to prevent brute-force attacks on 6-digit codes
6. **Remember device** — `SignInManager.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient)` sets a device cookie so the user isn't prompted for 2FA on every login from the same browser

---

## Reference: MintPlayer Implementation

The existing MintPlayer project (`C:\Repos\MintPlayer`) serves as the reference implementation:

| Feature | MintPlayer Location |
|---|---|
| 2FA repository methods | `MintPlayer.Data/Repositories/AccountRepository.cs` (lines 398-487) |
| 2FA controller endpoints | `MintPlayer.Web/Server/Controllers/Web/V3/AccountController.cs` |
| 2FA Angular login | `MintPlayer.Web/ClientApp/src/app/pages/account/two-factor/` |
| 2FA profile setup | `MintPlayer.Web/ClientApp/src/app/pages/account/profile/profile.component.ts` |
| Popup base component | `MintPlayer.Web/ClientApp/src/app/components/social-logins/base-login.component.ts` |
| Popup callback view | `MintPlayer.Web/Server/Views/Account/ExternalLoginCallback.cshtml` |
| Bypass2fa property | `MintPlayer.Data/Entities/User.cs` |
| Login result enum | `MintPlayer.Dtos/Enums/eLoginStatus.cs` (Failed=0, Success=1, RequiresTwoFactor=2) |
