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
- S5 answered and fixed (the visitor cookie is an ambient credential). S1b's mechanism answered by reading; its browser
  proof is in M7.
- **M2 (server) done:** `SparkVisitorCookies`, `SparkVisitorOptions`, the principal's `VisitorId` / `EnsureVisitorId()` /
  `ForgetVisitor()`, with tests in `SparkCurrentPrincipalTests`. The client half (presence-cookie interceptor) is in M5.
- **M1 nearly done:** rename, `ISparkAccount` / `ISparkAccountDirectory`, the four hooks non-generic in Abstractions,
  `SparkUserAccount` adapter (PRD G4 deviations), every implementer migrated, `SparkNotifier` on the directory with
  its own `PublicBaseUrl`, Authorization reference gone from Notifications. Left: the tests that implement the old
  generic hooks, the READMEs/guides, a solution build.
- The visitor branch of `SparkNotifier` (`VisitorIds`, `Notification.VisitorId`) landed with M1 (part of M3).
- **Later 2026-11-23:**
  - **M1 done** (tests migrated, READMEs/guides, build). **M3 + M4 done:** `VisitorNotificationTests` 10/10 green
    (isolation over HTTP and `/spark/live`, 24 h expiry, mark-read with XSRF, push 401 for visitors, the claim).
    `RouteTableSnapshotTests` + antiforgery tests 46/46 green, unchanged. An anonymous denial is the uniform 401 (not 404).
  - **M5 + M6 done (client):** `SparkSessionNotifications` replaces the toast service; `SparkLiveService.reconnect()`;
    `SparkVisitorPresence` + `withSparkVisitor()` (services); `SparkNotificationsOptions.server`; merged `entries`,
    combined `unreadCount`, `arrived`; `<spark-notification-list>`; the bell's pop-out. Specs: 111/111 green in the
    affected files.
  - **M7 in progress:** the toast container is gone from all six apps; the bell is in every shell (SeoTester shows it
    signed out too). **The anonymous demo moved to QnA** (PRD G2 revision: DemoApp has no database). E2E locators moved
    to `.spark-notification-popout`. QnA: build, `--spark-synchronize-model`, `--spark-synchronize-security` and
    `--spark-verify-security` exit 0. The posture adds `RemindMe/Question` and the five `notifications` rights for
    anonymous visitors. Preferences and devices stay closed to anonymous callers (`OwnRows`, the New interceptor). Goldens
    `qna-actions` and `qna-translations` regenerated; the golden, `NotificationTests` and `VisitorNotificationTests`
    classes: 47/47 green. A client edit of a `Notification` now keeps its stored `VisitorId` too.
  - **M8 done:** `guide-notifications.md` (decoupling, "Anonymous visitors", the bell and pop-out), the Notifications
    and ng-spark READMEs, `docs/release-notes-710.md`.
  - Angular build of ng-spark + the 8 apps: green; ng-spark specs: green.
  - **S1b proven on QnA** (PRD S1b row: reconnect 136 ms, reminder at 15.8 s, survives a reload). Fixed on the way:
    `sparkSignedIn()` (a signed-out `{ isAuthenticated: false }` was read as signed in, and the push-key 401 redirected
    anonymous readers to login); the reminder's body is the question's title, not the type name.
- **NEXT:** re-run the ng-spark notifications spec, commit, push, PR (ready), CI, coverage (M9).

## Spikes

- **S1b. The socket reconnect after the presence cookie appears** (PRD G7, refined: an HTTP interceptor, no
  `visitorIssued` operation). In QnA (`RemindMe` signed out), issue the visitor cookie mid-session, then reconnect. Measure two things and keep the numbers in the PRD:
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
    `Spark:Visitor:Lifetime` and `SessionCookie`. (No client operation: G7 refined.)
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
  - The follow rule (signed in, or `spark.visitor.present`) and the HTTP interceptor that reconnects when the presence cookie appears (per S1b).
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
  - QnA (not DemoApp: PRD G2 revision):
    - `AddNotifications()`, `withNotifications()`, `withSparkVisitor()`;
    - `"notifications:users": [ "Anonymous visitors", "Signed-in users" ]`;
    - the `RemindMe` action: notifies its user or visitor 5 s later (a delayed message).
  - Run `--spark-synchronize-model` (and verify-security) for every model or security change.
  - Move the E2E locators. Regenerate the translation goldens with any `translations.json` change.
- **M8. Docs.**
  - `docs/guide-notifications.md`, the library READMEs and the ng-spark docs.
  - The release-note text for every break (PRD "Versioning").
- **M9. Gate.** One push, CI green, then the coverage check through the codecoverage MCP.
