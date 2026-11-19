# Dating app on Spark: plan

PRD: [dating_app_PRD.md](dating_app_PRD.md). Everything lands in **one PR** from branch `feat/dating-app`
(app, framework changes, CodeCoverage/MailRelay compose changes, deploy workflow). Tests are written red-first per
step and run once at the end (global rules 5/6); spikes measure and record numbers in PRD §10.

Status (2026-11-19): PRD and plan drafted; grill on PRD §9 next. No code yet.

### Log

Update at every step; it is what survives a context compaction.

- 2026-11-19: investigation (PRD §2), VPS layout read (shared `coverage-raven`, per-app network + database-scoped
  client cert, Community licence, nightly `raven-cert-renew`). Branch `feat/dating-app` created.

## Stage 0: decisions and spikes

| Step | Work | Done when |
|---|---|---|
| 0.1 | Grill PRD §9 (Q1–Q16); record answers in PRD §10 and update D-numbers | every Q answered or explicitly deferred |
| 0.2 | S1 spatial radius query + S2 stack generation (local RavenDB, 100k synthetic profiles, no personal data) | numbers in §10, approach chosen |
| 0.3 | S5 match race, S3 trilateration test | pass bars met |
| 0.4 | S4 photo pipeline, S10 deletion completeness (can run after M2) | pass bars met |
| 0.5 | S8 production database option, for Q2 | owner decision |

## Stage 1: dating core

| Step | Work | Done when |
|---|---|---|
| 1.1 | Scaffold `apps/Klik` (host, Library, Tests, ClientApp `@spark-apps/klik`) from `apps/MintPlayer`: https 5017/http 5018, PWA, `<spark-shell>`, nl/fr/en, auth (email verification required, passkeys, Google, Microsoft). `CLAUDE.md` lists it as production | host + client build without warnings |
| 1.2 | Domain: `Profile`, `Photo`, `Preferences`, `LanguageSkill`, `Swipe`, `Match`, `Block`, `Settings`; `security.json` (Anonymous: nothing; Signed-in: own rows only); row filters + attribute redaction; one test per row of PRD §5.1 | `--spark-synchronize-model`, `--spark-synchronize-security`, `--spark-verify-security` clean |
| 1.3 | Location: grid snapping, bucketed distance, rate-limited updates (S3 result) | trilateration test green |
| 1.4 | Photos: upload, re-encode, attachment storage, authorised serving, SW exclusion (S4 result) | metadata-strip and access tests green |
| 1.5 | Onboarding + profile editor + preferences UI (languages with levels, mutual filters explained) | usable on a phone viewport |
| 1.6 | Stack endpoint (S1/S2 result), shown-tracking, swipe/undo endpoints with quotas, match creation (S5 result) | endpoint + race tests green |
| 1.7 | Swipe deck page (gestures + buttons + keyboard), "who liked you", matches list | Playwright check in mobile emulation |
| 1.8 | Chat: `Conversation`/`Message` live, DM setting (Q5), message requests, first-message link block, read receipts toggle, mute/unmatch/block/report | S6 pass bars as tests |
| 1.9 | Notifications: match + message via in-app/push/mail, per-user channels | push arrives with no tab open |
| 1.10 | Safety + GDPR: moderation wiring (reports, review queue, suspensions, auto-hide), pause, export, hard delete incl. revisions (S10), retention job (Q13), consent screens, privacy/terms pages | S10 zero-hit test green |

## Stage 2: events

| Step | Work | Done when |
|---|---|---|
| 2.1 | `Event`, `Attendance` with row security (private place until accepted), spatial index | tests per visibility rule |
| 2.2 | Create/edit/cancel, RSVP, waiting list, approvals, notifications to attendees | |
| 2.3 | Event search (radius, date, category, language, places left, text) | |
| 2.4 | Event chat (reuses 1.8), attendee visibility opt-in, Q10 "attendees can like each other" | |

## Stage 3: deploy

| Step | Work | Done when |
|---|---|---|
| 3.1 | `apps/Klik/docker-compose.yml`: networks `web`, `klik-raven` (internal), `klik-mail` (internal); `Spark__RavenDb__CertificateFile/KeyFile/ServerCertificateAuthorityFile`, `EnsureDatabaseCreated=false`, Data Protection keys volume | `tools/verify-klik-deploy.test.mjs` green |
| 3.2 | CodeCoverage compose + `raven-cert-renew` know `klik`; MailRelay knows `klik-mail` (or the dedicated server of Q2) | |
| 3.3 | `.github/workflows/klik-deploy.yml`, image `ghcr.io/mintplayer/klik:master` | |
| 3.4 | **Owner-run on the VPS** (each step asked first): operator creates database `Klik`, issues `klik.crt` (Admin on `Klik` only) with the CA, installs files `0400` for the app user, DNS + Traefik route | app healthy, `/license/status` unchanged, cert scoped (a query on another database is refused) |
| 3.5 | Full test sweep once, via the PR's CI (memory: no local sweeps), then coverage via the codecoverage MCP | green |
