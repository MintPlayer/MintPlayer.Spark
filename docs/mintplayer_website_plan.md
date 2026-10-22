# MintPlayer website on Spark: plan

PRD: [mintplayer_website_PRD.md](mintplayer_website_PRD.md). Everything lands in **one PR** from branch
`feat/mintplayer-website`. Stage 0 is deployed from that branch's image before the PR merges, if needed, so
the site can go live today. Test runs are batched at the end of each stage, never per step.

Status (2026-10-22): 0.2/0.3 committed (4b642c39, 3450c067). 0.4 migration committed (2c3df51b), its 50 tests green. The local run into `MintPlayer` reconciled GREEN, and the old local `MintPlayer`, `MintPlayer_Migration` and `MintPlayer_Cutover` were dropped (owner-allowed). **Production `MintPlayer` imported (owner-approved, 2026-10-22):** the database was created on coverage-raven with the operator cert, and the dump was imported through `/smuggler/import` (operation Completed). Verified 1180 documents and 776 `emails/` reservations, then the dump was deleted from the VPS. There are no indexes yet; the app deploys them at first start. Still open for 0.7: the `mintplayer` client cert (registered for `MintPlayer` only), the shared network in the CodeCoverage compose, the `raven-cert-renew` extension (it knows only codecoverage and seotester today), then a CodeCoverage redeploy. Next: the login checks, mail in dev, and S1. **0.5 done (2026-10-22):** per G6 the song page is the generic `/po/song/...` page (title, artists, lyrics, media play buttons into the G7 queue), and home lists the newest songs. Detail URLs carry the breadcrumb as a slug through the new opt-in `sparkRoutes({ slugs: true })` (ng-spark 22.43.0), and `LegacyRedirects.cs` 301s `/song|artist|person/{id}[/{name}]` and `/amp/song/{id}` there, keeping the name as the slug. Verified in the browser: a missing or stale slug is corrected in place with query parameters kept, and Edit still opens `/edit`. The queue runs on `@mintplayer/playlist-controller` (ba5bfbb9). Checked per provider: YouTube and SoundCloud autoplay and step, on a fresh player and on a reused one. Vimeo works for a real video; the test id 76979871 is a 404 at Vimeo. ⚠️ Dailymotion does not play: the plugin frames `https://www.dailymotion.com/embed?...` with no video id, and Dailymotion refuses it (`frame-ancestors 'none'`). This is in `@mintplayer/dailymotion-player`, not the app.

### Log

Update this section at every step; it is what survives a context compaction.

- **Existing local Raven data checked, not reused (2026-10-22).** localhost:8080 holds two old-branch databases:
  - `MintPlayer_Migration`: 1156 docs, written 2026-09-27 by the old branch's migration.
    - Subjects match the snapshot exactly: 141 songs + 145 artists + 15 people = 301 `Subjects` rows. Tags 51, categories 8, medium types 15, playlists 16 and blog posts 10 also match.
    - Users: 752 against the snapshot's 776, so 24 accounts were registered after it was taken.
    - It is in the **Spark 10 shape**: users sit in a `MintPlayerUsers` collection with CLR type `MintPlayer.Web.MintPlayerUser` and the fields `Bypass2faForExternalLogin` and `PictureUrl`; embedded rows (`Media`, `Artists`, `Tracks`, `Members`) have no `Id`; entities carry `OldId` and the hand-written `IsDeleted`/`DeletedAt`/`CreatedAt`/`ModifiedAt`.
  - `MintPlayer`: 347 docs and 3 users, a dev database. Ignore it.
  - **Decision:** port `MintPlayer.Migration` (step 0.4) and run it on `MintPlayer_Snapshot_20261022` (G12), writing the current shape. Embedded rows get explicit ids, because the generated `[ValueObject]` key mints a new GUID on every load, which dirties each document. Users go into `SparkUsers`. `MintPlayer_Migration` is kept only as a reconciliation reference: the new run's counts and ids are diffed against it.
  - The snapshot's SQL schema is `mintplay.*`, not `dbo`. Row counts: AspNetUserLogins 34, AspNetUserTokens 15, Likes 156, Lyrics 145, Media 507, PlaylistSong 116, SubjectTag 132.
