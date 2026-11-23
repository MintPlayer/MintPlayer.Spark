# Notifications: one bell UI, and async notifications for anonymous visitors — plan

PRD: [notification_bell_PRD.md](notification_bell_PRD.md). Issue #710. Branch `feat/issue710-notification-bell`,
**one pull request**.

## Status

**2026-11-23:**
- The branch is cut from master `ff66fd0a6`.
- Three read-only investigation passes are done (PRD "Investigation").
- The grill is done: decisions G1–G8 are in the PRD.
  - **G1:** the bell, with a pop-out that reuses its own list rows, not `<bs-toast>`.
  - **G2:** DemoApp proves the anonymous path, and Notifications is decoupled from Authorization now.
  - **G4:** three seams in Abstractions, `ISparkCurrentUser` → `ISparkCurrentPrincipal`. Breaking changes are allowed;
    stored classes are not renamed.
- **NEXT:** spike S1b, then M1.

## Spikes

- **S1b. The socket reconnect after `visitorIssued`.** In a test host, issue the visitor cookie mid-session, send
  `visitorIssued`, then reconnect. Measure two things and keep the numbers in the PRD:
  - the time until both live queries are subscribed again;
  - whether a notification stored right after `EnsureVisitorId()` reaches the open tab.
- **S5. CSRF for an anonymous POST.** Read how the antiforgery scheme treats anonymous callers. A mark-read that relies
  only on the visitor cookie must not be forgeable cross-site. Settle this before M3 changes the endpoint group.

## Milestones

Tests go red first, then green, one class at a time. The full sweep runs once, at the end (CI). Type-check between
milestones.

- **M1. The seams in Abstractions** (R2, G4).
  - Rename `ISparkCurrentUser` → `ISparkCurrentPrincipal` and update its consumers.
  - Add `ISparkAccount` (implemented by `SparkUser`) and `ISparkAccountDirectory` (Authorization registers it).
  - Move the four lifecycle contracts to Abstractions without `TUser`. Authorization invokes them; Organizations,
    Moderation and Notifications migrate.
  - Notifications uses the directory instead of loading `SparkUser`, and drops its Authorization `ProjectReference`.
  - Tests: the existing deletion, export and sign-in handler tests stay green. Startup refuses mail or push
    configuration without a directory.
  - Regenerate the layering goldens if they move.
- **M2. The visitor in core** (R1, G5, G7).
  - `ISparkCurrentPrincipal.VisitorId` and `EnsureVisitorId()`, Data Protection, the companion cookie,
    `Spark:Visitor:Lifetime` and `SessionCookie`, and the `visitorIssued` client operation.
  - Tests:
    - no cookie on a plain visit;
    - issued and renewed by `EnsureVisitorId()`;
    - a tampered or foreign cookie reads as none;
    - session-cookie mode;
    - null for a signed-in caller.
- **M3. Visitor notifications** (R3–R6, R8).
  - `VisitorIds` and `VisitorId`, the visitor branch in `SparkNotifier` (in-app only, `Lifetime` expiry), `OwnRows` on
    user-or-visitor, and mark-read for visitors per S5.
  - Tests:
    - visitor A cannot read, mark or delete visitor B's rows, over HTTP **and** `/spark/live`;
    - users and visitors cannot see each other's rows;
    - without the `@anonymous` binding, a visitor gets 404;
    - `@expires` follows `Lifetime`.
  - Update `CsrfSurfaceTests` and the route snapshot.
- **M4. The claim** (R7). `NotificationVisitorClaim`.
  - Tests for password, passkey and external sign-in: the documents move, both cookies are cleared, a second run is a
    no-op, and the export and deletion include the claimed documents.
- **M5. ng-spark: one store and the list** (R9, R10, R12, R13).
  - Session entries in `SparkNotificationsService`, and `notify` → session entry.
  - Delete `SparkNotificationService` and `<spark-toast-container>`.
  - Extract `<spark-notification-list>`.
  - Session-only mode without `withNotifications()`.
  - The follow rule (signed in, or `spark.visitor.present`) and the reconnect on `visitorIssued` (per S1b).
  - The dev-mode warning.
  - Specs for each.
- **M6. ng-spark: the pop-out** (R11, G8).
  - Connected overlay, at most 3 entries, the timers (errors stay, hovering pauses).
  - No pop-out while the dropdown is open, on the initial load, or on the server.
  - Multi-tab deduplication in `localStorage`.
  - The badge includes session entries; opening the dropdown marks them read.
  - Specs for each.
- **M7. The apps** (R14, G2).
  - CodeCoverage, Fleet, MintPlayer, QnA and SparkId: the bell replaces `<spark-toast-container />`. SeoTester drops
    the container.
  - DemoApp:
    - `AddNotifications()`, `withClientOperations()`, `withNotifications()` and the bell;
    - the `@anonymous` + `@authenticated` binding;
    - a demo action that notifies its visitor after a few seconds.
  - Run `--spark-synchronize-model` (and verify-security) for every model or security change.
  - Move the E2E locators. Regenerate the translation goldens with any `translations.json` change.
- **M8. Docs.**
  - `docs/guide-notifications.md`, the library READMEs and the ng-spark docs.
  - CLAUDE.md's DemoApp note, recording the exception.
  - The release-note text for every break (PRD "Versioning").
- **M9. Gate.** One push, CI green, then the coverage check through the codecoverage MCP.
