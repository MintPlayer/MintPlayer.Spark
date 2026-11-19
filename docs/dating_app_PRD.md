# Dating app on Spark: PRD

Status: draft, 2026-11-19; grill Q1–Q18 decided, spikes S1/S2 done (§10.1), Q19–Q21 open (§9). Execution plan:
[dating_app_plan.md](dating_app_plan.md). Name: **Bothmeet**
(bothmeet.com + bothmeet.net; Q1 in §10): "both want to meet", for a date, a friend or an event.

## 1. Goal

A free alternative to Tinder, built as a **production** Spark app in `apps/Bothmeet`, installable as a PWA and
deployed on the VPS beside CodeCoverage, SeoTester and MintPlayer. The owner's brief (2026-11-19):

- swipe left/right on profiles; matches;
- direct messages, with a setting each user can turn on and off;
- a web app that installs as a PWA (no app stores);
- **no discrimination against men** (owner, 2026-11-19, answered as all four of): the same rules for every
  gender, no hidden desirability score, no paywall on likes or swipes, and "who liked you" visible for free
  (*superseded by Q6, §10: everyone sees how many liked them, not who*);
- free to use;
- people organise events, and others search for them;
- user information is never readable by anyone not entitled to it;
- filters on radius, **language** (the Flanders case: a Dutch speaker keeps being shown French-only profiles)
  and more;
- "anything else you can think of" (§3.4, all marked as proposals).

When it is created, `apps/Bothmeet` is added to the production paragraph of `CLAUDE.md`.

## 2. Where we start from (investigation, 2026-11-19)

### 2.1 What Spark already gives the app

| Need | Spark building block | Evidence |
|---|---|---|
| Accounts: email + password, passkeys, external login, 2FA | `MintPlayer.Spark.Authorization` (`SparkUser`), passkeys page | [guide-passkeys.md](guide-passkeys.md), `apps/MintPlayer` uses the same |
| "Only the right people read a row" | row security: `GetRowFilterAsync` pushed into the Raven query, `IsAllowedAsync`, `GetProtectedAttributesAsync` per-row attribute redaction, on every read path incl. live updates | [guide-row-security.md](guide-row-security.md) |
| Type-level rights per group | `security.json` + `--spark-verify-security` | [guide-authorization.md](guide-authorization.md) |
| Chat that updates live | live updates over one WebSocket (`/spark/live`), same row security as a page load | [guide-live-updates.md](guide-live-updates.md) |
| "You have a match / new message" | notifications: in-app + mail + **Web Push** in one call, per-user channel choice | [guide-notifications.md](guide-notifications.md) |
| Installable PWA | `@mintplayer/ng-spark/pwa`, manifest + Angular service worker | [guide-pwa.md](guide-pwa.md) |
| Reports, review queue, suspensions | `MintPlayer.Spark.Moderation` (flags, review queue, locks, suspensions) on `IModeratable` | [guide-moderation.md](guide-moderation.md) |
| Anti-spam limits | rate limiting (per caller) and quotas (per user, per custom action) | [guide-rate-limiting.md](guide-rate-limiting.md), [guide-quotas.md](guide-quotas.md) |
| nl / fr / en UI | `translations.json`, `TranslatedString` | [guide-translated-strings.md](guide-translated-strings.md) |
| Account / settings page | settings package | [guide-settings.md](guide-settings.md) |
| Mail (verification, notifications) | MailManager through the shared relay `apps/MailRelay` | [guide-outgoing-mail.md](guide-outgoing-mail.md) |
| RavenDB over HTTPS with a client certificate | `Spark__RavenDb__CertificateFile` / `CertificateKeyFile` / `ServerCertificateAuthorityFile` | [guide-docker-deployment.md](guide-docker-deployment.md):84-87, `apps/MintPlayer/docker-compose.yml`:28-34 |

### 2.2 What Spark does **not** have yet (framework gaps)

| Gap | Why the app needs it | Proposal |
|---|---|---|
| **Geo / radius queries.** No spatial support anywhere in `libs/spark` (grep `spatial`, 2026-11-19: only an analyzer string). RavenDB has it natively (`CreateSpatialField`, `WithinRadiusOf`). | radius filter for profiles and events | App-defined index with a spatial field (indexes live in the application, see memory "indexes only in application"); the stack and event search are custom endpoints. Spike S1 decides whether a generic `Spatial` filter belongs in Spark. |
| **Images / files.** The Raven store advertises `SparkStoreCapabilities.Attachments` (`RavenSparkStore.cs:19`) but there is no image attribute, upload endpoint or renderer. | profile photos, event pictures | Spike S4. Photos are app documents with a Raven attachment, served only through an authorised endpoint. |
| **Swipe deck UI.** Generic PO pages are list/detail/edit. | the core screen | A custom Angular page with ng-bootstrap components (never plain bootstrap classes). |
| **User-to-user chat.** `MintPlayer.Spark.Messaging` is a durable job queue, not chat. | DMs, event chat | Compose: `Conversation` + `Message` types, `"live": true`, row filter "participant". |
| **Encryption at rest.** Production RavenDB runs the **Community** licence (`docs/code-coverage/raven-tls-migration.md`:97, :325, :344); database encryption is an Enterprise feature. | special-category data (§5.5) | Q12: field-level encryption of selected fields in the app, disk encryption on the VPS, or accept. |

### 2.3 Production hosting today (VPS, read 2026-11-19)

- One RavenDB server, `coverage-raven` in `/var/www/code-coverage`, HTTPS on 8080 with
  `RAVEN_Security_Certificate_Path` and an operator certificate as the only cluster admin
  (`RAVEN_Security_WellKnownCertificates_Admin`). No host ports.
- **Every app reaches it over its own internal network** (`seo-raven`, `mintplayer-raven`) with **its own client
  certificate, registered with Admin on its own database only**, signed by a private CA (`ca.key` root-only in
  `/root/raven-ca/`, 20 years). Certificates are renewed nightly by `raven-cert-renew`
  (`apps/CodeCoverage/ops/raven-cert-renew.sh`), which knows its apps by name (`docs/mintplayer_website_plan.md`
  status line: it had to be extended for `mintplayer`).
