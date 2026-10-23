# mintplayer.com outgoing mail: PRD and plan

Status: **in progress, 2026-10-23.** Steps 1–6 and 8 are done. The sender moved to the apex, `noreply@mintplayer.com`
(see "Sender on the apex"), and step 7 was dropped. The relay moved out of CodeCoverage as `mail-relay` (apps/MailRelay,
`/var/www/mail-relay`) and is pre-staged and healthy on the VPS. The cutover to it happens when the PR merges (see
"Rename the relay"). The first mail from `mail.mintplayer.com` went to Outlook's junk folder. The first one from the
apex arrived in the inbox (step 6).

## Problem

mintplayer.com (`apps/MintPlayer`) is live on the VPS, but it cannot send mail yet. Email
confirmation, password reset and the other auth mails all fail.

Evidence (2026-10-22, on the VPS):

- The app hands mail to the shared relay `code-coverage-coverage-smtp-1` (boky/postfix v4.3.0) as
  `noreply@mail.mintplayer.com` (`apps/MintPlayer/docker-compose.yml:52-56`;
  `/var/www/mintplayer/.env` sets no `MAIL_*` key, so the defaults apply).
- The relay accepts only the senders in `ALLOWED_SENDER_DOMAINS`, which is
  `coverage.mintplayer.com seotester.mintplayer.com` (from `MAIL_SENDER_DOMAIN` in
  `/var/www/code-coverage/.env`). `mail.mintplayer.com` is refused.
- `RELAYHOST` is empty: the relay delivers straight to each recipient's MX over IPv4. Nothing goes
  through Foxxl, so the answer to "VPS or Foxxl?" is the VPS, once the sender is allowed.
- `/var/www/code-coverage/mail-dkim/` holds keys for `coverage.` and `seotester.` only.
- No sent/bounced/deferred lines in the relay log for the past 7 days (not checked whether the log
  restarted with the container).

Current DNS at Foxxl (authoritative `ns10.foxxl.net`):

| Record | Value |
|---|---|
| `mintplayer.com` MX | `10 mx.spam-filter.email.`, `20 fallbackmx.spam-filter.cloud.` |
| `mintplayer.com` TXT (SPF) | `v=spf1 include:_spf.foxxl.com ~all` |
| `_dmarc.mintplayer.com` | `v=DMARC1; p=none; rua=mailto:…@ag.eu.dmarcadvisor.com; sp=reject;` (via CNAME to `_dmarcdefault.foxxl.com`) |

`sp=reject` means a subdomain sender that fails DMARC alignment is **rejected**, not junked. So
`mail.mintplayer.com` needs both SPF and DKIM before the first real mail.

## Decisions already made (from `docs/mintplayer_website_PRD.md`)

- **G3:** send-only, through the existing relay, from a subdomain sender
  (`noreply@mail.mintplayer.com`) with its own DKIM key.
- **G4:** nothing receives mail at `@mintplayer.com`. Null MX, SPF for the relay only, DMARC
  `p=reject`. Check every account's recovery address first.
- **G5:** the zone stays at Foxxl; only the records change.

## Scope

1. `mail.mintplayer.com` as an allowed sender on the relay, with a DKIM key.
2. SPF, DKIM and DMARC records for `mail.mintplayer.com`.
3. A delivered, signed test mail (Gmail + Outlook), DKIM/SPF/DMARC all `pass`.
4. G4 on the apex (null MX, SPF, DMARC), only after the owner confirms the recovery-address check
   and that Foxxl mail is to be cancelled.

Not in scope: inbound mail, a third-party relay (`RELAYHOST` stays empty unless deliverability is
poor), IPv6 sending (the relay is IPv4-only on purpose, see `apps/CodeCoverage/docker-compose.yml:136-145`).

## Plan