- **0.2 scaffold and 0.3 domain, server side (2026-10-22), uncommitted.**
  - `apps/MintPlayer/MintPlayer.Library` holds the entities (`MintPlayer.Entities`). `apps/MintPlayer/MintPlayer` is the host, on https 5015 and http 5016. Both build. `OldId` was dropped.
  - Actions: the catalog types own row security (`ISparkOwnsRowSecurity`). `PlaylistActions`: everyone reads public playlists, the owner reads and edits their own, and an admin everything. `OwnerId` is stamped on create.
  - Model: the legacy customizations were ported onto the generated JSON (`scratchpad/model_patch.js`). That covers the Person breadcrumb `{FirstName} {LastName}`, Lyrics as MultiLineString, LyricsTimings hidden, `OwnerId` hidden and read-only, Tracks inline, the renderers `media-player` (Medium.Value) and `color-swatch` (TagCategory.Color), the legacy sort columns instead of CreatedBy, and the sub-queries Person_Artists, Artist_Songs, Category_Tags, Tag_Children, My_Playlists and Public_Playlists. A re-sync keeps all of them.
  - `security.json` (keys `5e15…`): groups Anonymous, Signed-in, Administrator, Blogger and Editor, the legacy role names. Anonymous gets QueryRead on the catalog and on Playlist/PlaylistTrack (the row filter limits that to public playlists). Editor gets QueryReadEditNewDelete on Person, Artist, Song and the rows. Signed-in users get QueryReadEditNewDelete on Playlist. Administrator gets everything, plus Restore/Purge/ViewDeleted/History/Revert. Posture: SparkUser is not reachable anonymously.
  - `programUnits.json`: the legacy Music, Playlists and Catalog groups. `translations.json` has full en/nl/fr labels for every type, attribute, query, group and program unit.
  - Indexes: `Person` has `[GenerateIndex]` and a `[Search]` computed `FullName`, so its list runs on the generated `People_Overview` (breadcrumb `{FullName}`, FullName shown in the list only, labelled Name/Naam/Nom). `Subjects_Search` is a multi-map over Artist, Person and Song with search and suggestions on `Text`, for the search endpoint. `Likes_Count` is a fan-out map-reduce over `UserLike`. `Songs_ByYouTubeId` normalises media URLs with `YouTube/YouTubeUrl.cs` (from the old `feature/spark-migration` branch). Every map filters `IsDeleted != true` (SPARK022). `Subject.Media` is `[IgnoreForIndex]` (SPARK_INDEX_010).
  - Generator fix: `[GenerateIndex]` emitted non-nullable `SparkIndexValue<T>` companions (`CreatedAtRaw` …), which gave CS8618 in a nullable app. They are now `SparkIndexValue<T>?` (`GenerateIndexGenerator.cs`, both places). The expectation in `GenerateIndexGeneratorTests.cs` was updated; it has not been run yet (batched sweep at the end). The host builds with no SPARK or CS warnings.
  - ClientApp (`@spark-apps/mint-player`, from SeoTester): `<spark-shell>` with the language selector and auth bar, `sparkAuthRoutes` with Facebook, Google, LinkedIn, Microsoft and X plus the account page, a home page (tagline `home.tagline` and the `songs`/`artists` query cards), the PWA manifest and the legacy icons. Renderers: `media-player` (a link with a host icon, http(s) only; the real player is spike S1) and `color-swatch` (paints only `#rgb`/`#rrggbb`). `nx run @spark-apps/mint-player:build:development` is green with no warnings. The csproj has a `UserSecretsId` for the provider secrets in dev. CLAUDE.md lists the app as production.
  - Still open in 0.2/0.3: mail in dev.
  - 0.4 decisions while porting the migration (`apps/MintPlayer/MintPlayer.Migration`): users go to `SparkUsers/{guid:D}` as `SparkUser`. Roles become `Roles[]` only, with no extra `group` claim, because `AgentStoreGroupMembership` and the cookie read `Roles`. `AuthenticatorKey` and other tokens are copied verbatim, since `UserStore.Secrets` reads values without the `sdp1:` prefix as plaintext. `PictureUrl` is dropped (SparkUser has no such field) and reported. `OldId` is gone. Embedded rows get deterministic ids derived from the document id and the legacy row, so re-runs and `--verify-only` reproduce every byte. `BlogPost` becomes a real library entity now (no persistent-object wiring until step 9).
  - 0.4 delivery (owner, 2026-10-22: "let the migration run locally on localhost:8080 - export the database contents - scp the file over to my vps and import the file in that database"): production never runs the migration or sees the SQL snapshot. The migration deploys **no indexes**, because Spark deploys the app's own at startup, so the dump carries data only.
    - Evidence, local run into `MintPlayer_Cutover`: 1180 documents, 776 email reservations, RECONCILER GREEN. Every count check matched the source, round-trip 1180/1180 SHA-256 equal, 1176 references with 0 unresolved, 20 karaoke timelines with 0 mismatches. 77 issues, all expected (2 Warning: `medium-empty` x2, `bare-datedelete-live` x1).
    - Export: the Raven client smuggler (`Documents | CompareExchange | CounterGroups | Attachments`) gave 1180 documents and 776 compare-exchange values in a 281 KB `.ravendbdump`. The raw `/smuggler/export` endpoint via curl returned only a `SmugglerResult` with no data.
    - `Microsoft.Data.SqlClient` 6.1.1 became 7.1.1, and `System.Security.Cryptography.Pkcs` is pinned to 10.0.13: 7.1.1 still brings 9.0.18, which is flagged by GHSA-846f-f764-76mx (high). `SPARK039` is suppressed in the tool, since it is not a Spark host.
    - The tests use MintPlayer.Assertions, because `Assert` is banned repo-wide (RS0030).