- Each app also has its own internal mail network to the shared relay (`coverage-mail`, `seo-mail`,
  `mintplayer-mail`).
- Taken ports: https 5003, 5005, 5007, 5009, 5011, 5013, 5015. **Bothmeet takes https 5017 / http 5018.**

So the owner's note "access to the RavenDB server needs a client certificate" (2026-11-19) is the existing
pattern: decision D8 below, with the open choice of a dedicated server in Q2.

## 3. Scope

### 3.1 Stage 1: dating core (the first deploy)

**Accounts.** Email + password with mandatory email verification, passkeys, Google and Microsoft sign-in, optional
TOTP. Date of birth at sign-up; under 18 cannot register (Q14 for how hard to verify). Sign-out of all sessions.

**Profile.**
- display name (first name only by convention), age (derived; the birth date is never shown), gender, pronouns
  (optional), bio, 1–6 photos, relationship intent (friendship, dating, relationship, open to see),
  interests (tags), **languages spoken with a level** (native, fluent, conversational, basic; ISO 639-1),
  home area (§5.2), optional: height, children, smoking, drinking, religion, education. Optional fields are
  shown only when filled in.
- **account status** (Q11): visible, hidden ("in a relationship" / taking a break), or delete everything (§5.6).

**Discovery preferences (filters).**

| Filter | Default | Hard or soft |
|---|---|---|
| radius | 25 km, 1–200 km | hard |
| age range | ±5 years of own age, within 18–99 | hard |
| genders shown | from "interested in" | hard |
| **languages: candidate speaks at least one of mine at ≥ level X** | my languages at "conversational" | hard (Q4: mutual?) |
| relationship intent | any | soft or hard per user |
| has photo / recently active (14 days) | on | hard |
| optional fields (children, smoking, …) | not used | per field: "don't care" / prefer / dealbreaker |

**Mutual filters:** a candidate appears only when **both** sides' hard filters accept each other. I never see
someone whose filters exclude me, so nobody's stack fills with people who can't match them. This is also the
fairness rule's mechanism: no ranking, just mutual eligibility.

**The stack.** Cards with photos, name, age, approximate distance (§5.2), shared languages first, shared interests
highlighted. Swipe right (like), left (pass), **undo the last swipe, free**. Order: eligible candidates,
least-recently-shown first, then most recently active (D4). **No score of how often someone is liked.** Keyboard
and button alternatives to gestures (accessibility).

**Likes and matches.** A count of incoming likes, free for everyone; the likers come first in the stack (Q6). A mutual like is a match: in-app + push
notification to both. Unmatch at any time (the conversation disappears for both).

**Direct messages and the DM setting** (Q5 decides the exact semantics). Proposal:

| Setting | Who can start a conversation with me |
|---|---|
| **Matches only** (default) | people I matched with |
| **Open requests** | also anyone who passes *my* filters, as a *message request* I accept or decline; one message until accepted |
| **Off** | nobody new; existing conversations stay, unless I also pick "close all" |

Plus per conversation: mute, unmatch, block, report. Read receipts and typing indicator are each a setting
(off by default; reciprocal: whoever turns read receipts off doesn't see others' either).

**Safety.** Block (absolute, both ways, everywhere: stack, likes, chats, events), report (profile, photo, message,
event) into the moderation review queue, links and phone numbers blocked in the first message of a conversation
(anti-scam), uniform rate limits (D3).

**PWA.** Installable, Web Push for matches and messages (iOS needs the PWA installed, iOS 16.4+), offline shell.
The service worker must **never cache another user's photos or messages across sign-out** (S4).

### 3.2 Stage 2: events

- Any verified user creates an event: title, description, category (activity tags), start/end, place, languages
  spoken at the event, optional age range, capacity, "approve attendees" or open, optional picture.
- **Place privacy:** public venue (exact address shown) or private place (only the area is shown until the
  organiser accepts you).
- Search: radius, date range, category, **language**, "has places left", text.
- RSVP (going / interested), waiting list when full, attendee list visible only to attendees who opted in to be
  visible, an event chat for attendees, cancel/edit notifications to attendees, report event.
- Optional (Q10): "people going to this event" opt-in, so attendees can like each other before meeting.
- Recurring events and organiser groups are Stage 3.

### 3.3 Stage 3: later

Photo verification badge (S9), travel mode (set the location elsewhere for a while), incognito (only people I liked
see me), share-my-date-plan with a trusted contact, profile prompts ("A perfect Sunday is…"), voice notes, recurring
events and organiser groups, de/en extra UI languages.

### 3.4 Proposals beyond the brief (owner to accept or strike in the grill)

1. **Mutual filters** (§3.1): the most effective fix for "French-only profiles in my Flemish stack", because it
   also hides me from people who don't speak my languages.
2. **Language levels**, not just a language list: "basic French" should not satisfy "speaks French".
3. **UI language ≠ spoken languages:** the default for "languages I speak" is the UI language, not the location.
4. **Exposure fairness:** least-recently-shown first, so every eligible profile gets seen, instead of a popularity
   loop.
5. **Undo, like count, unlimited radius changes, read-receipt toggle**, all free: the paywalled features of
   Tinder, given to everyone.
6. **Anti-scam in the first message** (no links/phone numbers) and a uniform daily like cap (D3) instead of paid
   limits.
7. **Events as the cold-start answer**: in a small region the stack runs dry; events still give a reason to open
   the app.
8. **Data export** (GDPR Art. 20) and **real deletion** including revisions (§5.6).

### 3.5 Out of scope

Payments, boosts, ads and trackers (an ad network would undo §5), native apps, video calls, AI matchmaking,
importing contacts.

## 4. Architecture decisions (proposed)

