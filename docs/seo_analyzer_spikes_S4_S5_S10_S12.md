# Spikes S4, S5, S10, S12 for docs/seo_analyzer_PRD.md §8

Run on 2026-10-10 from the owner's machine (Windows 11, pwsh 7, no API keys).
Evidence lives next to this file: `fixtures/` holds raw responses, `scripts/` holds the scripts that
produced them, and `downloads/` holds third-party source files that were read but never executed.

---

## S4: RDAP coverage of domain expiry

**Question.** For com, net, org, eu, nl, be, io, dev, app, brussels and vlaanderen: does the IANA RDAP
bootstrap list a base URL, and does the domain object carry `events[eventAction=expiration]`? Does
.be offer RDAP, and does WHOIS publish an expiry?

**Method.**
- Downloaded `https://data.iana.org/rdap/dns.json`, publication `2026-09-30T23:00:03Z`, saved as `fixtures/iana_dns.json`.
- Sent `GET {base}domain/{name}` with `Accept: application/rdap+json` (`scripts/s4.ps1`).
- Guessed RDAP hosts for the TLDs missing from the bootstrap.
- Probed the `rdap.org` redirector for those TLDs.
- Ran WHOIS over a raw TCP connection to port 43 (`scripts/whois.ps1`, a .NET `TcpClient`, not a whois binary).
- Parsed the events with `scripts/events.mjs`; the output is in `s4_events.txt`.

Gotcha: pwsh `Invoke-WebRequest` returns `application/rdap+json` as a `byte[]`, so the client must
decode it as UTF-8 itself. The .NET `HttpClient` + `ReadAsStringAsync` route does not have this
problem.

**Result.**

| TLD | In IANA bootstrap? | RDAP base URL | Query (HTTP status) | `expiration` event | Fixture |
|---|---|---|---|---|---|
| com | yes | https://rdap.verisign.com/com/v1/ | google.com (200, 287 ms) | **yes**, 2028-09-14T04:00:00Z | rdap_com.json |
| net | yes | https://rdap.verisign.com/net/v1/ | google.net (200) | **yes**, 2027-03-15T04:00:00Z | rdap_net.json |
| org | yes | https://rdap.publicinterestregistry.org/rdap/ | wikipedia.org (200) | **yes**, 2027-01-13T00:12:14Z | rdap_org.json |
| dev | yes | https://pubapi.registry.google/rdap/ | google.dev (200) | **yes**, 2027-06-13T22:30:20.594Z | rdap_dev.json |
| app | yes | https://pubapi.registry.google/rdap/ | google.app (200) | **yes**, 2027-03-29T16:02:13.209Z | rdap_app.json |
| brussels | yes | https://rdap.nic.brussels/ | nic.brussels (200) | **yes**, 2027-03-26T12:09:34Z | rdap_brussels.json |
| vlaanderen | yes | https://rdap.nic.vlaanderen/ | nic.vlaanderen (200) | **yes**, 2027-03-26T12:11:56Z | rdap_vlaanderen.json |
| nl | yes | https://rdap.sidn.nl/ | sidn.nl (200) | **no**; only registration, last changed and last update events | rdap_nl.json |
| io | **no** | not in bootstrap; `https://rdap.identitydigital.services/rdap/` answers (found by guessing) | nic.io (200) | **yes**, 2027-05-01T00:00:02Z | rdap_io.json |
| eu | **no** | none; rdap.eurid.eu does not resolve, rdap.eu refuses connections | none | **no** | none |
| be | **no** | none; rdap.dns.be and rdap.dnsbelgium.be do not resolve, www.dnsbelgium.be/rdap/... returns 404 | none | **no** | none |

`rdap.org/domain/{dnsbelgium.be, eurid.eu, nic.io}` returns 404 for all three, so the redirector
cannot fill these gaps.

**WHOIS on port 43** (fixtures `whois_be.txt`, `whois_eu.txt`, `whois_nl.txt`, `whois_io.txt`):

