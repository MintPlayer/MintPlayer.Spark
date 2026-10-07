# Plan — The passkeys page on Spark's generic pages, and client-method retries

PRD: [generic_passkeys_page_PRD.md](generic_passkeys_page_PRD.md). One PR. Test suites run only
in M8. Milestones are verified by reading the code and type-checking.

## Status

| Milestone | State |
|---|---|
| M0 Spikes S1–S5 | ✅ 2026-10-06, PRD §9 |
| M1 Client-method retry: server | ✅ 2026-10-06 (tests written, run in M8) |
| M2 Client-method retry: client | ✅ 2026-10-06 (specs written, run in M8) |
| M2a `paragraph` renderer (PRD D5, grill Q8 = B) — missing from the original plan | ✅ 2026-10-06 |
| M2b `requiresClient` on actions (PRD §5 O5, grill Q9 = C) — missing from the original plan | ✅ 2026-10-06 |
| M3 `Passkeys` PO + `my-passkeys` query | ✅ 2026-10-06 (tests written, run in M8) |
| M4 Add / Rename / Remove actions | ✅ 2026-10-06 (tests and specs written, run in M8) |
| M5 App wiring (CodeCoverage, Fleet, HR) | ✅ 2026-10-06 (gates regenerated; specs run in M8) |
| M6 Retire component and endpoints, plus the retry modal's translated Cancel | ✅ 2026-10-06 (tests, specs and E2E written, run in M8; PRD §10b, M6) |
| M7 Docs and versions | ✅ 2026-10-07 (client-method retries, `requiresClient`, `paragraph` renderer, passkeys page docs; bumps shared with composition M10) |
| M8 Sweep | ⏳ |

Owner decisions O1, O3, O4 and O5 (PRD §5) must be settled before M3. O2 is decided: remove the
endpoints, with no backward compatibility.

## M0 — Spikes

- **S4**: run CodeCoverage with `dotnet run --launch-profile https`. With `playwright_node` at
  375 px, screenshot `/account/passkeys` and `/po/forge-accounts/github`. Record which one clips
  and which one scrolls.
- **S2**: put a throw-away `Custom.X` and a virtual-type Actions class in the Authorization
  assembly, and check that `ActionsResolver` and the query source find them.
- **S1, S3, S5**: answer them with a throw-away action in Fleet. Record the results in PRD §9,
  "Spike results".

## M1 — Client-method retry: server

- `Abstractions/Retry`:
  - `RetryOperation.ClientMethod` and `Arguments`
  - `RetryResult.Value`
  - `IRetryAccessor.Invoke(clientMethod, Func<Task<object?>> arguments)`, where the arguments
    factory runs only for an unanswered step (PRD D7 trap)
- `Services/RetryAccessor.cs` and `ClientAccessor.PushRetry`: carry the new fields.
- JSON schema for `RetryOperation`, if it is covered.
- Unit tests, written now and run in M8:
  - `Invoke` raises a 449 that carries the method name and arguments.
  - Pass 2 exposes `Result.Value`.
  - Cancel is reported as `Option == "Cancel"`.
  - On an answered step the factory is **not** invoked.
  - Three mixed steps in one action (`Invoke`, then `Action`, then `Invoke`) round-trip in order.

## M2 — Client-method retry: client (`@mintplayer/ng-spark`)

- `models/src/retry-action.ts`: add the fields.
- `client-operations/src`: add `SPARK_CLIENT_METHODS` and `provideSparkClientMethods`.
- `services/src/spark.service.ts` retry path:
  - `await` the registered method.
  - An unknown method or a rejection resolves as Cancel.
- Specs:
  - dispatch
  - unknown name
  - rejection
  - the depth cap still holds

## M2a — `paragraph` renderer (PRD D5)