| # | Decision | Why |
|---|---|---|
| D1 | `apps/Bothmeet/Bothmeet` (host) + `apps/Bothmeet/Bothmeet.Library` (entities) + `apps/Bothmeet/Bothmeet.Tests`; ClientApp `@spark-apps/bothmeet`, scaffolded like `apps/MintPlayer` | the established production-app layout |
| D2 | Own Spark accounts (`AddSparkFull`/Authorization, `SparkUser`), not SparkId | owner, 2026-11-19 |
| D3 | **Same rules for everyone**: no gender-dependent code path anywhere; uniform limits, e.g. 200 likes/day, 30 new conversations/day, via quotas; nothing for sale | owner, 2026-11-19; numbers are a proposal (Q7) |
| D4 | Stack order = mutual eligibility, pending likers first (Q6), then a **random per-viewer order** that stays stable so a cursor can page it (S2). **No desirability score** is computed or stored. ~~then least-recently-shown, then last active~~ (superseded by S2, 2026-11-19: a changing sort key cannot be paged by cursor) | owner, 2026-11-19 (no score); S2 (order) |
| D5 | Profiles are **never** reachable through generic queries for non-admins. The stack, likes, matches and event attendees are custom endpoints that return a `ProfileCard` projection, after the row checks of §5.1 | §5.1 |
| D6 | Chat = `Conversation` + `Message` documents, `"live": true`, row filter "I am a participant"; push when no tab is open | compose existing blocks (memory "compose from existing blocks") |
| D7 | Spatial: app index with `CreateSpatialField` on a **snapped** location (§5.2); radius via Raven `WithinRadiusOf` in the custom endpoints | S1 |
| D8 | Production RavenDB over HTTPS with **Bothmeet's own client certificate, Admin on database `Bothmeet` only**, private CA, own internal network `bothmeet-raven`, added to `raven-cert-renew`. `EnsureDatabaseCreated=false`; the operator creates the database | existing pattern, §2.3; owner's note 2026-11-19. Q2: shared vs dedicated server |
| D9 | Own mail network `bothmeet-mail` to the shared relay | §2.3 |
| D10 | Ids are GUIDs (`Profiles/{guid}`), deterministic ids only for pair documents (`Swipes/{a}/{b}`, `Matches/{min}/{max}`); no sequential ids anywhere | no enumeration, idempotent writes (S5) |
| D11 | Zoneless Angular, ng-bootstrap components, `<spark-icon>` for icons, router navigation only (no full reloads) | repo rules / memory |
| D12 | One pull request, branch `feat/dating-app`, framework changes included | global rule 8 |

## 5. Privacy and security model

Dating data is **special-category personal data** under GDPR Art. 9 (it reveals sex life and sexual orientation),
and location + photos make it stalking-relevant. Real incidents shape the design: trilateration of exact distances
(Tinder 2014, Grindr), EXIF GPS in uploaded photos, scraping of public profile endpoints, enumerable ids.

### 5.1 Who reads what

| Data | Owner | Another signed-in user | Moderator | Anonymous |
|---|---|---|---|---|
| profile card (name, age, photos, bio, languages, interests, optional fields) | yes | only when served in their stack, when they liked me, when matched, or when we are both opted-in attendees of an event; **never** when either blocked the other | only via a report | **no** |
| birth date, email, exact location, filters, settings | yes | **never** | no (admin only, for support) | no |
| swipes | own outgoing | the **count** of incoming likes only (Q6); who liked, and passes, never | no | no |
| messages | participants | participants only | only the messages attached to a report (snapshot) | no |
| events | yes | public fields to signed-in users; private place after acceptance | yes | Q10 |

Enforced in three layers: `security.json` (types not queryable by `Signed-in` at all where possible, e.g. `Swipe`,
`Location`, `Settings`), row filters on every type (`GetRowFilterAsync`), and attribute redaction
(`GetProtectedAttributesAsync`). A test per row in this table (red first).

### 5.2 Location

- The browser location (or a typed postcode/city) is **snapped to a ~1 km grid cell** before it is stored; the
  precise coordinates are never stored.
- Distances are shown in buckets ("< 2 km", "~5 km", "~10 km", then rounded to 5 km) and computed between cell
  centres, so moving a fake account to three positions can't triangulate a cell more precisely than the cell (S3).
- Location updates are rate-limited (e.g. 1 per 10 minutes) to stop scripted probing.

### 5.3 Photos

Re-encoded server-side (strips EXIF/GPS and any payload), resized to fixed sizes, stored as Raven attachments on a
`Photo` document, served only through `/photos/{id}/{size}` after the same "may I see this profile" check, with
`Cache-Control: private` and a short lifetime. No public URLs, no CDN. The service worker excludes them.

### 5.4 Anti-scraping and abuse

The stack endpoint returns a few cards at a time and records them as shown (D4), so scraping costs swipes, which
are capped (D3). Rate limits on every endpoint, quotas on likes, new conversations, reports and location
changes. GUID ids (D10). Moderation: suspensions hide the account everywhere.

### 5.5 Data at rest and in transit

TLS everywhere, including app → RavenDB with a database-scoped client certificate (D8). The RavenDB database is not
encrypted on the Community licence (§2.2); Q12 decides between field-level encryption of messages, VPS disk
encryption, or accepting the risk in the DPIA. Backups: same question.

### 5.6 GDPR and DSA

- Explicit consent for the special-category fields (gender interest, orientation implied), separately from the
  terms, withdrawable (= profile hidden).
- **Delete account = hard delete** of profile, photos (attachments), swipes, matches, settings, the user, and
  their messages (Q11 for what the other party sees), **including RavenDB revisions** of those documents, which
  otherwise keep the data (S10). Export as JSON + photos (Art. 20).
- Inactive accounts: hidden after 6 months, deleted after 24 (Q13).
- A DPIA document, privacy policy and terms (nl/fr/en) before launch.
- Digital Services Act: notice-and-action for reports, a statement of reasons on each moderation action, an
  appeal path. Small-platform exemptions likely apply; checked before launch.

## 6. Spikes

Each spike has a question, a method and a pass bar; results go to §10 with numbers.