| # | Step | Who | Done when |
|---|---|---|---|
| 1 | Generate the DKIM key for `mail.mintplayer.com` (selector `mail`, as `MAIL_DKIM_SELECTOR`). **Flat** layout: `mail-dkim/mail.mintplayer.com.private`, owned `101:104`, mode 600 (gotchas in `apps/CodeCoverage/docker-compose.yml:147-160` and its README "Outgoing mail"). Copy the public TXT value out of the generated `.txt`. | Claude, on the VPS | file present, flat, right owner |
| 2 | Add `mail.mintplayer.com` to `MAIL_SENDER_DOMAIN` in `/var/www/code-coverage/.env` (space-separated, keep the two existing domains). Back up the `.env` first. | Claude, owner approves | `.env` diff shows only that change |
| 3 | DNS at Foxxl for `mail.mintplayer.com`: <br>• `mail._domainkey.mail.mintplayer.com` TXT = the DKIM value from step 1 <br>• `mail.mintplayer.com` TXT = `v=spf1 ip4:188.245.190.60 -all` (IPv4 only, because the relay sends only over IPv4) <br>• `_dmarc.mail.mintplayer.com` TXT = `v=DMARC1; p=reject; adkim=s; aspf=s; rua=mailto:<owner's choice>` | Owner (or Claude in a `playwright_node` browser after the owner signs in) | authoritative `dig` returns all three |
| 4 | Recreate only the relay: `docker compose up -d coverage-smtp` in `/var/www/code-coverage`. Coverage and SeoTester mail pauses for a few seconds; queued mail survives (`smtp-queue` volume). | Claude, owner approves | container up, log shows the DKIM key loaded, not "Skipping DKIM" |
| 5 | `opendkim-testkey -d mail.mintplayer.com -s mail -vvv` inside the relay. | Claude | prints `key OK` |
| 6 | Test mail: trigger a password reset (or register) on mintplayer.com for an owner-owned Gmail and Outlook address. Read the headers. | Owner sends/reads, Claude reads the relay log | relay log `status=sent`; headers show `dkim=pass`, `spf=pass`, `dmarc=pass` |
| 7 | G4 on the apex (only after the owner confirms the recovery-address check and the Foxxl mail cancellation): `mintplayer.com` MX `0 .`, SPF `v=spf1 -all`, `_dmarc.mintplayer.com` `p=reject` (replacing the Foxxl CNAME). | Owner decides, then DNS | records live |
| 8 | Update `docs/mintplayer_website_plan.md` row 0.9 / item 6 and the PRD's S4/Q4 with the outcome and evidence. | Claude | docs say what was done and measured |

Outcome (2026-10-23):

1. **Done.** `mail-dkim/mail.mintplayer.com.private` is flat, `101:104`, mode 600. It is in the old relay's directory and, copied with `cp -a`, in `/var/www/mail-relay/mail-dkim/`.
2. **Done.** The old `.env` was backed up first. `MAIL_SENDER_DOMAIN` lists the three domains, both in `/var/www/code-coverage/.env` (old relay) and in `/var/www/mail-relay/.env` (new relay, the only key there, mode 600).
3. **Done** in Plesk through a `playwright_node` browser. The three records were read back from the authoritative `ns10.foxxl.net`. The `rua` first reused the apex's dmarcadvisor address. That address turned out to be Foxxl's default (the apex `_dmarc` was a CNAME to `_dmarcdefault.foxxl.com`), so it was dropped the same day (Q1).
4. **Done.** The old `coverage-smtp` was recreated with the key loaded. The new `mail-relay` is up and `healthy`. Its OpenDKIM SigningTable lists all three domains, and it sits on `coverage-mail`, `seo-mail`, `mintplayer-mail` and `mail-relay_mail-relay-egress`. The log shows no error, only the benign hash→lmdb notice.
5. **Done, by a different method.** `opendkim-testkey` times out inside the relay (it has no resolver for it). Run in a throwaway alpine container against the same key, it prints `key OK`. The method is in apps/MailRelay/README.md, "Adding a sender domain".
6. **Delivered, to junk.** The owner's password reset of 2026-10-23 06:02 UTC was signed `d=mail.mintplayer.com` and accepted by Outlook (`relay=msn-com.olc.protection.outlook.com`, `status=sent`, 250 2.6.0). It landed in the junk folder. The owner then asked for `noreply@mintplayer.com` as the sender (see "Sender on the apex"). **Inbox, 06:17 UTC:** the next reset, sent from `…@mintplayer.com` and signed `d=mintplayer.com`, was accepted by Outlook (`status=sent`, 250 2.6.0). The owner confirmed it arrived in the inbox.
7. **Dropped.** The owner said Foxxl won't be used any more (Q2), but the apex now *sends*, and a null MX on a sending domain gets its mail refused (Postfix's `reject_unknown_sender_domain` treats `MX 0 .` as an unknown sender domain). The apex SPF and DMARC are now the sending records under "Sender on the apex". The MX stays at Foxxl's spam filter until the hosting is cancelled, and must then point at something that accepts mail.
8. **Done.** `docs/mintplayer_website_plan.md` item 6 and PRD row A6 are updated. The superseded relay text in the SeoTester PRD, raven-tls-migration and multi-forge-plan carries a note.