- `ng-spark/renderers`: `SparkParagraphRendererComponent`, shipped as a core renderer
  (`sparkCoreRenderers`, available without registration; an app's own `paragraph` wins).
- Registration flag `fullWidth`: the detail page drops the label and spans the row.
- Escaped by default; `rendererOptions.sanitize === false` → `[innerHTML]`, never
  `bypassSecurityTrustHtml`.
- Specs: escaping, line breaks, the innerHTML path stripping `<script>` / `onerror`, core registration.

## M2b — `requiresClient` (PRD §5 O5 / Q9)

- `actions.json` property `requiresClient` (loader `KnownProperties`, `ActionsFileEntry` → schema),
  carried by `/spark/actions/list` (omitted when null) and the .NET client's `SparkCustomAction`.
- Client: `SparkClientMethodRegistry.unavailableReason(name)`; the grid toolbar, query card,
  query-list page, row menu and the detail page's action bar disable the action and show the
  translated reason (`common.clientUnsupported`, or the method's own `unsupportedReason`) as `title`.
- Tests/specs: listing carries it; grid disables / leaves the rule in charge / inert row-menu item.

## M3 — `Passkeys` PO and the `my-passkeys` query (Authorization library)

- `PasskeysActions.OnLoadAsync`:
  - resolves the current user and ignores the id
  - returns 404 when the user is anonymous or passkeys are disabled
  - fills `Description` and the breadcrumb from translations
- `PasskeyRowActions.MyPasskeys(CustomQueryArgs)`: maps `SparkUser.Passkeys` to rows with Name,
  Created (`DateTimeOffset`) and Synced.
- ~~Reference copies of `Passkeys.json` and `PasskeyRow.json` in the library (O1).~~ Superseded by the
  composition system: `Passkeys.json`, `PasskeyRow.json` and `PasskeyRename.json` **ship** from the
  library's `App_Data/Model/` with UUIDv5 ids (`npm run stamp:library-model-ids`, SPARK045 verifies);
  no app keeps a copy.
- Add translation keys as needed (the library's `translations.json`, en/fr/nl).

## M4 — Add / Rename / Remove actions

- `AddPasskey` (PRD D3): two passes, sharing ceremony state per S1.
- `RenamePasskey`: prompts with a retry PO form, `Sanitize` to 64 characters.
- `RemovePasskey`: asks for confirmation, then refuses the last credential through
  `SparkCredentialInventory`.
- `ng-spark-auth` registers `webauthn.create` with `provideSparkClientMethods`, reusing
  `parseCreationOptionsFromJSON`, the `passkeysSupported()` check and the `passkeyError` mapping.
- Port the logic and the tests from `PasskeyManagementTests` / `PasskeyEndpointTests` to the
  actions. Add the cross-user isolation test.

## M5 — App wiring

For CodeCoverage, Fleet and HR (and QnA, which references the library too):
- ~~the model JSON (or the O1 alternative)~~ — shipped by the library (composition D6).
- ~~`security.json` grants~~ — the library's `App_Data/security.json` grants Read and `AddPasskey` on
  `Passkeys`, Query, `RenamePasskey` and `RemovePasskey` on `PasskeyRow`, to `@authenticated` (D4); the
  apps state nothing.
- ~~the sync test from O1~~ — obsolete: there are no copies to keep in sync.
- Regenerate `modelHashes.json` / `securityPosture.txt` where the new library layers change them, and
  check that the posture diff is exactly the five library grants.

Routing:
- `withAccount()` maps `account/passkeys` as a redirect to `/po/passkeys/me`.
- CodeCoverage's `/passkeys` redirect.
- `app.routes.spec.ts`.

## M6 — Retire the hand-written component and endpoints (per O2)

- Delete:
  - `ng-spark-auth/passkeys/` (the component, its spec, the entry point)
  - the `SparkAuthService` management methods and `spark-auth.passkeys.spec.ts` cases
  - the five endpoints, and `PasskeyEndpoints.cs` mappings for them
- Update `spark-auth-routes.account.spec.ts`.
- Rewrite `tests/MintPlayer.Spark.E2E.Tests/PasskeyCeremonyTests.cs` for the generic page:
  - action-bar "Add a passkey"
  - the row action menu
  - assertions through the generic query instead of `GET /spark/auth/passkeys`

## M7 — Docs and versions

- Make minor bumps only (PRD D9): `@mintplayer/ng-spark`, `@mintplayer/ng-spark-auth`, and the
  NuGet packages touched.
- Document client-method retries next to the retry docs, with WebAuthn as the worked example.
- Update `docs/code-coverage/` if it mentions the passkeys page.

## M8 — Sweep

- `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`,
  with its log written raw to a file.
- Use `playwright_node` to check the acceptance list (PRD §7) on CodeCoverage at 375 px and at
  desktop width.
- Manual browser checks (`playwright_node`), added in M6:
  - CodeCoverage `/po/forge-accounts/github`: composition M8 changed how its forge is chosen, and no
    E2E covers that page. It must show the GitHub account, not another forge's or an empty page.
  - The passkeys page at 375 px (PRD acceptance): the action bar's "Add a passkey", the grid's row
    menu (Rename opens the `PasskeyRename` form, Remove asks first) and the Rename modal's footer
    (Save and the translated Cancel), with no horizontal scroll. Check the Cancel in nl or fr too.