| # | Question | Method | Pass |
|---|---|---|---|
| S1 | Can a Spark app run a radius + filters query on an app-defined spatial index, through the Spark session and row security? Does a generic `Spatial` attribute/filter belong in Spark? | Local RavenDB, 1M synthetic profiles worldwide (Q15) with dense clusters (cities) and sparse areas, index with spatial field + languages/age/gender | radius + filters p95 < 100 ms in a dense city; one call path with row security |
| S2 | Stack generation with **mutual** filters, excluding already-swiped and blocked, at 1M profiles (Q15) and a user with 20k swipes | compare: query-then-exclude in memory, `NOT IN` on ids, and a per-user "seen" document | p95 < 150 ms, no full scans; pick one |
| S3 | Does grid snapping + bucketed distance defeat trilateration? | an attacker test that moves three accounts and tries to locate a victim | best estimate no better than the cell (~1 km) |
| S4 | Photo pipeline: library (SkiaSharp MIT vs ImageSharp split licence), EXIF strip proven, attachment vs volume storage, authorised serving, SW never caches across sign-out | upload a GPS-tagged JPEG, HEIC from iPhone, a PNG with a payload | output has no metadata; another user gets 404; SW cache empty after sign-out |
| S5 | Two users like each other at the same moment: exactly one `Match` | deterministic id + optimistic concurrency vs compare-exchange; 1000 parallel pairs | 1000 matches, 0 duplicates, 0 lost |
| S6 | Chat on live updates: latency, per-message row security, unread counts, push when no tab is open, DM setting enforced server-side | two browsers + one closed PWA | < 500 ms delivery; a non-participant gets nothing, not even counts |
| S7 | Swipe UX in a mobile PWA: pointer-event gestures, prefetch, keyboard, reduced motion | Playwright mobile emulation + a real phone | 60 fps drag, no layout shift, works without gestures |
| S8 | Production database: shared `coverage-raven` (new db + cert + network) vs a dedicated RavenDB container for Bothmeet (own licence? Community allows how many servers?) | read the licence terms, `/license/status`; dry-run the cert issuance with the CA locally | owner picks (Q2) with the costs in hand |
| S9 | Photo verification without paid services: moderator compares a pose selfie, or none | design only | owner picks (Stage 3) |
| S10 | Account deletion leaves **nothing**: documents, attachments, revisions, compare-exchange, indexes' stored fields, notifications, push subscriptions | delete a fully used account, then search the whole database for its id, email and a photo hash | zero hits |

## 7. Risks

| Risk | Mitigation |
|---|---|
| Legal: special-category data, minors, DSA | §5.6; 18+ at sign-up; DPIA before launch; Q14 on age verification |
| Cold start: an empty stack in a small region | events (§3.2), launch per region (Q15), the radius filter shows how many people are in range |
| Bots and romance scammers on a free service | uniform caps, first-message link block, reports, email verification, (S9) |
| Moderation load for a one-person operator | review queue, auto-hide after N reports from distinct accounts pending review, Q8 |
| Photo storage growth on the VPS | fixed sizes, max 6 per profile, deletion is real; measure in S4 |
| Shared RavenDB with other apps holds intimate data | database-scoped cert (D8); Q2 dedicated server |

## 8. Changes outside this app