| TLD | Server | Dates published |
|---|---|---|
| be | whois.dns.be | `Registered: Wed Jun 13 2012` and `Status: NOT AVAILABLE`. **No expiry date.** |
| eu | whois.eu | **No dates at all**: no registration, no expiry. |
| nl | whois.domain-registry.nl | `Creation Date` and `Updated Date`. **No expiry date.** |
| io | whois.nic.io | `Registry Expiry Date: 2027-05-01T00:00:02Z`, the same value as RDAP. |

.be, .eu and .nl have no registry-side expiry because these registries renew automatically until
the registrar cancels. .be registrations run one year at a time; registrar documentation says an
expired name keeps working until the end of the month and then enters a 40-day quarantine. The
expiry date is known only to the registrar.

**Decision.**
- Domain expiry comes from **RDAP only**, resolved through the IANA bootstrap. Cache the bootstrap for about a day.
- Read `events[eventAction=="expiration"].eventDate`.
- If the bootstrap has no entry for the TLD, or there is no expiration event, report **"expiry not published by the registry"** (an informational state, not an error).
- Covered: com, net, org, dev, app, brussels and vlaanderen.
- Not covered: be, eu and nl. These are exactly the TLDs that matter most for Belgian sites, so the UI must say so plainly. The fallback is to let the user enter the expiry date or renewal date shown by their registrar.
- io: optional extra. A small built-in override map (`io → https://rdap.identitydigital.services/rdap/`) works, but it is a guessed endpoint outside the bootstrap. Skip it unless someone asks.
- Do **not** scrape WHOIS: it adds nothing for be, eu or nl, and DNS Belgium's terms forbid automated extraction.

---

## S5: PageSpeed Insights (and CrUX)

**Question.** Does an unauthenticated PSI v5 call work? What are the latency, the Lighthouse version,
whether CrUX field data is present, the Lighthouse 13 insight audit ids, the category score paths and
the quotas?

**Method.** `scripts/s5_s10.ps1` sent two calls without a key:
- example.com: mobile, all four categories.
- www.google.com: mobile, performance only.

Insight ids came from the Lighthouse source at the current npm version, **13.5.0**
(`downloads/lh_default-config.js`, fetched from the GitHub tag `v13.5.0`). Quotas and the response
schema came from Google's documentation.

**Result: unauthenticated calls are refused.**
- **example.com:** `429` after 139 ms. The body is in `fixtures/psi_example_mobile.json`:
  `"Quota exceeded for quota metric 'Queries' and limit 'Queries per day' of service
  'pagespeedonline.googleapis.com' for consumer 'project_number:583797351490'"`.
  The details carry `quota_limit: defaultPerDayPerProject` and **`quota_limit_value: "0"`**, reason
  `RATE_LIMIT_EXCEEDED`, status `RESOURCE_EXHAUSTED`. The anonymous shared project has a quota of
  **zero**, so this is not a transient limit.
