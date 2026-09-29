# Outgoing mail from a Spark app on a VPS

How to make a Spark application send transactional mail that actually arrives, and why nearly every
step of it is about DNS rather than code.

Every measured claim below was verified against `coverage.mintplayer.com` on a Hetzner Cloud server
on **2026-09-21**, sending to an Outlook/Hotmail mailbox. Where something is a recommendation
rather than a measurement, it says so.

> **The wiring is the easy part.** A working SMTP setup that nobody receives looks exactly like a
> working SMTP setup. Plan to spend your time on reverse DNS, SPF, DKIM and DMARC, and verify
> against a real external mailbox — a local capture proves nothing about delivery.

---

## 0. The shape, in one table

| | |
|---|---|
| **Spark ships** | `MintPlayer.Spark.MailManager` (#460 M8): templates, queueing, SMTP / Mailpit / pickup / custom transports, suppression, bounces — all configured under `Spark:Mail` |
| **Spark does not ship** | a default transport: with MailManager added and none configured, startup refuses — see §1 |
| **The app ships** | its `Spark:Mail` settings and, if it wants, its own `Templates/Mail/*.mjml` |
| **The relay ships** | queueing, retry, DKIM signing and delivery |

⚠️ **The app does not talk to the internet.** A mail is queued on a Spark Messaging lane and a worker
hands it to a relay container one hop away. The request that caused it (a sign-up, a password reset,
an external-login callback) never waits on SMTP at all, and a relay outage delays mail instead of
failing requests: the `mail-transactional` lane retries for about four hours, and a mail whose token
would be dead by then expires instead of arriving late.

---

## 1. Why there is no silent default transport

ASP.NET Identity `TryAdd`s a no-op `IEmailSender`, so an application with no mail configured still
*resolves* one and every message is silently discarded. Absence is invisible; the only way to
detect it is to recognise an internal type by name, which fails **open** the day that type is
renamed.

Spark therefore refuses the configurations whose mail would go nowhere, at startup:

- `spark.AddMailManager()` with no registered transport and neither `Spark:Mail:Smtp:Host` nor
  `Spark:Mail:PickupFolder`, or with no `Spark:Mail:From:Address`.
- `SparkExternalLoginLinking.ConfirmByEmail` without a link-confirmation sender (MailManager registers
  one; without MailManager there is none): a confirmation nobody sends is a link nobody makes.
- A registration surface (`LocalCredentials = Full`) whose account mail goes to Identity's no-op
  (#460, D6): nobody who registers would ever get a confirmation or reset link. Opt out with
  `Spark:Auth:AllowUnconfirmedRegistration=true`.

A development or demo app that should not send real mail uses the pickup folder
(`Spark:Mail:PickupFolder`), which writes every mail as an `.eml` file — HR and Fleet do. A staging or
test deployment that must send through the real relay but never reach real users sets
`Spark:Mail:Development:RedirectTo`: every mail goes to that one address (the original recipient in
`X-Spark-Original-To`), with a warning per mail outside Development. Startup **refuses** it in the
Production environment (`IHostEnvironment.IsProduction()`), where it would divert every user's mail.

The transport is registered explicitly — `UseSmtpTransport()`, `UseMailpitTransport()`,
`UsePickupFolderTransport()` or `AddMailTransport<T>()` after `spark.AddMailManager()` — or, with none
registered, picked from the configuration as above (`Smtp:Host`, else `PickupFolder`). Two
registrations refuse startup. Each transport states whether it `DeliversToRealRecipients`: SMTP does
(unless its host is loopback), Mailpit and the pickup folder do not, a custom transport does unless it
says otherwise.

Development **fails closed**: in the Development environment a transport that delivers to real
recipients refuses startup unless `Spark:Mail:Development:RedirectTo` is set (put it in user secrets:
`dotnet user-secrets set "Spark:Mail:Development:RedirectTo" "you@example.com"`). To see the mail
instead, run Mailpit — `docker run -d --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit` — and
register `spark.AddMailManager().UseMailpitTransport()` (`localhost:1025`, no TLS, no auth; override
with `Spark:Mail:Mailpit:{Host,Port,Tags}`). It sends real SMTP, so Mailpit shows the exact MIME, and
tags each mail with its template and lane (`X-Tags`); the UI is at http://localhost:8025. Mailpit, the
pickup folder and loopback SMTP need no redirect.

Or let the app start Mailpit: `Spark:Mail:Mailpit:AutoStart=true` (Development only; ignored
elsewhere). `Mode=Binary` (default) runs an installed `mailpit` — never downloaded; install it with
`winget install axllent.mailpit` or `scoop install mailpit` — and `Mode=Docker` runs the pinned
`axllent/mailpit:v1.31.3` image as container `spark-mailpit`. It never blocks startup, reuses whatever
already listens on the SMTP port or an existing container of that name, and at shutdown stops only
what it started. On Windows the binary is tied to the host with a kill-on-close Job Object, so it dies
even when the host crashes; on Linux and macOS a leftover from a crash is adopted by the next run. The
MailManager README lists every option.

---

## 2. What your host blocks before you write any code

**Check this first.** On Hetzner Cloud, outbound **25 and 465 are blocked by default on every
server**; 587 is open. Measured on a server that had never sent mail:

```
outbound 25  -> BLOCKED
outbound 465 -> BLOCKED
outbound 587 -> OPEN
```

That decides your architecture before anything else:

| | Needs port 25 | Deliverability rests on |
|---|---|---|
| **Direct to MX** — your relay talks to each recipient's mail server | yes | your IP's reputation, and your DNS |
| **Smarthost relay** — your relay forwards to a provider | no, 587 is enough | the provider's reputation |

Unblocking is a support request from the console's Limits page, usually granted within minutes.
Ask for it only if you want direct delivery; a relay through a provider needs nothing.

⚠️ **The unblock does not make mail deliverable.** It only lets you connect. See §3.

---

## 3. The three DNS facts that decide whether mail arrives

### 3.1 Reverse DNS should match what you announce

A fresh Hetzner server has a generic PTR like `static.60.190.245.188.clients.your-server.de`. Set
it, in the Hetzner console under the server's **Networking → Reverse DNS**, to a hostname that
forward-resolves back to that address — and announce the *same* name in HELO.

⚠️ **Measured, and weaker than the usual advice:** Outlook accepted mail from this server while the
PTR was still the generic Hetzner name and HELO said `coverage.mintplayer.com` — a plain mismatch.
So this is hardening, not a precondition; SPF and DKIM did the work. Other receivers are stricter,
and a mismatch is a free thing to fix, so fix it — but do not go hunting here when mail is being
rejected. Read the rejection text first: it names the actual reason, as §3.2 shows.

### 3.2 A parent domain's DMARC policy applies to your subdomain

This is the one that surprises people. A DMARC record has two policies:

```
v=DMARC1; p=none; rua=mailto:...; sp=reject;
```

`p` governs the domain itself; **`sp` governs every subdomain**. A parent that is relaxed about its
own mail can be absolute about subdomains, and here it was: `p=none` but `sp=reject`. Mail from
`coverage.mintplayer.com` was therefore **rejected outright**, not junked:

```
550 5.7.509 Access denied, sending domain [COVERAGE.MINTPLAYER.COM]
does not pass DMARC verification and has a DMARC policy of reject.
```

Read the parent's `_dmarc` record before assuming a subdomain inherits nothing.

### 3.3 DMARC needs SPF *or* DKIM to pass, aligned — publish both

Either one passing is enough, so a correct SPF alone will deliver. Publish both anyway, because
they fail differently:

- **SPF** breaks the moment a recipient forwards the message — the forwarding server's IP is not in
  your record.
- **DKIM** is a signature over the message and survives forwarding.

```
coverage                    TXT   v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all
mail._domainkey.coverage    TXT   v=DKIM1;k=rsa;p=<public key>
```

(The example is CodeCoverage's record as published. Its `ip6:` entry is the one mistake described
next: it names the AAAA's address, not the one the host sends from.)

⚠️ **SPF must name the address you *send* from, not the address your AAAA points at.** On a host
with IPv6, Postfix may deliver over IPv6 to any MX that publishes an AAAA, and the receiver checks
the *source* address of that connection. Those are often different: a Hetzner server is typically
given a /64, its AAAA points at the subnet's `::` (`…::0`), and the kernel sends from `…::1`. An
`ip6:` entry naming `…::` then authorises an address that never sends, the real one fails `-all`,
and the failure is intermittent, because it depends on which family each *recipient* publishes.
Measure the source address (`ip -6 route get <an MX's IPv6>` shows the `src`), and list that
address, or the whole /64 (`ip6:2a01:db8:1:2::/64`).

⚠️ **Send over IPv4 only unless IPv6 is fully set up.** The large receivers hold IPv6 senders to a
stricter standard: a PTR for the *sending* v6 address that forward-resolves to your HELO name, and
SPF coverage for it. Cloud providers set PTRs per address and usually leave v6 unconfigured. When
either is missing, set `inet_protocols = ipv4`, which makes Postfix never try IPv6.
`smtp_address_preference = ipv4` is **not** a substitute: it only *prefers* IPv4 and still falls
back to IPv6 when an IPv4 attempt fails, which is exactly the delivery that then fails SPF.

*Measured on CodeCoverage (2026-09-29):* the host sent IPv6 from `2a01:4f8:c0c:f87c::1`, the AAAA
and SPF named `2a01:4f8:c0c:f87c::`, and `::1` had no PTR, so every IPv6 delivery failed SPF and
Gmail and Outlook refused it. The relay now runs with `inet_protocols=ipv4`; IPv4 has a matching
PTR and passes.

---

## 4. The relay container

```yaml
coverage-smtp:
  image: boky/postfix:v4.3.0
  environment:
    - ALLOWED_SENDER_DOMAINS=coverage.mintplayer.com
    - HOSTNAME=coverage.mintplayer.com          # must match reverse DNS
    - DKIM_SELECTOR=mail
    - POSTFIX_inet_protocols=ipv4                # never IPv6 unless its PTR + SPF are set (§3.3)
    - RELAYHOST=                                 # empty = direct to MX
  volumes:
    - ./mail-dkim:/etc/opendkim/keys:ro
    - smtp-queue:/var/spool/postfix              # survives recreation
  networks:
    - coverage-internal                          # no published ports
```

⚠️ **This is not an inbound mail server.** It publishes no ports and sits only on the internal
network, so nothing outside the compose project can reach it — which is also what makes running it
without authentication safe. `ALLOWED_SENDER_DOMAINS` is the second lock: an envelope sender from
any other domain is refused rather than relayed, so a bug cannot turn it into an open relay.

The named queue volume matters more than it looks. Without it, a redeploy during a downstream
outage discards whatever was queued, and the messages are simply gone.

---

## 5. DKIM, and the two traps in the key layout

Generate on the server:

```bash
cd /var/www/<app>/mail-dkim
docker run --rm -v "$PWD":/out alpine:3.20 sh -c \
  'apk add --no-cache opendkim-utils && opendkim-genkey -b 2048 -d <domain> -s mail -D /out'
```

Then two fixes, **both of which cost a deploy cycle here**:

1. **The key must be flat**: `mail-dkim/<domain>.private`. `opendkim-genkey` writes
   `<domain>/<selector>.private`, and with that layout the image logs `Skipping DKIM` and delivers
   the message **unsigned**. ⚠️ It still *arrives*, because SPF passes on its own — so this failure
   is invisible unless you read the container log or the received headers. It is the worst kind of
   bug: the feature looks finished.
2. **The key must be owned by opendkim on the host** — `101:104` in `boky/postfix:v4.3.0`, and the
   image prints the pair at startup. It tries to `chown` a key it cannot read, the read-only mount
   refuses, and the container exits. This one at least fails loudly.

```bash
mv <domain>/mail.private <domain>.private
chown -R 101:104 . && chmod 600 *.private
```

**Verify against what is actually published** — this is the check that proves it, rather than
proving the container started:

```bash
docker run --rm -v /var/www/<app>/mail-dkim:/keys:ro alpine:3.20 sh -c \
  'apk add --no-cache opendkim-utils bind-tools >/dev/null &&
   opendkim-testkey -d <domain> -s mail -k /keys/<domain>.private -vvv'
```

`key OK` means the private key and the DNS record agree. `key secure` additionally means the record
was DNSSEC-validated. Anything else usually means the base64 was line-wrapped when pasted into the
DNS panel.

---

## 6. Proving it end to end

Do not trust a local capture. Send to a real mailbox at a strict provider — Outlook/Hotmail is the
harshest useful test — from a **throwaway** container, so the running stack is untouched:

```bash
docker run -d --name smtp-test \
  -e ALLOWED_SENDER_DOMAINS=<domain> -e HOSTNAME=<domain> -e DKIM_SELECTOR=mail \
  -e POSTFIX_inet_protocols=ipv4 \
  -v /var/www/<app>/mail-dkim:/etc/opendkim/keys:ro \
  boky/postfix:v4.3.0

# ... send one message with sendmail -f <from> -t ...

docker logs smtp-test 2>&1 | grep -E 'status=|dkim'
docker exec smtp-test postqueue -p          # empty = nothing deferred
docker rm -f smtp-test
```

What to look for:

| Log line | Means |
|---|---|
| `status=sent ... 250 ... Queued mail for delivery` | accepted by the receiver |
| `status=bounced ... 550 5.7.509` | DMARC — see §3.2 |
| `status=deferred` | could not connect; check §2 |
| `Skipping DKIM for domain ...` | unsigned, and it will still arrive — see §5 |

Then open the received message and read `Authentication-Results`. Landing in the inbox is the goal;
`dkim=pass` and `spf=pass` are the reasons it did.

---

## 7. Falling back to a provider

If deliverability degrades — a shared-IP neighbour gets listed, or a receiver starts junking you —
set `RELAYHOST` (plus username and password) to a provider's submission host. Postfix then relays
instead of delivering directly, the port-25 unblock stops mattering, and the provider's reputation
replaces your IP's. Nothing in the application changes: it is still handing a message to a
container one hop away.

This is also the right default if you never got the unblock, and the only option on hosts that will
not grant one.

---

## 8. Templates, bulk mail and bounces

The earlier version of this section said bulk mail and a mail manager were out of scope. Both are now
in MailManager (#460 M8); its README (`libs/mail/MintPlayer.Spark.MailManager/README.md`) is the
reference. What matters for the relay:

### 8.1 Templates

MJML + Scriban files in the app's repository, one per culture with a fallback chain
(`Name.nl-BE.mjml` → `Name.nl.mjml` → `Name.mjml`). The account mails ship as embedded defaults in
`en` and `nl`; an app overrides one by adding a file with the same name under `Templates/Mail/`.

### 8.2 Bulk mail and pacing

Campaigns go through `ISparkMailer.SendCampaignAsync`: one message per recipient on the `mail-bulk`
lane, throttled to 20 a minute by default (`Spark:Messaging:Queues:mail-bulk:MaxPerInterval` /
`Interval`), each with one-click `List-Unsubscribe`. Never one mail with many BCCs. How lanes are
throttled (GCRA slots, burst allowance, deferral without burning retries, `ExpiresAtUtc`) is in the
[Messaging README § Per-queue options and throttling](../libs/messaging/MintPlayer.Spark.Messaging/README.md#per-queue-options-and-throttling);
the lane defaults are in the [MailManager README](../libs/mail/MintPlayer.Spark.MailManager/README.md).

The lane paces what the app hands to the relay; the relay paces what it sends to each receiving
domain. For a burst to one large provider, also set Postfix's per-destination pacing (a
recommendation, not measured here):

```yaml
# docker-compose.yml, relay service
- POSTFIX_smtp_destination_concurrency_limit=2   # parallel connections per receiving domain
- POSTFIX_smtp_destination_rate_delay=1s         # pause between deliveries to the same domain
```

A self-hosted relay on a fresh IP still has no sending reputation: large volumes belong on a
provider relay (`RELAYHOST`, §7), whatever the pacing.

### 8.3 Bounces — tier 1: the local relay pipes them to the app

With `Spark:Mail:Bounces:VerpDomain` set, every mail's envelope sender is
`bounces+{deliveryId}@{VerpDomain}`. When the **relay itself** fails to deliver (the receiver refuses
with a 5xx), Postfix generates the bounce and addresses it to that envelope sender. Routing that domain
to a pipe hands the bounce to Spark — no MX record and no inbound port 25 needed. Measured against
`boky/postfix:v4.3.0` (spike S-M5, #460): the image has `curl` (no `wget`), sources every
`/docker-init.db/*.sh` after its own configuration, and `postconf -M` adds the `master.cf` entry.

`init/50-spark-bounces.sh`, mounted at `/docker-init.db/`:

```bash
#!/bin/bash
postconf -e "relay_domains = ${VERP_DOMAIN}"
postconf -e "transport_maps = inline:{ ${VERP_DOMAIN}=sparkbounce: }"
postconf -e "recipient_delimiter = +"
postconf -e "sparkbounce_destination_recipient_limit = 1"
postconf -M "sparkbounce/unix=sparkbounce unix - n n - - pipe flags=Rq user=nobody argv=/usr/local/bin/spark-bounce \${original_recipient}"
# The pipe runs with an empty environment, and the image unsets secret variables after this
# script: URL and secret reach the pipe as files.
printf '%s' "${SPARK_BOUNCE_URL}" > /etc/postfix/spark-bounce-url
printf '%s' "${SPARK_BOUNCE_SECRET}" > /etc/postfix/spark-bounce-secret
chown nobody /etc/postfix/spark-bounce-url /etc/postfix/spark-bounce-secret
chmod 0400 /etc/postfix/spark-bounce-url /etc/postfix/spark-bounce-secret
```

`spark-bounce`, mounted at `/usr/local/bin/spark-bounce`:

```sh
#!/bin/sh
# stdin = the bounce; $1 = the VERP address.
# Exit 0: delivered (2xx), or dropped because the endpoint can never accept this report
#         (400 unparseable, 413 too large, 422 unprocessable).
# Exit 75 (EX_TEMPFAIL): anything else -- 401/403 (wrong secret), 404 (wrong URL), 429,
#         503 (endpoint disabled),
#         other 5xx, no answer -- so
#         Postfix keeps the bounce queued and retries until the operator fixes the cause.
# Postfix appends the command's output to the delivery's log line, on success too.
recipient=$(printf '%s' "$1" | sed 's/+/%2B/g; s/@/%40/g')
status=$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --max-time 30 \
  -H "Authorization: Bearer $(cat /etc/postfix/spark-bounce-secret)" \
  -H "Content-Type: message/rfc822" \
  --data-binary @- \
  "$(cat /etc/postfix/spark-bounce-url)?recipient=${recipient}")
case "$status" in
  2??) exit 0 ;;
  400|413|422)
    echo "spark-bounce: dropped, the endpoint answered $status and will never accept this report"
    exit 0 ;;
  *)
    echo "spark-bounce: the endpoint answered ${status:-nothing}; Postfix will retry"
    exit 75 ;;
esac
```

| Endpoint answer | Exit | Postfix (measured, S-M5b) |
|---|---|---|
| 2xx | 0 | `status=sent`, queue empty |
| 400, 413, 422 | 0 | `status=sent (… (spark-bounce: dropped, the endpoint answered 400 …))`, queue empty |
| 401, 403, 404, 429, 500, 503 (503 = endpoint disabled) | 75 | `status=deferred (temporary failure. Command output: spark-bounce: the endpoint answered 401; …)`, kept |
| unreachable | 75 | `status=deferred (… curl: (6) Could not resolve host … answered 000 …)`, kept |

Why a dropped report exits 0 and not a permanent-failure code: exit 69 (`EX_UNAVAILABLE`) was measured
too: Postfix logs `status=bounced (service unavailable …)` and removes the message without sending a
notification (its sender is empty) — the same drop, logged as a delivery failure of the bounce itself.
`postlog` is not an option for the log line: it lives in `/usr/sbin`, outside the pipe's `PATH`.

A **disabled** endpoint (`Spark:Mail:Bounces:Endpoint:Enabled` false) answers **503**, so the reports
that arrive before it is enabled stay deferred in Postfix's queue and are delivered once it is — they
are not lost. The endpoint itself never answers 404 (a report for an unknown delivery id is accepted
with 204, logged, and suppresses nothing); a 404 therefore means the request never reached it — a
wrong `SPARK_BOUNCE_URL`, or an app without MailManager — a misconfiguration like a wrong secret, so the
script defers it (exit 75) and Postfix retries until the URL is fixed (#460, M10). Check the URL
before pointing the relay at the app.

Relay environment: `VERP_DOMAIN`, `SPARK_BOUNCE_URL=http://<app>:8080/spark/mail/bounces`,
`SPARK_BOUNCE_SECRET` (in the VPS `.env`, never in the compose file), and the VERP domain added to
`ALLOWED_SENDER_DOMAINS`. App: `Spark__Mail__Bounces__VerpDomain`, `Spark__Mail__Bounces__Endpoint__Enabled=true`,
`Spark__Mail__Bounces__Endpoint__Secret` (the same secret). SPF of the VERP domain must authorise the
server, as for the `From` domain (§3).

What S-M5 measured: a DSN addressed to `bounces+{id}@verp.test` reached the pipe and was POSTed with the
bearer header, `Content-Type: message/rfc822`, the full report (720 bytes) and
`?recipient=bounces%2B{id}%40verp.test`; the queue was then empty. With the endpoint unreachable, curl
failed, the script exited 75 and Postfix kept the bounce **deferred** (`dsn=4.3.0, status=deferred
(temporary failure. Command output: curl: (6) Could not resolve host …)`); once the endpoint was back,
`postqueue -f` delivered it (204) and the queue emptied. A 401 or 403 (wrong secret) is retried until
Postfix's queue lifetime (5 days by default) — check the app log for the 401 when bounces pile up. A
report the endpoint can never accept (400, 413, 422) is dropped at once instead of being retried
for five days (#460, M9).

### 8.4 Bounces — tier 2: remote bounces via MX

A receiver that **accepts** a mail and bounces it later sends the bounce to the VERP domain's MX. That
needs an MX record for the VERP domain pointing at the server and inbound port 25 open to the relay,
which this deployment deliberately does not have (§6 of the PRD: opening inbound SMTP is out of scope).
Recipe, not done here: publish `MX 10 relay.example.` for the VERP domain, publish port 25 of the relay
container, keep `relay_domains` limited to the VERP domain (anything else is an open relay), and the
same pipe handles both tiers. Without tier 2, late bounces are lost; synchronous refusals (the common
case for unknown mailboxes) are still caught by tier 1.

### 8.5 Still not covered

- **Inbound mail** other than bounces. Point `From` at a real mailbox if people reply.
- **Complaint feedback loops** (ARF): `ISparkMailSuppressions.SuppressAsync(…, Complaint)` is there
  for an app that receives them from a provider; nothing parses them.


---

## 9. What was actually done for `coverage.mintplayer.com`

The exact sequence, in order, as a record. Steps marked **manual** were done by the owner in a
provider console; the rest are reproducible commands.

### 9.1 Hetzner console — manual

Requested removal of the outbound SMTP block for ports **25 and 465** from
`console.hetzner.com/limits`. Granted within minutes. Verified from the server:

```bash
timeout 8 bash -c 'exec 3<>/dev/tcp/alt4.aspmx.l.google.com/25' && echo OPEN25
```

~~⚠️ Still outstanding: the **PTR is unchanged** (`static.60.190.245.188.clients.your-server.de`).~~
**Done for IPv4 on 2026-09-29:** the PTR of `188.245.190.60` is now `coverage.mintplayer.com`, which
forward-resolves to the server. The IPv6 address has no PTR, which is one reason the relay now sends
over IPv4 only — see `apps/CodeCoverage/README.md` § Outgoing mail.

### 9.2 DNS at the registrar — two records added

`mintplayer.com` is hosted at foxxl/Qweb (`ns10.foxxl.{com,net,nl}`). Two TXT records were
**added**; nothing existing was modified. The zone was exported to CSV first, which is worth doing
on any panel that edits the whole zone as one form.

```
coverage                    TXT   v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all
mail._domainkey.coverage    TXT   v=DKIM1;k=rsa;p=<408-character public key>
```

The IPv6 address was included because `coverage.mintplayer.com` has an AAAA record pointing at the
same host. ⚠️ That was the wrong address to list: the AAAA is the subnet's `::`, while the host
sends from `::1` (§3.3). It is harmless now that the relay sends over IPv4 only; re-enabling IPv6
would first need `ip6:` to name the sending address (or the /64) and a PTR for it. Confirmed published against the authoritative server before testing:

```bash
dig +short TXT coverage.mintplayer.com @ns10.foxxl.com
dig +short TXT mail._domainkey.coverage.mintplayer.com @ns10.foxxl.com
```

### 9.3 On the server

```bash
mkdir -p /var/www/code-coverage/mail-dkim/coverage.mintplayer.com

# 2048-bit key, generated in a throwaway container so nothing is installed on the host
docker run --rm -v /var/www/code-coverage/mail-dkim/coverage.mintplayer.com:/out alpine:3.20 sh -c \
  'apk add --no-cache opendkim-utils && opendkim-genkey -b 2048 \
     -d coverage.mintplayer.com -s mail -D /out && chmod 600 /out/mail.private'

# §5 trap 1 — the image wants the key flat, not under a per-domain directory
cp /var/www/code-coverage/mail-dkim/coverage.mintplayer.com/mail.private \
   /var/www/code-coverage/mail-dkim/coverage.mintplayer.com.private

# §5 trap 2 — opendkim (101:104) must own it, because it cannot chown a read-only mount
chown -R 101:104 /var/www/code-coverage/mail-dkim
chmod 600 /var/www/code-coverage/mail-dkim/*.private
chmod 700 /var/www/code-coverage/mail-dkim
```

The private key never leaves the server and is never printed; only the `.txt` public half is read
out for DNS.

### 9.4 Verification, in the order it was run

| Check | Result |
|---|---|
| `opendkim-testkey` | `key OK`, `key secure` |
| Probe **before** DNS | `550 5.7.509 ... DMARC policy of reject` |
| Probe **after** SPF, DKIM misfiled | `status=sent`, but log showed `Skipping DKIM` — delivered **unsigned** on SPF alone |
| Probe **after** both traps fixed | `status=sent`, opendkim running, message 9,212 → 10,808 bytes |
| Recipient | **inbox**, not junk, at an Outlook/Hotmail address |

The third row is the one worth remembering: it looked like success and was not.

### 9.5 In the repository

- `apps/CodeCoverage/docker-compose.yml` — the `coverage-smtp` service, the `smtp-queue` volume,
  and on the app `Spark__Mail__Smtp__*`, `Spark__Mail__From__*`, `Spark__Auth__PublicBaseUrl` and
  `Spark__Auth__ExternalLoginLinking` (#460 M8: these replaced `Coverage__Mail__*`; the `.env`
  variables are unchanged).
- `apps/CodeCoverage/CodeCoverage/Program.cs` — `spark.AddMailManager()`, only when both
  `Spark:Mail:Smtp:Host` and `Spark:Mail:From:Address` are set. The hand-written
  `SmtpLinkConfirmationSender` and `CoverageMailOptions` are gone; the link-confirmation mail is the
  shipped `SparkAuth/LinkConfirmation` MJML template.
- `apps/CodeCoverage/.env.example` — every variable, with the delivery caveats inline.

### 9.6 Still to do before mail is live

1. Deploy the updated `docker-compose.yml` (the VPS refetches it per deploy).
2. Set `MAIL_FROM_ADDRESS` in `/var/www/code-coverage/.env`. Until then the app does not add
   MailManager, which is deliberate and supported.
3. Flip `EXTERNAL_LOGIN_LINKING` to `ConfirmByEmail` only when a second forge exists — with a
   single provider the situation the mode exists for cannot arise.
4. ~~Optionally set the PTR, per §9.1.~~ Done for IPv4 on 2026-09-29.
5. Bounces (§8.3) are not enabled for coverage.mintplayer.com; doing so needs a VERP domain in
   `ALLOWED_SENDER_DOMAINS` and SPF, and `SPARK_BOUNCE_SECRET` in the VPS `.env`.
