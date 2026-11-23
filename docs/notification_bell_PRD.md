# Notifications: one bell UI, and async notifications for anonymous visitors — PRD

Issue [#710](https://github.com/MintPlayer/MintPlayer.Spark/issues/710). Plan:
[notification_bell_plan.md](notification_bell_plan.md). Branch `feat/issue710-notification-bell`, **one pull request**.
It is released before #657 (Formatting, draft PR #709) resumes (#657 PRD D14).

## The problem

#657 starts long-running runs whose result arrives **after** the request has ended. `manager.Client.Notify` only works
during the request. `MintPlayer.Spark.Notifications` delivers stored notifications live, but only to **signed-in**
users. The apps also show them in two different UI systems: the hand-rolled `<spark-toast-container>` and
`<spark-notification-bell>`. This issue makes one notification experience for everyone, signed in or not.

## Decisions already taken (grill of #657, Q12–Q14, owner, 2026-11-23)

- **D1. One UI system: the notification bell with its dropdown.** The separate toast container goes away, so no app
  shows both.
- **D2. A short pop-out anchored to the bell** appears when something arrives.
  - Live notifications land in the dropdown and pop out.
  - In-request `Notify` messages pop out the same way. They stay in the dropdown as **unsaved session entries**: they
    are not stored and are gone after a reload.
  - `<spark-toast-container>` is deleted.
- **D3. Anonymous visitors get the same experience, asynchronous notifications included.**

## Investigation (2026-11-23, master `ff66fd0a6`)

Three read-only passes, one per spike in the issue: S1 visitor-id filtering, S2 Toast, S3 claim on sign-in. I checked
the key claims against the code afterwards. Paths are under `libs/` unless they say otherwise.

### Pass 1 (S1): row filtering and the live socket

- **Row filter.** `NotificationActions.GetRowFilterAsync` → `OwnRows.For(accessor, n => n.UserId)`
  (`notifications/MintPlayer.Spark.Notifications/Actions/NotificationActions.cs:22-23, 57-66`).
  - It builds `UserId == caller` as an expression, so the id reaches RQL as a parameter.
  - A caller with no id gets `_ => false`.
  - The queries only add `IsRead` (`:26, :29`), and there is no dedicated index.
- **The caller.** `NotificationCaller.Id` reads the `NameIdentifier` claim (`Endpoints/NotificationEndpoints.cs:19-22`).
  It is internal to the package; there is no shared "current principal" abstraction.
- **The document has one owner.** `Notification.UserId` is a single string (`Models/Notification.cs:15`). The request
  type `SparkNotification` takes `UserIds` and fans out to one document per user.
- **The live socket keeps the handshake's `HttpContext`.** `LiveConnection.RunAsync` runs its worker in the same
  execution context, so `IHttpContextAccessor` returns the socket's context in every scope
  (`spark/MintPlayer.Spark/Live/LiveConnection.cs:31-33, 88-90`).
  - So the handshake's cookies are readable in `OwnRows`, with no special case per query. This answers S1's question.
  - **Consequence:** a socket opened *before* the visitor cookie was issued never sees that cookie. The client has to
    reconnect the socket once a visitor id exists (**S1b** below).
- **Anonymous callers are refused in three places today:**
  - `App_Data/security.json` grants `QueryRead`/`Delete` on `Notification` only to the slot `notifications:users`.
  - `NotificationsGroup` has `RequireAuthorization()` (`NotificationEndpoints.cs:16`), so mark-read refuses visitors.
  - `OwnRows` returns `false` for a caller with no id.
- **The security schema already has an `@anonymous` token** (`schemas/security.schema.json:21, 75`). An application can
  bind `notifications:users` to it, so no new rights machinery is needed.
- **There is no visitor concept yet.** Data Protection is already used elsewhere (`ModerationIpHasher`,
  `WebhookSecretProtector`, `UserStore.Secrets`).
- **Expiry needs no spike.**
  - `Notification : IExpiring` defaults to `expiresAfter` 90 days.
  - `ExpirationStamper` applies the model default only when `ExpiresAtUtc` is null on a new document
    (`spark/MintPlayer.Spark.RavenDB/Expiration/ExpirationStamper.cs:55-61`), so a visitor document sets its own 24 h.
  - `ExpirationStartup` enables the database sweep.
- **Channels.** `SparkNotifier` loads a `SparkUser` per id and skips unknown ids (`Services/SparkNotifier.cs:52-58`).
  Push goes through the outbox (`:81-97`), mail through `ISparkMailer` (`:99`), and preferences are keyed by user id
  (`:133-141`). Visitors therefore need their own branch: in-app only.
- **Live socket quota.** An anonymous socket is counted per remote IP (`LiveSocket.UserKey`, `LiveSocket.cs:89-92`).
  That is unchanged.

### Pass 2 (S2): ng-bootstrap Toast in ng-spark

- **No new dependency.** `ng-spark` already peer-depends on `@mintplayer/ng-bootstrap ^22.35.0` and `@mintplayer/ui-core`.
  It imports about 30 entry points, `dropdown`, `modal` and `tooltip` among them, but not `toast` yet.
- **The Toast entry point (22.35.0):**
  - `BsToastComponent` (`bs-toast`, model `visible`, `politeness`), `BsToastHeaderComponent`, `BsToastBodyComponent`.
  - `BsToasterComponent` (`queue: ToastQueue`, `placement`). Placement is **fixed viewport corners only**
    (`toast-container position-fixed`, `ui-core/toast/index.mjs:30-41`); there is no anchoring.
- **Zoneless and SSR.** `ToastQueue` uses raw `setTimeout` (enter, autohide 5000 ms, remove) and no `PendingTasks`
  (`ui-core/toast/index.mjs:100-128`).
  - Zoneless it works, because the queue is a `Machine` and `useCore` subscribes.
  - On SSR, a push keeps the render busy for the timeout, so **never pop out on the server**. Today's toast service has
    the same bare `setTimeout` (`client-operations/src/notification.service.ts:32`).
- **Anchoring.** The bell already opens a CDK connected overlay holding `BsDropdownMenuComponent`
  (`notifications/src/spark-notification-bell.component.ts:19-79`). It deliberately avoids ng-bootstrap's dropdown
  directive (README :131-135, ng-bootstrap #419).
  - The pop-out reuses that pattern: a second connected overlay on the bell, holding plain `<bs-toast>`s.
  - It does not use `bs-toaster`, which cannot anchor. `[bsPopover]` is trigger-driven, not a programmatic stack.
- **Where `Notify` goes today.** `client-operations/src/provide.ts:48-55` calls `SparkNotificationService.show(message,
  kind, durationMs)`, and `toast-container.component.ts` renders `toasts()`.
  - The names `SparkNotificationService` (client-operations) and `SparkNotificationsService` (notifications) differ by
    one letter.
- **The apps:**
  - `<spark-toast-container />` is in `app.html` of CodeCoverage, Fleet, MintPlayer, QnA, SeoTester and SparkId (line 3).
  - The bell is only in SeoTester (`shell/shell.component.html:9`) and the template (`libs/templates/.../shell.component.ts:43`).
  - DemoApp and HR use neither (they don't use `client-operations`).
  - E2E locators use the toast container: `tests/MintPlayer.Spark.E2E.Tests/PasskeyCeremonyTests.cs:206` and
    `QnA/QnAContributionsBrowserTests.cs:156`.

### Pass 3 (S3): claiming on sign-in

- **Every fresh sign-in already passes one hook:** `ISparkSignedInHandler<TUser>.OnSignedInAsync(user, ct)`
  (`authorization/MintPlayer.Spark.Authorization/Identity/SparkAccountContracts.cs:107`). It is called from
  `SparkSignInManager.SignInWithClaimsAsync` (`Identity/SparkSignInManager.cs:126-160`), on a fresh sign-in only.
- The paths that reach it:
  - password (`SparkSignInManager.cs:110`);
  - SparkId password, two-factor and recovery code;
  - external login, auto-provisioning and the PWA popup (`ExternalLogin/ExternalLoginCallback.cs:73, :205`);
  - an external link confirmed by email;
  - external login with two-factor;
  - passkeys (`Passkeys/PasskeySignIn.cs:51`);
  - the relying apps (HR, Fleet, QnA), which reach SparkId through `AddOpenIdConnect` and land in
    `ExternalLoginCallback`.
- Refreshes are not fresh sign-ins and do not run it, which is correct here.
- **Precedent.** Organizations registers `PersonalOrganizationOnSignIn<>` with `TryAddEnumerable`
  (`organizations/.../SparkOrganizationsExtensions.cs:76`).
- **The handler can read and clear the cookie.** It injects `IHttpContextAccessor`, and the response has not started.
- **Failures:** a handler error is logged and the sign-in goes ahead (`SparkSignInManager.cs:150-157`). The claim must
  therefore be idempotent, and an unclaimed document still dies by expiry.
- **Cost:** the contract asks for a cheap handler, so the claim only queries when the cookie is present.
- **Export and deletion:** `Identity/NotificationAccountHooks.cs:28-30, 54-56` query `n.UserId == userId`. A claim that
  sets `UserId` (and clears `VisitorId`) is therefore covered with no change.
- **Coupling:** Notifications already references `MintPlayer.Spark.Authorization` directly, so no new dependency.

## Decisions (grill, owner, 2026-11-23)

Every answer was given on 2026-11-23. Breaking changes are explicitly allowed for this PR ("For once, you are allowed
to introduce breaking changes"). Stored classes must not be renamed, because a rename breaks loading documents that
already exist. Interfaces may be renamed.

- **G1. Pop-out shape.**
  - The owner's direction: one UI system, any of the three shapes, implementer's choice. The choice is **the bell**.
  - The pop-out is **not** `<bs-toast>`. It reuses the bell's own list rows (title, a secondary body, a relative time),
    the "nice list" seen on seotester.mintplayer.com.
    - SeoTester has no UI of its own here: its list is ng-spark's bell dropdown (`shell.component.html:9`), and its
      pop-ups are ng-spark's `<spark-toast-container />` (`app.html:3`).
    - The rows move out of the bell's template into `<spark-notification-list>` in ng-spark. The dropdown and the
      pop-out both render it.
  - Pass 2's Toast findings therefore don't apply; only "never pop out on SSR" carries over.
- **G2 (Q1). DemoApp proves the anonymous path, and Notifications is decoupled from Authorization now.**
  - The owner: "We need to decouple the Notifications library from the Authorization library anyway, sooner or later.
    So if we indeed need to do so, we should do it NOW." No app on the VPS has a use for anonymous notifications.
  - DemoApp gets `withClientOperations()`, the bell, `AddNotifications()` (core only) and a demo action that notifies
    its visitor a few seconds later.
  - This is an owner-sanctioned exception to "new features don't land in DemoApp"; CLAUDE.md records it.
  - HR is unchanged.
  - DemoApp's `security.json` already has the anonymous well-known group, enforced by core's `PermissionService`, so it
    binds `"notifications:users": [ "@anonymous", "@authenticated" ]`.
  - **Revised 2026-11-23 (M7): QnA, not DemoApp.** The owner left the choice open ("let's add it to DemoApp or QnA
    perhaps … I'll let you decide"). Evidence against DemoApp: it runs on `spark.UseInMemory(...)` (no database, by
    design), and `MintPlayer.Spark.Notifications` needs RavenDB (`IDocumentStore`/`IAsyncDocumentSession`: 21 uses in 6
    files, from `SparkNotifier` to `NotificationVisitorClaim`). Making DemoApp a RavenDB app would undo its purpose.
    QnA already has RavenDB, Messaging and `withClientOperations()`, and anonymous readers.
    - QnA: `AddNotifications()`, `AddRecipients()`, a `RemindMe` action on a question (rights for both groups) that
      calls `EnsureVisitorId()` when signed out and sends a `QuestionReminderMessage` 5 s later through
      `DelayBroadcastAsync`; its recipient notifies the user or the visitor. Client: `withNotifications()` and
      `withSparkVisitor()`.
    - Bindings name groups by NAME: `"notifications:users": [ "Anonymous visitors", "Signed-in users" ]`. The
      `@anonymous` form above does not compose (startup refuses an undeclared group; seen in `VisitorNotificationTests`).
    - DemoApp is unchanged, so CLAUDE.md needs no DemoApp exception.
- **G3 (Q2).** Superseded by G2: no production app gets anonymous notifications. SeoTester only swaps the UI.
- **G4 (Q5). Decoupling: three seams in `MintPlayer.Spark.Abstractions`.** This is the owner's own idea ("an
  `ISparkSessionPrincipal`-like interface … `SparkUser` implements it … core carries the tools to identify a
  session"), steered toward the patterns already in Abstractions (`ISparkCurrentUser`, `ISparkUserNameResolver`).
  1. **`ISparkCurrentUser` is renamed `ISparkCurrentPrincipal`** and gains `VisitorId` and `EnsureVisitorId()`.
     - `Id` stays the user id. Its eight consumers (Authorization agents, Boards, Contributions, Files, History, Mcp)
       follow the rename.
     - Core `MintPlayer.Spark` implements it: the claim as today, plus the visitor cookie (G5).
     - The visitor is a cookie, never a document. Loading principals through an interface would not work either:
       RavenDB needs the concrete type, and an untyped load comes back as a `JObject`.
  2. **`ISparkAccountDirectory`**: a batched `ResolveAsync(ids)` that returns `{ Email, EmailConfirmed, Culture }` per id.
     - Authorization registers it over `SparkUser`.
     - Notifications takes it as optional: without it, delivery is in-app only, and startup refuses mail or Web Push
       configuration.
  3. **The account lifecycle contracts move to Abstractions and lose `TUser`.** These are `ISparkSignedInHandler`,
     `ISparkAccountDeletionHandler`, `ISparkAccountDeletedHandler` and `ISparkPersonalDataContributor`.
     - They take an `ISparkAccount` (`Id`, `Email`, `EmailConfirmed`, …) that `SparkUser` implements. `SparkUser`
       keeps its name and collection.
     - Every implementer today (Notifications, Organizations, Moderation) reads only `user.Id`, so their migration is
       mechanical.
     - Authorization keeps invoking them.

  Result: Notifications drops its `ProjectReference` to `MintPlayer.Spark.Authorization`.

  **Implementation deviations (2026-11-23, M1):**
  - **`SparkUser` does not implement `ISparkAccount`; an adapter does.** `SparkUser` lives in
    `MintPlayer.Spark.Authorization.Abstractions`, which is documented as "depends on nothing web" and has no reference
    to `MintPlayer.Spark.Abstractions`. Adding one would pull the whole Spark contract surface into the type every store
    document uses. Authorization wraps the user instead: `SparkUserAccount(SparkUser) : ISparkAccount`
    (`Identity/SparkUserAccount.cs`), created where the hooks are invoked (sign-in, deletion, personal data). Nothing is
    renamed or stored differently.
  - `ISparkAccountDirectory.FindAsync(ids)` returns the `ISparkAccount`s by id (case-insensitive), not a separate
    `{ Email, EmailConfirmed, Culture }` record: the same contract the hooks receive. Authorization registers
    `SparkUserAccountDirectory<TUser>` (one batched `LoadAsync`).
  - Without a directory, a user id is taken at its word for in-app only (there is nothing to check it against). With
    one, an unknown id is skipped, as before.
  - **Public base URL.** Notifications read it from `SparkAuthenticationOptions.Links.PublicBaseUrl`. It now has its own
    `Spark:Notifications:PublicBaseUrl`, falling back to `Spark:Auth:PublicBaseUrl` through the shared
    `SparkPublicBaseUrl.Resolve` (the pattern of share links and Mcp). An app that set only the Auth key is unchanged.
  - **Startup** refuses Web Push configuration without a directory. Mail needs no refusal: with no directory there is
    no address to mail, and the mail channel is already optional.
- **G5 (Q3). Cookie lifetime.**
  - `Spark:Visitor:Lifetime` (default 24 h, sliding) sets both the cookie and the expiry of visitor notifications.
    Every `EnsureVisitorId()` renews it.
  - `Spark:Visitor:SessionCookie: true` makes the cookie a browser-session cookie; the documents still expire after
    `Lifetime`.
- **G6 (Q4). Server-package detection.** No new flag and no probe: `withNotifications()` already exists
  (`notifications/src/spark-notification.ts:59`).
  - With it, the bell follows the server lists.
  - Without it (CodeCoverage, Fleet, MintPlayer, QnA, SparkId), the bell shows session entries only: no server count,
    no push toggle, no "all notifications" link.
- **G7 (Q6). How the client knows a visitor exists.**
  - A readable companion cookie, `spark.visitor.present=1`, carries no id and has the same expiry. It is set and cleared
    together with `spark.visitor`.
  - The service follows the live queries when signed in **or** when that cookie is present.
  - The tab that is already open learns that the cookie appeared and reconnects the socket, because the socket keeps its
    handshake's `HttpContext` (Pass 1), then follows.
  - **Refined in implementation (2026-11-23): an HTTP interceptor, not a `visitorIssued` client operation.** Client
    operations ride only on Spark's own responses (actions, persistent objects). An app endpoint that calls
    `EnsureVisitorId()` would issue the cookie with no operation to carry the news. An ng-spark HTTP interceptor
    checks `document.cookie` for `spark.visitor.present` after every response instead, and reconnects once when it
    goes from absent to present. It works for any endpoint, and the server needs no new operation.
- **G8 (Q7). Pop-out behaviour.**
  - At most 3 entries; the oldest leaves when a fourth arrives.
  - Each entry hides after its `durationMs` (a `Notify`) or after 6 s (a live notification). Errors stay until
    dismissed, and hovering pauses the timer.
  - Nothing pops out while the dropdown is open, and nothing pops out on the server render.
  - The badge counts unread server notifications plus unread session entries. Opening the dropdown marks the session
    entries read.
  - The guide explains why the badge can drop after a reload: session entries are gone by definition.

## Design

### Core (`MintPlayer.Spark.Abstractions`, `MintPlayer.Spark`)

- **R1. The visitor id** (G4.1, G5, G7). `ISparkCurrentPrincipal.EnsureVisitorId()` issues the visitor cookie when it is
  absent and returns the id. A signed-in caller gets null and addresses the user instead.
  - **The cookie, `spark.visitor`:**
    - a random 128-bit id, protected with Data Protection (purpose `MintPlayer.Spark.Visitor`);
    - `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, with a sliding `Spark:Visitor:Lifetime`;
    - a tampered or foreign cookie reads as "no visitor".
  - **The companion `spark.visitor.present=1`:** readable, no id, the same expiry.
  - **Issued only by `EnsureVisitorId()`.** A plain visit never gets a cookie.
  - The client notices the presence cookie after the response (G7, refined): no client operation.
  - **Consent:** the cookie is strictly necessary for a service the visitor asked for, so no banner. The guide writes
    this down.
- **R2. The account seams** (G4.2, G4.3): `ISparkAccount`, `ISparkAccountDirectory` and the four lifecycle contracts
  without `TUser`, in Abstractions.
  - Authorization implements and invokes them.
  - Organizations and Moderation migrate.

### Server: `MintPlayer.Spark.Notifications` (no Authorization reference)

- **R3. Addressing.**
  - `SparkNotification` gains `VisitorIds` beside `UserIds`.
  - `Notification` gains `VisitorId`: one owner per document, like `UserId`, and exactly one of the two is set.
  - Visitor ids are stored as `visitor:<id>`, so they can never equal a user id.
  - There is no API to enumerate or address another visitor: an app only has the id `EnsureVisitorId()` returned in
    the request that started the work.
- **R4. Row filter.** `OwnRows` becomes "`UserId == me` or `VisitorId == my visitor`", read from
  `ISparkCurrentPrincipal`.
  - Each side is present only when that identity exists, and both values go in as parameters.
  - A caller with no identity still gets `_ => false`.
  - `PushSubscription` and `NotificationPreference` stay user-only.
- **R5. Expiry.** A visitor document gets `ExpiresAtUtc = now + Spark:Visitor:Lifetime`. The stamper keeps an explicit
  value.
- **R6. Channels.** Visitors get in-app only. Mail and Web Push need a user and the `ISparkAccountDirectory`; without
  the directory, delivery is in-app only.
- **R7. Claim on sign-in.** A `NotificationVisitorClaim : ISparkSignedInHandler`. When a visitor id is present, it:
  1. patches every `VisitorId == me` document to `UserId = account.Id, VisitorId = null`, and resets the expiry to the
     model default;
  2. clears both cookies.

  It is idempotent, and a no-op without a visitor. Export and deletion then cover the claimed documents (Pass 3).
- **R8. Rights and endpoints.**
  - The library's `security.json` is unchanged; an app binds `notifications:users` to `@anonymous` to let visitors in.
  - `NotificationsGroup` drops `RequireAuthorization()`. Mark-read authorizes inside: a caller with neither identity
    gets 401. The push endpoints still require a user.
  - This touches `CsrfSurfaceTests` and the route snapshot.

### Client: `@mintplayer/ng-spark`

- **R9. One store.** `SparkNotificationsService` holds:
  - the live server lists, when `withNotifications()` is on (G6);
  - the session entries: `Notify` messages, in memory, with `kind`;
  - the pop-out queue.

  The `notify` handler in `client-operations` pushes a session entry. `SparkNotificationService` (client-operations)
  and `<spark-toast-container>` are deleted.
- **R10. `<spark-notification-list>`.**
  - The rows extracted from the bell, used by the dropdown and by the pop-out (G1).
  - Session entries show a `kind` accent: a colour bar or an icon via `<spark-icon>`, never `bi bi-` classes.
- **R11. The pop-out** (G8).
  - A second CDK connected overlay on the bell.
  - `aria-live`, and clicking an entry opens it.
  - A live notification pops out when it **arrives**, never on the initial load.
  - Across several tabs, a live notification pops out once: each tab records the ids it has shown in `localStorage`,
    wrapped in try/catch.
- **R12. Following as a visitor** (G7): follow the live queries when signed in or when `spark.visitor.present` is set.
  When an HTTP response leaves the presence cookie newly set, reconnect the socket (`SparkLiveService.reconnect()`),
  then follow.
- **R13. Missing bell.** In dev mode, a `Notify` that arrives with no bell rendered logs a console warning.
- **R14. The apps.**
  - CodeCoverage, Fleet, MintPlayer, QnA and SparkId replace `<spark-toast-container />` with the bell in their
    header.
  - SeoTester drops the container; it already has the bell.
  - DemoApp per G2. The template is already right.
  - The E2E locators in `PasskeyCeremonyTests.cs:206` and `QnAContributionsBrowserTests.cs:156` move to the pop-out.

## Spikes

| Id | Question | Status |
|---|---|---|
| S1 | Can the live row filter key off a cookie-derived visitor id with no per-query special case? | **Answered by reading** (Pass 1). To prove with a red test in M2, over HTTP and `/spark/live`. |
| S1b | Does the presence cookie appearing → socket reconnect resume the subscriptions, and does a notification stored right after `EnsureVisitorId()` reach the open tab? | **Mechanism answered by reading (2026-11-23).** `SparkLiveService.open()` resubscribes every entry in `onopen` (`spark-live.service.ts:283-286`), and every entry's state resets on close (`:305`). A public `reconnect()` (close, then open at once, no backoff) therefore gives a new handshake carrying the new cookie, plus fresh snapshots. A notification stored before the resubscribe is in the snapshot, and one stored after it is pushed. **Proven in the browser on QnA (2026-11-23, Chrome, signed out, no cookie before):** click on RemindMe → new socket after **136 ms** (the interceptor saw `spark.visitor.present`), cookie present by 205 ms, the stored "Reminder" popped out of the bell at **15.8 s**. After a reload: badge 1, the reminder in the list, the session entry gone, no pop-out. The 15.8 s is Messaging, not the socket: a delayed message is woken by `MessageRetrySweeper` every `FallbackPollInterval` (30 s), so it arrives between the 5 s delay and about 35 s; the demo says "shortly". Found on the way: `ng-spark/auth` holds `{ isAuthenticated: false }` for an anonymous visitor, which is truthy, so the service asked for the push key and its 401 sent anonymous readers to the login page. Fixed with `sparkSignedIn(state)` beside `SPARK_AUTH_STATE`, with two specs. |
| S2 | ng-bootstrap Toast in ng-spark. | **Dropped** (G1). Remaining check: `<spark-notification-list>` in a connected overlay renders zoneless, and no timer runs on the server. |
| S3 | Does the claim hook cover every sign-in? | **Answered** (Pass 3). To prove for password, passkey and external login in M4. |
| S4 | How does the bell know whether the server package is there? | **Answered** (G6): `withNotifications()`. |
| S5 | Does the antiforgery/CSRF scheme protect an anonymous mark-read POST that relies on the visitor cookie? | **Answered (2026-11-23): no, today it would not.** `SparkAntiforgeryMiddleware.HasAmbientCredential` (`:154-164`) returns false for an unauthenticated principal, so the default gate skips the check. The visitor cookie *is* an ambient credential. **Fix (M2):** `HasAmbientCredential` also returns true when the request carries `spark.visitor`. The XSRF-TOKEN cookie is minted on every response, anonymous ones included (`SparkMiddleware.cs:343-361`, `UseAntiforgeryGenerator`), so a visitor's client can echo it. `SameSite=Lax` on the visitor cookie is the second layer. |

## Acceptance (from the issue, plus G2/G4)

- [ ] An anonymous visitor who starts a long-running action gets its result in the bell, after a reload too, within
      `Spark:Visitor:Lifetime`. Proven in QnA (G2 revision).
- [ ] A signed-in user's notifications behave as today, plus the pop-out.
- [ ] `Notify` messages pop out of the bell in every app, and `<spark-toast-container>` no longer exists.
- [ ] Signing in claims the visitor's notifications.
- [ ] `MintPlayer.Spark.Notifications` has no reference to `MintPlayer.Spark.Authorization`.
- [ ] Docs:
  - `docs/guide-notifications.md`: visitors, the cookies and consent, the binding by group name, the claim, the
    pop-out, the badge after a reload;
  - the library READMEs;
  - the ng-spark docs;
  - the release notes (`docs/release-notes-710.md`). CLAUDE.md needs no DemoApp note (G2 revision).
- [ ] Tests:
  - visitor row filtering with no cross-visitor reads (HTTP and live);
  - the cookie: issued only on `EnsureVisitorId()`, tampered or foreign rejected, session-cookie mode;
  - expiry;
  - the claim;
  - in-app only without the directory;
  - ng-spark specs for the list, the bell, the pop-out and the follow rule;
  - `CsrfSurfaceTests` and the route snapshot.
- [ ] Layering and translation goldens regenerated in the same commit as the change that moves them.

## Versioning

Breaking changes are allowed for this PR (owner, 2026-11-23). Each still ships as a **minor** inside its platform
generation, with the break described in the release notes:

- **NuGet (.NET 11):**
  - `ISparkCurrentUser` → `ISparkCurrentPrincipal`;
  - the lifecycle contracts move to Abstractions and lose `TUser`;
  - Notifications drops its Authorization dependency, and `AddNotifications()` changes accordingly.
  - Affected packages: Abstractions, Spark, Authorization, Organizations, Moderation, Notifications, and every package
    that consumed `ISparkCurrentUser`.
- **npm (Angular 22):** `@mintplayer/ng-spark` loses `<spark-toast-container>` and `SparkNotificationService`, and gains
  `<spark-notification-list>`.

The versions come from the engine; nobody edits them by hand.