## Stage 0: live today

| Step | Work | Done when |
|---|---|---|
| 0.0 | **Owner:** export a fresh SQL Server `.bak` (or a Plesk DB dump) from Foxxl. Note the DNS zone, including the MX, SPF and existing records (S4). | The backup is on the local disk, and the DNS records are written down |
| 0.1 | Spike **S1**, the player smoke test: ng-video-player 22.1 in a throwaway route plays one YouTube, one SoundCloud, one Vimeo and one Dailymotion medium. | It plays, or the gap is written down along with its workaround |
| 0.2 | Scaffold `apps/MintPlayer/MintPlayer` and `ClientApp` from SeoTester. Steps: <ul><li>Nx `project.json` with the target `@spark-apps/mint-player:build`.</li><li>https port 5015, checked free in `launchSettings` across apps.</li><li>`AddSpark` and `AddAuthentication<SparkUser>`: `LocalCredentials=Full`, `Passkeys=Enabled`, `PasskeyServerDomain="mintplayer.com"`, `ExternalLoginTwoFactor` on.</li><li>`AddExternalProviders(config)` and `AddMailManager()`.</li><li>Add the app to the CLAUDE.md production list.</li></ul> | `dotnet run` serves the shell, and register/login works against local Raven |
| 0.3 | Port the domain from the old branch's `MintPlayer.Domain` and `MintPlayer.Web` (Actions, Indexes, `App_Data/Model`, security.json, translations) to current Spark APIs. Groups: Administrators, Moderator, and anonymous read. | The generic list, detail and edit pages work for Person, Artist, Song, MediumType, Tag and TagCategory |
| 0.4 | Spikes **S2** and **W2**: port `MintPlayer.Migration` and its tests to net11 and the ported entities. Run it on the 0.0 backup into a local database. Log in as a migrated user by email and by username (rehash). Check one TOTP user and one external-login mapping. | The reconciler is green, and the logins work |
| 0.5 | A minimal public song page, `/song/:id/:title`. It shows the title, artists and lyrics, and `ng-video-player` plays the media. Home lists the latest songs. | It works locally on migrated data |
| 0.6 | Deploy files, copied from SeoTester and adapted: `apps/MintPlayer/docker-compose.yml` (Traefik ``Host(`mintplayer.com`) \|\| Host(`www.mintplayer.com`)``, hard-coded, with a www-to-apex redirect), `Dockerfile`, `.github/workflows/mintplayer-deploy.yml`, `mintplayer-image-check.yml`, `tools/verify-mintplayer-deploy.test.mjs`, and an entry in `verify-backend-networks`. | The image builds locally |
| 0.7 | **VPS, with the owner's approval of every step** (it is production). Depending on Q1:<ul><li>Shared Raven: a `mintplayer-raven` network added to the CodeCoverage compose, then a CodeCoverage redeploy.</li><li>Create the `MintPlayer` database.</li><li>Issue and register the client cert (`raven-tls-migration.md` §3–4) and add it to `raven-cert-renew`.</li><li>Write `/var/www/mintplayer/.env` (0600) and `raven-certs/`.</li></ul>Then import the migration output into the production database. | `/health` is green behind Traefik when called with a Host-header override |
| 0.8 | **Cutover.** Show the maintenance page on Foxxl (D30), take the final `.bak`, re-run the import, switch the A and AAAA records for the apex and `www` at Foxxl/Plesk (click "Update"), keep the MX and mail records, then wait for the Let's Encrypt cert. | https://mintplayer.com serves the new app with migrated data |
| 0.9 | Smoke test on production: anonymous browse and play, a migrated login, register plus a confirmation mail (if S4 and DKIM are done), and passkey registration. | Green. Foxxl can be cancelled once mail is settled (Q4) |

