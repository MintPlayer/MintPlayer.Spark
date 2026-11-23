# Notifications: one bell UI, and async notifications for anonymous visitors — plan

PRD: [notification_bell_PRD.md](notification_bell_PRD.md). Issue #710. Branch `feat/issue710-notification-bell`,
**one pull request**.

## Status

**2026-11-23:**
- The branch is cut from master `ff66fd0a6`.
- Three read-only investigation passes are done (PRD "Investigation"). S1, S2 and S3 are answered by reading; S1b and
  S4 are open.
- Design R1–R14 is proposed. Q1–Q4 await the owner.
- **NEXT:** grill Q1–Q4, then S1b and S4, then M1.

## Spikes (before M1)

- **S1b. Socket reconnect after the cookie is issued.**
  - Add a throwaway `visitorIssued` client operation in a test host.
  - The live client reconnects on it.
  - Measure: time to resubscribe, and whether a notification stored right after `Ensure()` reaches the open page. Keep
    the measurement in the PRD.
- **S4. Server-package detection.** Pick between a provider flag, a probe and a capabilities field (Q4). Write down the
  choice and its evidence.
- **S2 proof.** A zoneless vitest spec with `<bs-toast>` in a CDK connected overlay anchored to a host element. Check
  that no `setTimeout` runs under `isPlatformServer`.

## Milestones

Tests go red first, then green, one class at a time. The full sweep runs once, at the end (CI).

- **M1. Visitor identity and addressing (server).**
  - `ISparkVisitor` (R1).
  - `SparkNotification.VisitorIds` and `Notification.VisitorId` (R2).
  - The visitor branch in `SparkNotifier`: in-app only, 24 h expiry (R4, R5).
  - Tests: the cookie is issued only by `Ensure()`; a forged or tampered cookie is ignored; ids are prefixed; no mail
    and no push for a visitor; `@expires` is 24 h.
- **M2. Reading as a visitor (server).**
  - `OwnRows` gains user-or-visitor (R3), and `NotificationCaller` gains the visitor id.
  - `NotificationsGroup` authorizes inside the endpoints (R8).
  - Tests: visitor A cannot read, mark or delete visitor B's notifications over HTTP **and** over `/spark/live`. A
    user cannot see visitor rows, and the reverse. Without the `@anonymous` binding a visitor gets 404.
  - Update `CsrfSurfaceTests` and the route snapshot.
- **M3. Claim on sign-in (server).**
  - `NotificationVisitorClaim<TUser>` (R6).
  - Tests for password, passkey and external sign-in: the documents move, the cookie is cleared, a second run is a
    no-op, and the personal-data export and account deletion cover the claimed documents.
- **M4. One store and the bell's session mode (ng-spark).**
  - Session entries in `SparkNotificationsService` (R9).
  - `client-operations` `Notify` → session entry.
  - Delete `SparkNotificationService` and `<spark-toast-container>`.
  - Server-package detection per S4 (R10).
  - Specs: session entries, the session-only bell, and the dev-mode warning (R13).
- **M5. The pop-out (ng-spark).**
  - Connected overlay with `<bs-toast>` (R11).
  - A live notification pops out on arrival but not on the initial load.
  - Multi-tab deduplication through `localStorage`, with try/catch.
  - Nothing pops out on SSR.
  - The socket reconnects on `visitorIssued` (R12, per S1b).
  - Specs for each.
- **M6. Apps.**
  - Replace `<spark-toast-container />` with the bell in the header of CodeCoverage, Fleet, MintPlayer, QnA and
    SparkId; drop it from SeoTester (R14).
  - Q2's production wiring, if chosen.
  - Move the E2E locators in `PasskeyCeremonyTests` and `QnAContributionsBrowserTests` to the pop-out.
  - Regenerate the translation goldens if a `translations.json` changed.
- **M7. Docs.**
  - `docs/guide-notifications.md`: visitors, the cookie and its consent position, the `@anonymous` binding, claiming,
    and the pop-out.
  - The library README and the ng-spark README / docs.
  - The release-note text for the ng-spark break.
- **M8. Gate.** One push, CI green, then the coverage check through the codecoverage MCP.