## Sender on the apex (owner, 2026-10-23)

Owner: "I'd rather prefer noreply@mintplayer.com and/or noreply@coverage.mintplayer.com" and "Foxxl won't be used anymore
in the future". mintplayer.com now sends as `noreply@mintplayer.com`. `coverage.mintplayer.com` stays CodeCoverage's
sender, so each app keeps its own domain and reputation.

- **Key:** `mintplayer.com.private` (selector `mail`, 2048-bit, `101:104`, 600) is in both relays' `mail-dkim/`, and
  `mintplayer.com` is in both `.env`s' `MAIL_SENDER_DOMAIN` (each `.env` backed up first). Both relays were recreated and
  are healthy, and both SigningTables list `*@mintplayer.com`.
- **DNS** (Plesk, read back from `ns10.foxxl.net`):
  - `mintplayer.com` TXT `v=spf1 ip4:188.245.190.60 -all`, replacing `include:_spf.foxxl.com ~all`
  - `mail._domainkey.mintplayer.com` TXT: the DKIM key, `opendkim-testkey`: `key OK`
  - `_dmarc.mintplayer.com` TXT `v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s`, replacing the CNAME to Foxxl's
    default (`p=none`, Foxxl's `rua`)
  - `_dmarc.mail.mintplayer.com` lost its `rua` (Foxxl's address).
- **App:** `MAIL_FROM_ADDRESS` defaults to `noreply@mintplayer.com` in `apps/MintPlayer/docker-compose.yml`. The
  server's copy was patched the same way (backup `docker-compose.yml.bak-fromapex`). The app was recreated and is
  healthy, and its environment shows the new address.
- `mail.mintplayer.com` stays allowed and keeps its records, so the few mails sent from it still verify.

## Deliverability

The relay does its part: every mail is DKIM-signed, SPF and DMARC align, and the PTR matches the HELO. Outlook.com
junking the first mails is the usual treatment of a new sending IP and domain with no history. It is reputation, not
configuration. What helps:

- Mark the test mails "Not junk" in Outlook and add the sender to the contacts.
- Register the IP `188.245.190.60` with Microsoft SNDS and JMRP (sendersupport.olc.protection.outlook.com/snds). If
  mail keeps going to junk, file the Outlook.com sender-support form for the IP.
- Read a received mail's `Authentication-Results` header (Outlook: "View message source") to confirm `dkim=pass`,
  `spf=pass`, `dmarc=pass` for `mintplayer.com`.

## Rename the relay (owner, 2026-10-22)

The relay serves CodeCoverage, SeoTester and mintplayer.com, so `coverage-smtp` is a misleading
name. Rename it to something coverage-neutral, in the same PR as the mail work.

What the name touches (grep `coverage-smtp|seo-mail|mintplayer-mail|smtp-queue`, 180 hits in 24 files):

- **The compose service** in `apps/CodeCoverage/docker-compose.yml` (plus `coverage-smtp-egress`, its
  outbound network). The service name is also the **DNS name the apps dial**: `MAIL_HOST` defaults
  to `coverage-smtp` in all three compose files and `.env.example`s.
- **The deploy workflows' guards:** `mintplayer-deploy.yml:171` (`coverage-smtp:mintplayer-mail`) and
  `seotester-deploy.yml:308` check by container name that the relay is on the shared network.
- **The structure tests:** `tools/verify-backend-networks.test.mjs` (network membership, egress list,
  the workflow-line regexes), `verify-mintplayer-deploy`, `verify-seotester-deploy`.
- **`apps/CodeCoverage/ops/raven-cert-renew.sh`:** mails failures through `smtp://coverage-smtp:587`
  on `${CC_PROJECT}_coverage-mail`.
- **The test fixture** `CodeCoverage.Tests/Fixtures/Manifests/codecoverage.docker-compose.yml.txt`
  (a manifest-parser fixture: probably leave it as is, it is a sample, not our config).
- **Docs:** `apps/CodeCoverage/README.md`, `docs/guide-outgoing-mail.md`, `docs/code-coverage/hosting.md`,
  `docs/seotester/deploy.md`, `docs/seo_analyzer_PRD.md`, `raven-tls-migration.md`.

Not renamed: `coverage-mail` (CodeCoverage's own app↔relay network, correctly named), `seo-mail`
and `mintplayer-mail` (already per app), `smtp-queue` (already neutral; the volume keeps its
`code-coverage_` project prefix, so queued mail survives).

**Rollout trap.** The three apps deploy separately. If CodeCoverage deploys first with only the new
name, SeoTester and mintplayer.com still dial `coverage-smtp` and their mail fails until they
redeploy. Fix: give the renamed service `aliases: [coverage-smtp]` on `seo-mail` and
`mintplayer-mail` for one release, redeploy all three, then drop the alias. Or deploy all three in
one go after the merge, since all three workflows trigger from the same master push if the PR
touches all three apps.

**Decision R1, the name.** Recommendation: `mail-relay` (service), `mail-relay-egress` (network);
container becomes `code-coverage-mail-relay-1`.

**Decision R2, where it lives.** Recommendation (A): rename only, keep it in the CodeCoverage
compose project. (B) would move it into its own compose project (`/var/www/mail-relay`, its own
deploy workflow, DKIM dir and queue volume moved). B removes the `code-coverage-` prefix as well, but
it is several times the work and a queue-volume migration. Owner to choose.

**Decided (owner, 2026-10-22: "should also no longer be called codecoverage-something … reconfigure the docker
networks"): R1 = `mail-relay`, R2 = B.** The relay is its own compose project, `apps/MailRelay`, deployed to
`/var/www/mail-relay` by `.github/workflows/mail-relay-deploy.yml`. Its container is named `mail-relay` and its
outbound network is `mail-relay-egress`. What changed against the analysis above:

- **No queue migration.** The old `smtp-queue` was empty when checked (`postqueue -p`: "Mail queue is empty"). The
  new relay starts with its own volume.
- **Networks:** every app has a pairwise internal network to the relay: `coverage-mail` (now external, like the other
  two), `seo-mail` and `mintplayer-mail`. Each deploy that uses one creates it `--internal` with the same loop.
  `tools/verify-backend-networks.test.mjs` asserts that the loop body is identical across deploys.
- **No alias needed.** The new relay was pre-staged on the VPS on 2026-10-23 and is already on all three networks. At
  the merge, each app's deploy switches `MAIL_HOST` to `mail-relay`, so the order of the deploys doesn't matter.
  CodeCoverage's `down --remove-orphans` then removes `coverage-smtp`. An app deploy only warns while `mail-relay` is
  missing, because MailManager retries through the messaging queue.
- **After the cutover:** remove the relay `MAIL_*` keys from `/var/www/code-coverage/.env` (a backup exists), and
  `docker volume rm code-coverage_smtp-queue` once it is confirmed empty.

## Open questions for the owner

- Q1. DMARC report address for `mail.mintplayer.com`: keep the dmarcadvisor `rua`, or none? **None.** The
  dmarcadvisor address was Foxxl's default, not the owner's (2026-10-23).
- Q2. Step 7 (apex null MX): is any mailbox at `@mintplayer.com` (Foxxl) still in use, e.g. the
  `android1@` forwarding from the backlog? If yes, step 7 waits. **Answered: "Foxxl won't be used anymore". Step 7 was
  dropped anyway, because the apex now sends.**

- Q3. R1 (name) and R2 (rename only vs own compose project), see "Rename the relay". **Answered: `mail-relay`, own
  project.**

Order: do the rename (code, one PR) before steps 2 and 4, so the relay is recreated once, under its
new name, with `mail.mintplayer.com` already allowed.

## Related, not mail: IPv6 records

Found while issuing the mintplayer.com certificate on 2026-10-22. The VPS's IPv6 address is
`2a01:4f8:c0c:f87c::1`; `2a01:4f8:c0c:f87c::` (the subnet address) does not answer (letsdebug:
"AAAANotWorking", 10 s timeout). The apex now points at `::1` and its certificate was issued at
19:47 UTC. The other subdomains (2048, video-player, bootstrap, coverage, …) still have
`AAAA 2a01:4f8:c0c:f87c::`. Their certificates renew only through Let's Encrypt's IPv4 fallback,
and IPv6 visitors wait for a timeout first. Change them all to `::1`.