- **www.google.com:** `429` after 7.2 s with an HTML "Sorry... automated queries" page
  (`fixtures/psi_google_mobile_perf.json`, which is HTML despite the extension).

  → **No real Lighthouse response could be captured.** The fixture still needs a run with an API key.
  The owner can run it, or someone can save one from the PSI web UI (pagespeed.web.dev → "View
  report" JSON).

Google's documentation says the API "can be used with or without an API key", with a key
"recommended". **In practice, as of 2026-10 a key is required.**

**Response schema** (Google REST reference for `runPagespeed`):
- Top level: `kind`, `captchaResult`, `id` (the final URL after redirects), `loadingExperience`,
  `originLoadingExperience`, `analysisUTCTimestamp`, `lighthouseResult`, `version{major,minor}`.
- `loadingExperience` and `originLoadingExperience` carry the CrUX field data:
  `{id, metrics{<METRIC>:{percentile, distributions[], category}}, overall_category, initial_url, origin_fallback}`.
  They are absent or empty when CrUX has no data for the URL or origin, so treat them as optional.
- Scores: `lighthouseResult.categories.{performance,accessibility,best-practices,seo}.score`, a 0..1
  value or `null`. Multiply by 100 for display.
- Version: `lighthouseResult.lighthouseVersion`. The npm latest is 13.5.0; PSI typically trails it slightly.
- Audits: `lighthouseResult.audits.<id>`, each with `{id,title,description,score,scoreDisplayMode,displayValue,numericValue,details}`.
- Query parameters:
  - `category` is repeatable: `PERFORMANCE`, `ACCESSIBILITY`, `BEST_PRACTICES`, `SEO`. Lowercase and hyphenated forms are accepted too. With no category, only performance runs.
  - `strategy` is `MOBILE` or `DESKTOP`. **The default is DESKTOP.**
  - Also accepted: `locale`, `utm_source`, `utm_campaign`, `captchaToken`.

**Performance metric weights in Lighthouse 13.5:** TBT 30, LCP 25, CLS 25, FCP 10, Speed Index 10.

**Lighthouse 13 insight audits** (all in group `insights` with weight 0, so they never affect the score):
`cache-insight`, `cls-culprits-insight`, `document-latency-insight`, `dom-size-insight`,
`duplicated-javascript-insight`, `font-display-insight`, `forced-reflow-insight`,
`image-delivery-insight`, `inp-breakdown-insight`, `lcp-breakdown-insight`, `lcp-discovery-insight`,
`legacy-javascript-insight`, `modern-http-insight`, `network-dependency-tree-insight`,
`render-blocking-insight`, `third-parties-insight`, `viewport-insight`.

**Quotas.**
- **PSI with a key:** Google publishes no figure on the get-started page. Third-party sources and the
  Cloud Console defaults agree on **25,000 queries/day per project**, with a per-minute cap of about
  240/min (often quoted as 400 per 100 s). The authoritative value is in the project's Cloud Console
  Quotas page.
- **CrUX API:** **150 queries/min per Google Cloud project**. Free, and the limit cannot be raised.
  It requires a key for the "Chrome UX Report API". Endpoint:
  `POST https://chromeuxreport.googleapis.com/v1/records:queryRecord?key=…`. Data updates daily
  around 04:00 UTC with no SLA.

**Decision.**
- PSI must be **configured with an API key**, an app secret stored as a secret and never logged.
- Without a key, disable the PSI check and say "not configured". Do not try anonymous calls: they
  are quota 0.
- Treat a 429 as "quota exhausted, retry later", not as a failure of the site.
- Each run takes tens of seconds, so run it in the background job with a timeout of 120 s or more.
- Store only the scores, the core metrics, the insight audit ids with their scores and `displayValue`,
  and the CrUX summary. Do not store the multi-MB JSON.
- CrUX field data comes "for free" inside PSI (`loadingExperience`), so a separate CrUX client is optional.
- **Open item:** capture `fixtures/psi_example_mobile.json` with a key before writing parser tests
  (the repo rule is that parsers need real fixtures).

---

## S10: hstspreload.org API

**Method.** Unauthenticated `GET` requests, all `200 application/json`, 270–710 ms. Fixtures:
- `hsts_status_{google.com,example.com,mintplayer.com,github.com,paypal.com,foo.dev,www.mintplayer.com}.json`
- `hsts_preloadable_{example.com,google.com}.json`
- `hsts_removable_example.com.json`

**`/api/v2/status?domain=X`** returns `{ name, status, bulk, preloadedDomain }`:
- `name`: the domain as queried, normalized.
- `status`: one of `unknown`, `pending`, `preloaded` or `rejected`. Only `unknown` and `preloaded` were observed here; the other values come from the hstspreload source.
- `bulk`: `true` when the entry was submitted through the site's bulk form. This is not the same as `includeSubDomains`.
- `preloadedDomain`: the list entry that covers this name, or `""`. It can be a **parent** or even a TLD:
  - `www.mintplayer.com` → `preloaded`, `preloadedDomain: "mintplayer.com"`.
  - `foo.dev` → `preloaded`, `preloadedDomain: "dev"`. The whole .dev and .app TLDs are preloaded.
- Observed values:
  - `mintplayer.com` → `preloaded` (bulk true).
  - `github.com` → `preloaded`.
  - `google.com` → **`unknown`** (Google preloads only specific subdomains).
  - `example.com` → `unknown`.

**`/api/v2/preloadable?domain=X`** and **`/api/v2/removable?domain=X`** return
`{ errors: Issue[], warnings: Issue[] }`, where `Issue = { code, summary, message }`. The domain is
preloadable if and only if `errors` is empty. Observed codes:
- `response.no_header`: no HSTS header.
- `redirects.http.no_redirect`: http does not redirect to https.
- `redirects.http.www_first`: http redirects to www before https.
- `server.removable.protected`: on the removable endpoint.

Other `header.*` and `redirects.*` codes exist in the hstspreload source (max-age too low, no
`includeSubDomains`, no `preload` directive, and so on).

**Decision.**
- Use `/status` for "is this site preloaded?", and walk up via `preloadedDomain` to explain parent or
  TLD coverage.
- Use `/preloadable` to list the blocking `errors[].summary`, plus the warnings, when the site is not preloaded.
- Key on `code`, not on `message`.
- No key is needed and there is no documented rate limit; cache results per domain for about a day.
- The site's own header and redirect checks stay local (we fetch anyway). The API adds the list-status
  fact plus Chromium's authoritative criteria.

---

## S12: Web Push on .NET 11

**Options evaluated.**

| | `WebPush` (web-push-csharp) | `Lib.Net.Http.WebPush` | Hand-rolled (BCL only) |
|---|---|---|---|
| Latest | 1.0.13, 2026-04-28 (latest commits are build and release chores) | 3.3.1, 2025-03-09; repo touched 2026-01 (README/copyright) | n/a |
| TFMs | net8.0, netstandard2.1, net48 | net451, net461, netstandard2.0, net5.0, net6.0 (net11 uses the net6.0 asset) | net11.0 |
| Dependencies | **Portable.BouncyCastle ≥ 1.9.0 on every TFM**, including net8/9/10 | net5/net6: only `Lib.Net.Http.EncryptedContentEncoding` 2.1.1. BouncyCastle only on netfx/netstandard | none |
| Licence | MPL-2.0 | MIT | n/a |
| Downloads | 7.7M | 2.4M | n/a |
| **Content encoding** | **`aesgcm` only**: legacy draft, with `Encryption:` and `Crypto-Key:` headers (`downloads/WebPushClient.cs` lines 211–253) | **`aes128gcm` (RFC 8291)** via `Aes128GcmEncodedContent` (`downloads/PushServiceClient.cs` line 308) | aes128gcm |
| VAPID | yes | yes (RFC 8292, `vapid t=…, k=…`) | ES256 JWT via `ECDsa` |
| TTL / Urgency / Topic | TTL only; Urgency and Topic only as raw headers | all three (`PushMessage.TimeToLive/Urgency/Topic`) | own code |
| 404/410 | throws `WebPushException` with the status code | throws `PushServiceClientException` with the status code | own code |

**Platform facts** (Apple's "Sending web push notifications in web apps and browsers", plus the RFCs):
- **iOS/iPadOS 16.4+** works only for a **Home Screen web app**: an installed PWA with a manifest.
- On macOS, Safari 16+ on macOS 13+ works.
- No Apple Developer account is needed.
- The `subscribe()` call must be made directly inside a user-gesture handler.
- **Safari revokes the permission if a push is not shown as a notification**: no silent pushes.
- Endpoints are `https://*.push.apple.com`, which an egress allow-list must permit. TLS with SNI is required.
- Headers:
  - `TTL` is required (`BadTtl` otherwise); the maximum is 30 days.
  - `Urgency` is `very-low`, `low`, `normal` or `high`.
  - `Topic` is at most 32 base64url characters.
  - `Authorization: vapid t=<JWT>,k=<pubkey>`.
- JWT rules:
  - `aud` must be the push service origin.
  - `exp` must be 24 h or less ahead.
  - `sub` must be a `mailto:` or https URL.
  - Apple: "Don't refresh your JWT more frequently than once per hour", so cache it per audience.
- Payload limit: **4 KB** (`PayloadTooLarge` / 413), measured on the encrypted body. With aes128gcm
  the overhead is 86 header bytes, a 16-byte tag and 1 padding delimiter, which leaves about
  3,993 bytes of plaintext.
- Expired subscriptions: **404 or 410 → delete the subscription**. 429 and 5xx → retry with
  backoff, honouring `Retry-After`. 400 and 403 → configuration bug (VAPID key mismatch:
  `VapidPkHashMismatch`, `BadJwtToken`). 413 → payload too large.
- Modern browsers (Chrome, Firefox, Edge, Safari) all accept aes128gcm, and RFC 8291 makes it the
  standard encoding. Apple's error list includes `BadWebPushRequest` for "doesn't conform to the
  encryption rules". `aesgcm` is the obsolete pre-RFC draft, so web-push-csharp is a risk for Safari
  and for any future removal of `aesgcm`.

**Hand-rolled estimate.** About 250–350 lines plus tests, using only BCL primitives:
- `ECDiffieHellman` (P-256): import the 65-byte uncompressed `p256dh` point by splitting X and Y, then `DeriveRawSecretAgreement` (.NET 8+).
- `HKDF.Extract` and `HKDF.Expand` for the RFC 8291 IKM, CEK and nonce. The info strings are `"WebPush: info\0"+ua_pub+as_pub`, `"Content-Encoding: aes128gcm\0"` and `"Content-Encoding: nonce\0"`.
- `AesGcm` with a 16-byte tag.
- `ECDsa.SignData(…, SHA256)`. Its default output is IEEE-P1363 (64 bytes r‖s), which is exactly JWS ES256. The pitfall is DER from other APIs.

Pitfalls:
- base64url without padding, both directions.
- A single record with delimiter `0x02`, `rs = 4096`, and `keyid` = the ephemeral public key (65 bytes).
- A fresh salt and ephemeral key for every message.
- `aud` = `scheme://host[:port]` of the endpoint.
- Cache the JWT per audience for 1 hour or less.
- `Content-Encoding: aes128gcm` and `Content-Type: application/octet-stream`.

RFC 8291 Appendix A has exact test vectors, so the code can be verified byte-for-byte without a browser.

**Recommendation: hand-roll it (RFC 8291 + RFC 8292) in roughly 300 lines, verified against the
RFC 8291 Appendix A test vectors.**

Reasons:
- **web-push-csharp is ruled out**: it sends only the legacy `aesgcm` encoding, it drags
  Portable.BouncyCastle 1.9 (the old, unmaintained fork) onto net11, and it is MPL-2.0.
- **Lib.Net.Http.WebPush is the only acceptable package**: MIT, aes128gcm, Urgency/Topic/TTL, and
  no BouncyCastle on net5+. But its newest TFM is net6.0, its last release was March 2025, and it
  pulls a second package. It works on net11 through forward compatibility, but it is a frozen
  dependency in a repo that tracks preview .NET.
- **Hand-rolling** needs no packages, and every primitive is in the BCL (`HKDF`, `AesGcm`, `ECDiffieHellman`, `ECDsa`).
- It gives full control over the 404/410 cleanup, JWT caching and `Retry-After`.
- It is small enough to own, and RFC test vectors make it falsifiable.

Fallback: if the owner prefers not to own crypto code, use `Lib.Net.Http.WebPush` 3.3.1. Never use `WebPush`.

---

## Sources
- IANA RDAP bootstrap: https://data.iana.org/rdap/dns.json
- PSI get-started: https://developers.google.com/speed/docs/insights/v5/get-started
- PSI REST reference: https://developers.google.com/speed/docs/insights/rest/v5/pagespeedapi/runpagespeed
- PSI quota (third-party): https://unlighthouse.dev/learn-lighthouse/pagespeed-insights-api/rate-limits
- CrUX API: https://developer.chrome.com/docs/crux/api
- Lighthouse 13.5.0 config: https://raw.githubusercontent.com/GoogleChrome/lighthouse/v13.5.0/core/config/default-config.js
- hstspreload API: https://hstspreload.org/api/v2/
- .be policies (registrar): https://support.opensrs.com/support/solutions/articles/201000063468--be-domain-policies
- Apple web push: https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers
- WebPush on NuGet: https://www.nuget.org/packages/WebPush · repository: https://github.com/web-push-libs/web-push-csharp
- Lib.Net.Http.WebPush on NuGet: https://www.nuget.org/packages/Lib.Net.Http.WebPush · repository: https://github.com/tpeczek/Lib.Net.Http.WebPush
- RFC 8291 (aes128gcm), RFC 8292 (VAPID), RFC 8030 (Web Push protocol)