**Fallback if 0.0 or 0.4 slips:** deploy 0.2, 0.3, 0.5, 0.6 and 0.7 on `new.mintplayer.com` today, then
do the apex cutover (0.8) when the data import is green. Keep the Foxxl site running until then.

## Stage 1: parity

Batch the deploys. Each item is a step on the same branch and PR.

1. **S3** spike. Then the public layout: an ng-bootstrap navbar, footer and the player dock with the queue
   (CDK drag-drop), as custom routes before `sparkRoutes()`.
2. Lists and detail pages for person, artist and song with the legacy URL shapes. Search over a RavenDB index
   (`Subjects_Search` from the old branch) and the `/search/:term` route.
3. Playlists (public and private, editing, reorder), likes and favorites, and tags with the category colors.
4. Lyrics, one shared text with revisions (D29), and the timeline sync editor.
5. Auth: the external providers live (Q3), the TOTP migration, 2FA bypass mapped to `ExternalLoginTwoFactor`,
   account page, and passkeys.
6. Mail: the `mintplayer.com` sender domain on the relay, DKIM, and SPF (S4).
7. i18n: en, nl and fr from the legacy JSON into `translations.json`, plus the language selector.
8. **S5** spike. Then bot and server pages with OG and JSON-LD, `sitemap.xml`, `robots.txt` and the OpenSearch
   descriptor, share buttons, and the 301s (`/amp/song/{id}` and any changed shapes).
9. GDPR privacy and terms (server pages), and the blog.
10. Tests:
    - a `MintPlayer.Tests` project (migration, actions, endpoints) plus an entry in `test.yml`;
    - E2E for the song page and login.
11. End-of-stage sweep: `npm run test:affected`, then a SeoTester run against old and new.

## Stage 2: later

These have no dates:
- SSR (S6).
- `api/v1`, if it has a consumer.
- Rooms (S9) and YouTube import (S10).
- Moderation and open editing (needs #460).
- GDPR export and delete.
- Service worker and PWA.
- The npm deprecation of the replaced packages (PRD §7).
- Archive `MintPlayer/MintPlayer` and move it out of the org.