- `CLAUDE.md`: list `apps/Bothmeet` as production (https 5017).
- CodeCoverage compose / deploy: the `bothmeet-raven` network on `coverage-raven` (unless Q2 = dedicated server).
- `apps/CodeCoverage/ops/raven-cert-renew.sh`: renew `bothmeet.crt`.
- `apps/MailRelay`: a `bothmeet-mail` network.
- `.github/workflows/bothmeet-deploy.yml` and `tools/verify-bothmeet-deploy.test.mjs` (like MintPlayer's).
- CI: `Bothmeet.Tests` lands in "Other suites" automatically; keep the run at ≤ 20 jobs.
- Possibly Spark: a spatial filter (S1) and an image attribute (S4), only if the spikes say so.
- DNS for the chosen domain (Q1).

## 9. Open questions (to grill)

| # | Question | Proposal |
|---|---|---|
| Q1 | Name and domain | **decided: Bothmeet** (§10) |
| Q2 | Shared `coverage-raven` with a database-scoped cert, or a dedicated RavenDB for Bothmeet? | **decided: shared** (§10) |
| Q3 | Gender model: which options, "interested in" multi-select, non-binary? | **decided: broad** (§10) |
| Q4 | Language filter: hard + mutual by default? Minimum level? | **decided: hard, mutual, "conversational"** (§10) |
| Q5 | DM setting: the three modes of §3.1? Do "open requests" exist at all? | **decided: three values, default "matches only"** (§10) |
| Q6 | "Who liked you" free: shown with photos, or blurred until I swipe on them myself? | **decided: count only** (§10) |
| Q7 | Uniform daily caps: values | **decided** (§10) |
| Q8 | Moderators: only the owner at launch? Photo pre-moderation or post? | **decided** (§10) |
| Q9 | Phone verification (SMS costs money) | **decided: no** (§10) |
| Q10 | Events: who may create, anonymous browsing (SEO/growth vs privacy), "attendees can like each other" | **decided** (§10) |
| Q11 | On account deletion, what does the other party see of past messages? | **decided** (§10) |
| Q12 | Data at rest without Raven encryption: field-level encryption of messages, disk encryption, or accept | **decided: field-level, messages** (§10) |
| Q18 | Backups: database and key ring, where, how often, restore tested? (raised by Q12) | **decided** (§10) |
| Q13 | Retention of inactive accounts | **decided** (§10) |
| Q14 | Age verification: self-declared birth date only, or more? | **decided** (§10) |
| Q15 | Launch region and UI languages | **decided: worldwide; 7 UI languages** (§10) |
| Q16 | Funding a free service (donations? none?) | **decided: none at launch** (§10) |
| Q19 | Stack order after S2: a random order per viewer that stays stable (cursor paging), instead of least-recently-shown (D4) | **open, owner to confirm**: random per-viewer order from ~8 random keys per profile; likers first (Q6); no score (§10.1) |
| Q20 | Age in the index: stored `Age` updated on birthdays by a daily job, query-time `BirthDate` range, or CronosCore `DynamicDates` | **open, owner to confirm**: stored `Age` + birthday job (§10.1: hybrid +22 ms p95; `DynamicDates` re-maps 1M profiles nightly) |
| Q21 | S1 misses 100 ms at 1M (p95 140 ms, dense 178 ms): accept with client prefetch, or keep optimising | **open, owner to confirm**: accept; S8 re-measures under the Community licence |

## 10. Spike results and grill decisions

- **Q1, name: Bothmeet** (owner, 2026-11-19). Domains bothmeet.com and bothmeet.net: unregistered at the Verisign
  registry (RDAP 404, 2026-11-19), confirmed available by the owner at a registrar. Chosen over Datemates
  (datemates.com parked by GoDaddy; only .net/.be free) and Datenite (only .be free, confusable with
  datenight.be, registered 2015), because the name must cover friendships and events as well as dating
  (owner). Identifiers: `apps/Bothmeet`, ClientApp `@spark-apps/bothmeet`, database `Bothmeet`, certificate
  `bothmeet.crt`, networks `bothmeet-raven` / `bothmeet-mail`, image `ghcr.io/mintplayer/bothmeet`. Follow-up:
  trademark search (BOIP/EUIPO) before launch.
  - **Registered 2026-11-19 at Qweb** (owner paid; €19.95/year each, 1-year term renewing yearly, no longer term
    offered in the order). Holder/admin/tech: the owner personally (not J.D.C TOPO BV). Parked at Qweb, Qweb
    nameservers (ns1.qweb.net, ns2.qweb.nu, ns3.qweb.co); DNS is managed in the Qweb panel, not Plesk.
  - **DNS (both zones):** A `@`/`www` → 188.245.190.60, AAAA `@`/`www` → 2a01:4f8:c0c:f87c::1 (the VPS, the same
    AAAA as coverage.mintplayer.com), TXT `@` `v=spf1 -all` (no mail as @bothmeet.* until the mail setup), TTL 14400.
    Qweb's default wildcard `*` → 217.18.79.54 (parking) was replaced by `www`. Qweb's default MX
    (mx.spam-filter.email / fallbackmx.spam-filter.cloud) left as is; revisit with the mail setup (plan 3.2).
    Verified on ns1.qweb.net at 14:56: the .com records live; .net still pending in Qweb's queue.
- **Q17, friends vs relationship: two modes, one active per account** (owner, 2026-11-19: "Either looking for
  friends (male and/or female) OR looking for a relationship (male or female)"). Each mode has its own pool,
  stack, swipes and matches; the mode is part of every pair id (`Swipes/{mode}/{a}/{b}`, `Matches/{mode}/…`).
  Friends: genders shown = male, female or both. Relationship: see Q3. A profile is only in the pool of its
  active mode, so a friends-only user never appears in a dating stack. Proposed, unconfirmed: switching mode is
  allowed (at most once a day, D3), keeps both modes' matches and chats, and moves the profile to the other pool.
  Supersedes the "friendship" value of relationship intent in §3.1 (intent now only applies to relationship mode).
- **Q3, gender model: broad** (owner, 2026-11-19: "C - let's make it broad"). Own gender: man, woman,
  non-binary. "Interested in": any non-empty subset of the three, in **both** modes (so Q17's Friends "male and/or
  female" gains non-binary, and Relationship is not limited to one gender). The mutual filter applies in both modes:
  I see you only if my set contains your gender and yours contains mine. Pronouns stay an optional free-text field,
  never filtered on. Gender is special-category-adjacent data: shown on the card, never queryable by others (§5.1).
- **Q4, language filter: hard and mutual, same default in both modes** (owner, 2026-11-19: "A - yes"). Each user
  lists languages (ISO 639-1) with a level: native, fluent, conversational, basic. A candidate is eligible only if
  we share at least one language where **both** levels are ≥ the minimum of **each** side (default
  "conversational", adjustable per user to basic/fluent/native). Default on in Relationship and Friends; a user may
  switch it off, which widens only their own side (the other side's filter still applies, mutual rule). The default
  "languages I speak" at onboarding is the UI language at "native", never derived from location (§3.4 item 3).
  Accepted risks: language-practice seekers must lower their minimum; overstated levels are only caught by reports.
- **Q5, DM setting: three values** (owner, 2026-11-19: "A", to the option "A with mutual filters for requests").
  **Matches only** (default); **also message requests**: someone who is mutually eligible (both sides' hard filters,
  same rule as the stack, same mode) may send one request message, no links or phone numbers, counted against the
  daily new-conversation cap (Q7); nothing more until the receiver accepts; decline is silent and blocks further
  requests from that sender; the sender never sees read state; **off**: nobody can start a new conversation, not
  even a new match (the match shows "not accepting new conversations right now"), existing conversations stay.
  Enforced server-side on the create-conversation and send-message endpoints, not in the UI only (S6).
- **Q6, incoming likes: count only** (owner, 2026-11-19: "I'd go for B i think"). Changes the owner's first answer
  ("show who liked you, free", §1). Everyone sees, for free, how many people liked them in the active mode; never
  who. Pending likers are placed **first** in the stack (still only mutually eligible ones), so a like turns into a
  match quickly; the identity is revealed only by the match. This is the one ordering exception to D4: it ranks by
  "this person already chose you", never by popularity. The count endpoint returns a number only, never ids; a
  blocked or suspended liker is not counted.
- **Q15 (region): worldwide** (owner, 2026-11-19: "It's not about Belgium. It should be targetting worldwide.").
  Belgium/Flanders is the owner's example of the language problem, not the market. Consequences: the
  Belgium-sized estimates are void (S1 now 1M profiles worldwide); distances in km or miles per user locale;
  times in the viewer's time zone (events); legal review covers more than GDPR/DSA (e.g. UK Online Safety Act and
  US state age-verification laws, §7); the UI-language list is still open; the .be domains are irrelevant.
- **Q7, uniform limits: proposal A tripled** (owner, 2026-11-19: "A looks good to me though, But I'd probably
  tripple the numbers"). Same for every account, stored as settings (changeable without a release): **300 likes
  per day** (passes unlimited), **60 new conversations per day** (matches + requests), **60 messages per minute**,
  **30 reports per day**. Not tripled, because they are security controls rather than usage caps (owner to
  object): mode switch 1 per day (Q17), location change 1 per 10 minutes (§5.2, trilateration). Email
  verification is required before swiping (owner: "Email confirmation is probably required anyway"; already §3.1).
- **Q2, production database: shared `coverage-raven`** (owner, 2026-11-19: "A", against the recommendation of a
  dedicated container). D8 stands as written: database `Bothmeet`, certificate `bothmeet.crt` with Admin on
  `Bothmeet` only, network `bothmeet-raven`, renewed by `raven-cert-renew`. Accepted risks: Bothmeet shares the
  Community licence's hardware limits with CodeCoverage, SeoTester and MintPlayer, and the intimate data sits on the
  same server as the other apps (isolation by certificate scope only). S8 shrinks to: measure the licence limits
  (`/license/status`) and the other apps' current use, so the moment to move Bothmeet out (export + own server) is
  a number, not a feeling.
- **Q12, data at rest: the app encrypts message bodies with Spark's Data Protection key ring** (owner,
  2026-11-19: "Probably A - But you'll need to see if the keyring is already persisted on my vps
  (PersistKeysToFileSystem)"). Scope: message text and report snapshots only. Email (sign-in lookup) and birth date
  (age filter) stay plaintext; filters and gender are query fields.
  - **Persisted: yes** (checked 2026-11-19). Spark calls `PersistKeysToFileSystem` when
    `Spark:DataProtection:KeysPath` is set (`libs/spark/MintPlayer.Spark/Services/SparkDataProtection.cs`); every
    production compose mounts a named volume there (`apps/MintPlayer/docker-compose.yml`:38/62,
    CodeCoverage :204/267, SeoTester :47/82). On the VPS the volumes `code-coverage_`, `seotester_` and
    `mintplayer_dataprotection-keys` each hold one `key-*.xml`, created 2026-09-29, 2026-10-11 and 2026-10-22, so
    they survive redeploys. Bothmeet gets its own `bothmeet_dataprotection-keys` volume and its own
    `ApplicationName`, so no other app's key ring can read its payloads.
  - **Not encrypted at rest:** no `ProtectKeysWith…` anywhere in `libs`/`apps` (grep, 2026-11-19), so the key XML
    is plaintext on the volume (directories `drwxr-xr-x`). Root on the VPS reads keys and database alike (accepted:
    §5.5 protects against RavenDB, backup and certificate leaks, not root).
  - **⚠️ No backup of the key rings found:** no cron or systemd job on the VPS mentions the volumes or a backup tool
    (2026-11-19). Today a lost volume only signs people out; with Q12 it makes every stored message unreadable
    forever. A key-ring backup, stored apart from the database backup, is a launch blocker (Q18).
  - Microsoft's guidance is that Data Protection is "not primarily intended for indefinite persistence of
    confidential payloads": rotated keys stay in the ring (never deleted by default), and revoking a key makes its
    payloads unreadable, so no key is ever revoked for this app. Spark already stores user-store secrets this way
    (`UserStore.Secrets.cs`).
- **Q18, backups: RavenDB periodic backup + separate key-ring backup to a Hetzner Storage Box** (owner, 2026-11-19:
  "Do we really need backups? If so, I think I'd go for A"). Why yes: our own mistakes (a migration, a deletion
  bug, a delete-by-query on the wrong database) are the likeliest loss and replicas copy them; one VPS, one disk,
  four apps; compromise; GDPR Art. 32(1)(c) requires the ability to restore availability in a timely manner.
  - Database `Bothmeet` incl. photo attachments: RavenDB's own periodic backup task, hourly incremental + daily
    full, **encrypted**, to the Storage Box (S8 confirms backup encryption is available on Community).
  - Key ring: nightly `restic` copy of the `bothmeet_dataprotection-keys` volume to a **different** repository with
    a **different** password, so a leaked database backup never comes with the key to its messages.
  - **Retention 30 days** for both, stated in the privacy policy: a deleted account (§5.6) is gone from the
    backups within 30 days.
  - Alert by mail (shared relay) when no new backup arrived for 26 hours; a monthly restore of database + key ring
    into a scratch database, which proves an encrypted message decrypts. Both are part of the deploy stage.
  - Not checked yet: whether CodeCoverage/SeoTester/MintPlayer have a RavenDB backup task configured inside the
    database (cron/systemd showed none). Out of scope for this PR; worth an issue if they have none.
- **Q11, leaving: three account statuses, and deletion removes conversations for both** (owner, 2026-11-19: "A -
  But let users choose whether they want to be advertized, just hidden because they have a relationship (then they
  can't swipe themselves either) or everything gone"). Replaces "pause profile" in §3.1.
  1. **Visible** (default): in the stack of the active mode, can swipe.
  2. **Hidden** ("in a relationship" or "taking a break", the user's label): out of every stack and every like
     count, **cannot swipe**, no new message requests or matches. Back to visible at any time, with swipes,
     matches and settings intact. Proposed, unconfirmed: existing conversations stay usable; events stay usable
     (attending is not swiping); hidden applies to both modes, so a user in a relationship who wants friends
     switches to visible in Friends mode (Q17) rather than staying hidden.
  3. **Delete everything**: hard delete per §5.6; **every conversation the user was in disappears for both sides**
     (theirs and the other party's messages). The other party gets a notification ("a match deleted their
     account"). Exception: messages already captured in a report stay as an encrypted snapshot until the report is
     closed + 6 months; reporting a conversation snapshots it at once, so a victim can secure evidence before
     the harasser deletes. Unmatch follows the same rule (gone for both).
- **Q13, inactive accounts** (owner, 2026-11-19: "A"). GDPR Art. 5(1)(e). Settings, changeable without a release:
  **3 months** without sign-in → out of every stack (not a status change; signing in restores it). **12 months**
  (visible accounts) or **24 months** (hidden accounts, Q11) → warning by mail and in-app at 30 and at 7 days
  before deletion; no sign-in → full deletion per Q11. The other party's notification says "removed after a long
  period of inactivity", not "deleted their account". A daily sweep (Spark cron) runs it; tests cover each
  boundary.
- **Q14, age: self-declared birth date + report-and-hide, plus regional blocking where the law requires more**
  (owner, 2026-11-19: "agree", to the recommendation A + C). Under 18 at sign-up is refused, and the same browser
  and email cannot retry with another date. An "underage" report hides the profile **at once**, before review;
  proof of age is asked only then (proposed: an ID document shown to the moderator through a one-off upload,
  deleted the moment the report is decided, never kept). Accounts whose underage reports keep proving false lose
  that report category. **Launch blocker:** a legal check lists the countries where a declared birth date is not
  enough (e.g. UK Online Safety Act, some US states, Australia); there the app refuses sign-up until a solution
  exists (country from the sign-up location, §5.2). Third-party facial age estimation is rejected for the launch
  (cost per check, faces to a third party; "free" and "no trackers").
- **Q8, moderation: community moderation for public content, Moderator and Admin as separate roles** (owner,
  2026-11-19: "I think I'd go for C - also, i don't have money to spend for other services", then "I follow you.
  But we'll probably make a distinction between Admin and Moderator"). Built on `MintPlayer.Spark.Moderation`
  (README: earned privileges gated by reputation + account age + active days; only reversible privileges are
  earnable; ten vote-fraud defences). Three tiers:
  1. **Community reviewers** (earned): review reports on **public content only**, which any eligible signed-in
     user could already see: profile text, photos, events. Reversible actions only (hide/restore); several
     independent reviewers per decision; may skip a case (explicit-photo warning).
  2. **Moderator** (appointed by the Admin, never earned; interpretation, owner to correct): everything a
     reviewer does, plus the private reports (reported message snapshots, underage reports and the one-off ID
     proof of Q14), suspensions, statements of reasons and appeals (DSA). Confidentiality agreement; every
     action logged with its reason; revocable at once.
  3. **Admin** (the owner): Moderator rights, plus appointing/revoking moderators, the settings (Q7 limits, Q13
     retention), the country blocks (Q14). No general browsing of user data; support access is logged.
  - **Reputation without votes** (a dating app has none; votes would become a popularity score, against D4):
    + for an upheld report, − for a rejected one, + for a review that agrees with the final outcome; privilege
    gate also requires e.g. 60 days account age and 30 active days. Reputation is never shown on a profile and never
    affects the stack.
  - Photos are post-moderated (visible at once, checked after a report); pre-moderation would make new users wait
    for one person. A local nudity classifier (no third party) is a Stage 3 idea.
  - Accepted risks: brigading by a patient group (reversible actions, reviewer diversity, the owner audits
    decisions); in the first months nobody has reputation, so Moderator/Admin handle everything.
- **Q10, events: public event pages, people behind sign-in** (owner, 2026-11-19: "B - and probably only show
  attendees profile picture + first name").
  - Anonymous (and search engines, via SSR + sitemap): title, date/time in the viewer's zone, area, category,
    languages, description, places left. Never the organiser, never attendees. A private place stays an area for
    everyone until the organiser accepts the attendee. A cancelled or past-and-removed event answers 410 and leaves
    the sitemap. A public page only appears for organiser accounts ≥ 7 days old (anti-SEO-spam).
  - Signed in: the organiser and the attendees who opted in to be visible, each shown as **first photo + first name
    only**. Interpretation (owner to correct): the same for the organiser; the full profile card opens only where it
    would anyway (same active mode, mutual filters, and both opted in to "attendees can like each other").
  - Proposed with B and not objected to: any account with a verified email creates events, max. 3 upcoming at a
    time; "attendees can like each other" is opt-in per event and limited to the active mode and mutual filters;
    the event chat is for confirmed attendees only, encrypted like DMs (Q12), closed 7 days after the event;
    the description gets the first-message link/phone check plus a warning in the form.
- **Q15 (UI languages): en, nl, fr, de, es, pt, it** (owner, 2026-11-19: "B"). Translated during development
  (no cost); every screen has a "suggest a better translation" link. The owner can check only nl/fr/en; de/es/pt/it
  stand until users report. Gender agreement (fr/es/pt/it) is written with neutral forms where the language allows.
  Legal texts (privacy policy, terms) are binding in English only; the other versions are marked informational
  until checked. The translations golden test (`SparkTranslationLayersTests`) keeps every key in all seven.
  Spoken languages (Q4) are the full ISO 639-1 list, independent of this.
- **Q9, phone verification: no** (follows from the owner's "i don't have money to spend for other services",
  2026-11-19; SMS costs per message). Email verification only.
- **Q16, funding: none at launch.** The only new running cost is the backup Storage Box (Q18, ~€4/month); the app
  runs on the existing VPS (Q2). Donations can be decided later without affecting the design.

### 10.1 S1/S2: stack query (2026-11-19)

Setup: console app in the session scratchpad (`spikes/stack/Program.cs`, not committed), RavenDB.Client 7.2.6 against
the local RavenDB 7.2.6 (Developer licence, 8-core dev machine, Lucene index), synthetic profiles only: 50 world
cities with Zipf-sized populations (90%, σ ≈ 15 km) + 10% scattered, locations snapped to the 0.009° grid (§5.2),
75% Relationship / 25% Friends, 48/48/4% man/woman/non-binary, 1–3 languages with levels, radius 10/25/50/100 km.
Each run: 300 random viewers, page of 40, mutual radius checked after the query. "Densest city" = viewers within
40 km of the largest city (≈ 200k profiles, more than any real launch will have). Throwaway databases deleted after.

- **Loading:** 1M profiles bulk-inserted in 26–36 s; the stack index non-stale after 163–213 s. At 100k: 4 s / 18 s.
- **S1, first index** (ranges: `BirthDate` between, `AgeMin ≤ myAge ≤ AgeMax`, `LastActiveAt ≥ now−90d`,
  language as `LangTok in (…) and (LangFilterOn = false or LangMin ≤ myLevel)`, plus `spatial.within`):
  100k: p95 54 ms (dense 61 ms), passes. **1M: p95 243 ms, dense 306 ms, fails** the 100 ms bar.
- **Where the time goes (1M, p95 overall / dense):**

  | Variant | p95 | dense p95 |
  |---|---|---|
  | full query, sorted | 243–273 ms | 306–371 ms |
  | same, unsorted | 239 ms | 299 ms |
  | without the two date ranges | 208 ms | 286 ms |
  | mutual radius inside the query (one `spatial.within` per radius setting, OR-ed) | 334 ms | 408 ms |
  | **radius only** (190k hits in the densest city) | **42–47 ms** | **48–54 ms** |
  | **filters only**, no radius | **360 ms** | **411 ms** |

  The spatial clause is cheap; the range and OR filters are evaluated over the whole index. Sorting costs nothing
  measurable. Pushing the candidate's own radius into the query is slower, so that check stays after the query; it
  discards 23% of the fetched rows (an earlier "50%" was a measuring bug: it counted rows dropped by `Take(20)`).
- **Exact terms instead of ranges** (1M): the index stores precomputed tokens, the query sends exact values.
  `Who` = `{mode}:{gender}:{wants}:{age}` per wanted gender (query: `in` over my genders × my age range ≤ 3×82
  terms), `AccAge` = the ages the candidate accepts (query: `= myAge`), `AccLang` = the `{lang}{level}` of a viewer
  the candidate accepts (query: per my language, `LangTok in (…) and AccLang = {lang}{myLevel}`), no date filter.
  **p95 140 ms, dense 178 ms** (half the first index, still above 100 ms). The hybrid (age through a query-time
  `BirthDate` range, no stored age) is **p95 162 ms, dense 217 ms**, same result rows. Unsorted variants are within noise.
- **Two index-map traps** (cost one reindex each): an enum stored as a string cannot be cast to `int` in the map
  (`InvalidCastException: Couldn't convert Man (LazyStringValue) to int32`, 94% of maps failed and Raven marked the
  index errored); and the dynamic map cannot run `Enumerable.Range(…).Where(…)` (`CS1929` on `IEnumerable<int>`).
  So `AccAge`/`AccLang` are **written by the app when the user saves their settings** and copied by the index; they
  do not depend on the clock.
- **The clock in the index.** Age changes on birthdays, and "active within 90 days" changes daily. Options:
  - CronosCore's `DynamicDates` (DeCronosGroep, `CronosCore.RavenDB/Indexes/DynamicDates.cs`): a compare-exchange
    "today" written at midnight and read with `LoadCompareExchangeValue` in ~20 indexes (CareAdmin, Fleet, …).
    **Rejected for the stack:** a change re-maps every profile in the index, 163–213 s of indexing every night at 1M.
  - Query-time ranges (owner's suggestion): correct, no job, but +22 ms p95 / +39 ms dense (the hybrid above).
  - **Chosen:** a stored `Age` that a daily job updates for **today's birthdays only** (~1/365 of profiles,
    ≈ 2.7k documents a day at 1M; 29 February counts as 28 February in non-leap years), and inactivity handled by
    the Q13 sweep, which sets the profile status, so the index drops the profile with no date filter. The query uses
    the age computed from the clock for `AccAge`, so a late job never makes it wrong in that direction.
- **S2, excluding 20k swipes** (1M, worst case: the viewer swiped the first 20k profiles of their own stack order,
  43,770 eligible):

  | Approach | Time for 20 fresh cards |
  |---|---|
  | E1 stream the stack, skip swiped ids in memory | 1.1–1.4 s (streams 20,034 rows), + loading 20k swiped ids 0.17–2.2 s |
  | E2 `not id() in (…20k ids)` | 2.0–2.8 s, + the same id load |
  | **E3 per-viewer cursor on a stable order + cached swiped set** | **112–202 ms** (streams 33 rows) |
  | (100k, 3,849 swipes: E1 245–355 ms, E2 5–48 ms) | |

  **Chosen: E3.** The stack is paged by a cursor over a **stable** per-viewer order, so everything before the cursor
  is already decided and the query starts after it; the swiped/blocked set (kept in memory for active users, or
  one document per user) only catches stragglers. That rules out "least-recently-shown" ordering (D4 updated): the
  order is a random key per profile, with a few independent keys (e.g. 8) and the viewer using
  `key[hash(viewer) % 8]`, so not everyone sees the same profiles first. Profiles that join or move into range land
  anywhere in the order; those behind the cursor are reached when the cursor wraps at the end of the stack (then
  the swiped set does the skipping). Changing a filter does not move the order; widening one resets the cursor.
  Pending likers (Q6) are a separate small query placed in front.
- **Verdict.** S1's 100 ms bar is **not met at 1M** on this machine (p95 140 ms, 178 ms in a 200k-profile city).
  S2's 150 ms bar is met in the worst case (112–202 ms, ≈ the S1 query itself). Accepted because the client
  prefetches the next page while the user swipes through the current one (S7), so a swipe never waits on the query;
  the query only has to be faster than ~20 swipes. **Production will be slower:** the VPS runs the Community licence
  (3 cores, 6 GB, shared with three apps, Q2), so S8 repeats the 1M run under those limits before launch.
- **Spark gap (D7):** nothing here needed Spark changes; spatial stays an app index and a custom endpoint. A generic
  `Spatial` filter in Spark is not justified by this app alone.
