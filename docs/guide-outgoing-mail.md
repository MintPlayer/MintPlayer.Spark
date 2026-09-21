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
| **Spark ships** | the contract (`ISparkLinkConfirmationSender<TUser>`) and the message text (`SparkLinkConfirmationMessage`) |
| **Spark does not ship** | any transport, on purpose — see §1 |
| **The app ships** | an implementation that hands the message to a local relay |
| **The relay ships** | queueing, retry, DKIM signing and delivery |

⚠️ **The app does not talk to the internet.** It hands the message to a relay container one hop
away and returns. This is not tidiness: the send happens *inside the external-login callback*,
while somebody is waiting on an HTTP response. A local handoff takes milliseconds and succeeds
whether or not the receiving mail server is reachable; a direct connection to a remote SMTP server
would put an internet round-trip, and its timeouts, in the middle of a sign-in.

---

## 1. Why Spark registers no default transport

ASP.NET Identity `TryAdd`s a no-op `IEmailSender`, so an application with no mail configured still
*resolves* one and every message is silently discarded. Absence is invisible; the only way to
detect it is to recognise an internal type by name, which fails **open** the day that type is
renamed.

Spark ships no default for its own contract precisely so the guard can be a plain null check. The
consequence you will meet: configuring `SparkExternalLoginLinking.ConfirmByEmail` **without**
registering a sender is refused at startup, because a confirmation nobody sends is a link nobody
makes — and the symptom would otherwise be a sign-in that appears to do nothing at all.

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

⚠️ **List both address families in SPF if your host has an AAAA.** Otherwise Postfix may send over
IPv6 and fail SPF there while passing over IPv4 — an intermittent failure that depends on which
family the *recipient* publishes.

⚠️ **And prefer IPv4 anyway.** The large receivers hold IPv6 senders to a stricter standard,
chiefly a valid PTR for the v6 address, which cloud providers set per address and which is usually
unconfigured. Pin `smtp_address_preference = ipv4`. SPF authorises both; this is about reputation,
not authorisation.

---

## 4. The relay container

```yaml
coverage-smtp:
  image: boky/postfix:v4.3.0
  environment:
    - ALLOWED_SENDER_DOMAINS=coverage.mintplayer.com
    - HOSTNAME=coverage.mintplayer.com          # must match reverse DNS
    - DKIM_SELECTOR=mail
    - POSTFIX_smtp_address_preference=ipv4
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
  -e POSTFIX_smtp_address_preference=ipv4 \
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

## 8. What this does *not* cover

- **Inbound mail.** The relay sends only. Bounces addressed back to a `no-reply@` on a host with no
  MX will defer and expire, which is harmless but means you never see them. Point `From` at a real
  mailbox if you want them.
- **Volume.** Everything here is sized for transactional mail measured in messages per day. Bulk
  sending is a different problem with different answers, and a self-hosted relay is the wrong tool
  for it.
- **A mail manager.** Message bodies are plain strings today (D24 in the multi-forge PRD). A
  templating layer is separate, future work.


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

⚠️ Still outstanding: the **PTR is unchanged** (`static.60.190.245.188.clients.your-server.de`).
Mail delivers regardless — see §3.1 — but setting it to `coverage.mintplayer.com` is a free
improvement and `coverage.mintplayer.com` already forward-resolves to the server.

### 9.2 DNS at the registrar — two records added

`mintplayer.com` is hosted at foxxl/Qweb (`ns10.foxxl.{com,net,nl}`). Two TXT records were
**added**; nothing existing was modified. The zone was exported to CSV first, which is worth doing
on any panel that edits the whole zone as one form.

```
coverage                    TXT   v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all
mail._domainkey.coverage    TXT   v=DKIM1;k=rsa;p=<408-character public key>
```

The IPv6 address is included because `coverage.mintplayer.com` has an AAAA record pointing at the
same host. Confirmed published against the authoritative server before testing:

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
  and `Coverage__Mail__*` plus `Spark__Auth__ExternalLoginLinking` on the app.
- `apps/CodeCoverage/CodeCoverage/Services/` — `CoverageMailOptions` and
  `SmtpLinkConfirmationSender`, registered only when `Host` and `FromAddress` are both set.
- `apps/CodeCoverage/.env.example` — every new variable, with the delivery caveats inline.

### 9.6 Still to do before mail is live

1. Deploy the updated `docker-compose.yml` (the VPS refetches it per deploy).
2. Set `MAIL_FROM_ADDRESS` in `/var/www/code-coverage/.env`. Until then the app registers no
   transport, which is deliberate and supported.
3. Flip `EXTERNAL_LOGIN_LINKING` to `ConfirmByEmail` only when a second forge exists — with a
   single provider the situation the mode exists for cannot arise.
4. Optionally set the PTR, per §9.1.
