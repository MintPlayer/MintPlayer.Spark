# MailRelay — the shared outgoing mail relay

One `boky/postfix:v4.3.0` container, `mail-relay`, that CodeCoverage, SeoTester and mintplayer.com hand their mail
to. It queues the mail and delivers it, and it DKIM-signs each message for its sender's domain. Each app keeps its own
sender address (`MAIL_FROM_ADDRESS` in that app's `.env`). The relay only decides which sender domains it accepts and
signs.

It is its own compose project in `/var/www/mail-relay`, deployed by
[`.github/workflows/mail-relay-deploy.yml`](../../.github/workflows/mail-relay-deploy.yml). Until 2026-10 it was the
`coverage-smtp` service inside CodeCoverage's stack; the decision and the cutover are in
[docs/mintplayer_mail_plan.md](../../docs/mintplayer_mail_plan.md). The general guide, with bounces and the provider
fallback, is [docs/guide-outgoing-mail.md](../../docs/guide-outgoing-mail.md).

**The apps never talk to the internet.** Each app hands its message over port 587 on an internal network, and the
handoff takes milliseconds whether or not the recipient's server is up. That matters because some sends happen inside
a sign-in callback. Spark's MailManager retries through the messaging queue, so if the relay is down, mail is only
delayed.

## Topology

| Network | Members | Kind |
|---|---|---|
| `coverage-mail` | `coverage-app`, `mail-relay` | external, created `--internal` by the CodeCoverage and relay deploys |
| `seo-mail` | `seotester-app`, `mail-relay` | external, created `--internal` by the SeoTester and relay deploys |
| `mintplayer-mail` | `mintplayer-app`, `mail-relay` | external, created `--internal` by the MintPlayer and relay deploys |
| `mail-relay_mail-relay-egress` | `mail-relay` | the relay's only way out (to recipients' MX on port 25) |

The relay never shares a network with RavenDB, and no app gets a route out through it.
`tools/verify-backend-networks.test.mjs` enforces both. Any deploy creates a shared network when it is missing, so
the deploys can run in any order. An app deployed before the relay logs a warning, and its mail waits in its queue.

## Sender domains

| Domain | App | `From` | Records (on `mintplayer.com`'s zone at Foxxl) |
|---|---|---|---|
| `coverage.mintplayer.com` | CodeCoverage | `no-reply@coverage.mintplayer.com` | SPF, `mail._domainkey.coverage` |
| `seotester.mintplayer.com` | SeoTester | `no-reply@seotester.mintplayer.com` | SPF, `mail._domainkey.seotester` |
| `mintplayer.com` | mintplayer.com | `noreply@mintplayer.com` | SPF on the apex (`v=spf1 ip4:188.245.190.60 -all`, Foxxl's include dropped), `mail._domainkey`, `_dmarc` (`p=reject`, strict alignment) |
| `mail.mintplayer.com` | none since 2026-10-23 (mintplayer.com's first sender; kept allowed) | — | SPF, `mail._domainkey.mail`, `_dmarc.mail` |

The apex keeps its MX records: a sending domain needs a working MX. Postfix's `reject_unknown_sender_domain`, for
example, refuses a sender domain that publishes a null MX (RFC 7505), so `MX 0 .` is not an option here. Before the
Foxxl hosting is cancelled, point the MX at something that accepts mail.

`MAIL_SENDER_DOMAIN` in `/var/www/mail-relay/.env` lists them, separated by spaces. The relay refuses any other
sender (`554 5.7.1 ... Access denied`), so it cannot become an open relay. The deploy refuses to start when a listed
domain has no DKIM key.

## Server setup

```
/var/www/mail-relay/
├── docker-compose.yml      # replaced by every deploy
├── .env                    # yours (see .env.example); the deploy fails without it
└── mail-dkim/              # owned 101:104, 700
    └── <domain>.private    # one per sender domain, FLAT, 600
```

The queue lives on the `mail-relay_smtp-queue` volume, so a redeploy loses nothing. Recreating the container is safe.

## Adding a sender domain

1. Generate the key on the VPS:

   ```bash
   cd /var/www/mail-relay/mail-dkim
   d=example.mintplayer.com
   docker run --rm -v "$PWD":/out alpine:3.20 sh -c \
     "apk add --no-cache opendkim-utils >/dev/null && mkdir -p /out/$d && opendkim-genkey -b 2048 -d $d -s mail -D /out/$d"
   cp "$d/mail.private" "$d.private"
   chown -R 101:104 . && chmod 600 *.private "$d"/* && chmod 700 .
   cat "$d/mail.txt"          # the public half, for DNS
   ```

2. Publish the DNS records. Under `mintplayer.com`'s DMARC `sp=reject`, a subdomain that fails alignment is
   **rejected**, not junked:

   ```
   <sub>                    TXT  v=spf1 ip4:188.245.190.60 -all
   mail._domainkey.<sub>    TXT  v=DKIM1; k=rsa; p=<public key, one line>
   _dmarc.<sub>             TXT  v=DMARC1; p=reject; adkim=s; aspf=s; rua=mailto:...   (optional; else sp=reject applies)
   ```

   At Foxxl (Plesk), click **Update** after adding the records, or nothing is published.

3. Add the domain to `MAIL_SENDER_DOMAIN` in `.env`, then `docker compose --env-file .env up -d` (or rerun the
   deploy).

4. Verify the key against the published record. This is the check that proves it:

   ```bash
   docker run --rm -v /var/www/mail-relay/mail-dkim:/keys:ro alpine:3.20 sh -c \
     'apk add --no-cache opendkim-utils bind-tools >/dev/null &&
      opendkim-testkey -d example.mintplayer.com -s mail -k /keys/example.mintplayer.com.private -vvv'
   ```

   `key OK` means the key and DNS agree. Run it in a throwaway container like this one. Inside `mail-relay`,
   `opendkim-testkey` times out even for a good key, because of that container's resolver.

⚠️ **Two layout traps, each of which cost a deploy cycle:**

1. **The key must be flat**: `mail-dkim/<domain>.private`. `opendkim-genkey` writes `<domain>/<selector>.private`.
   The image does not read that path, logs `Skipping DKIM`, and delivers **unsigned**. SPF alone still passes, so
   the failure is invisible unless you read the log or the headers.
2. **The key must be owned by opendkim on the host** (`101:104` in this image). Otherwise the image tries to `chown`
   it through the read-only mount and exits.

## Delivery facts (measured against this VPS)

| | |
|---|---|
| Hetzner blocks outbound **25 and 465** on Cloud Servers | Unblocked for this VPS on request (Limits page). Port 587 is open by default, so a provider fallback needs no request. |
| Reverse DNS must match the HELO name | The IPv4 PTR is `coverage.mintplayer.com`, so `MAIL_HELO_HOSTNAME` stays that for every app's mail. There is one name per address. |
| IPv4 only | `POSTFIX_inet_protocols=ipv4`. The VPS sends IPv6 from `2a01:4f8:c0c:f87c::1`, which has no PTR and is not the address SPF names. Gmail and Outlook refuse that. `smtp_address_preference=ipv4` alone was not enough. |

**If deliverability degrades**, set `MAIL_RELAY_HOST` (with username and password) to a provider's submission host.
Postfix then relays instead of delivering directly, and the provider's reputation replaces this IP's.

## Checking delivery

```bash
docker logs mail-relay --since 1h 2>&1 | grep -E 'status=(sent|bounced|deferred)'
docker exec mail-relay postqueue -p        # what is still queued
```

In a received message's headers, look for `dkim=pass header.d=<domain>`, `spf=pass` and `dmarc=pass`.
