# MintPlayer website on Spark: plan

PRD: [mintplayer_website_PRD.md](mintplayer_website_PRD.md). Everything lands in **one PR** from branch
`feat/mintplayer-website`. Stage 0 is deployed from that branch's image before the PR merges, if needed, so
the site can go live today. Test runs are batched at the end of each stage, never per step.

Status: not started (2026-10-22).

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
| 0.7 | **VPS, with the owner's approval of every step** (it is production). Depending on Q1:<ul><li>Shared Raven: a `mintplayer-raven` network added to the CodeCoverage compose, then a CodeCoverage redeploy.</li><li>Create the `MintPlayer` database.</li><li>Issue and register the client cert (`raven-tls-migration.md` §3–4) and add it to `raven-cert-renew`.</li><li>Write `/var/www/mint-player/.env` (0600) and `raven-certs/`.</li></ul>Then import the migration output into the production database. | `/health` is green behind Traefik when called with a Host-header override |
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
