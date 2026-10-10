# SeoTester

SeoTester (#268) is the production app at **https://seotester.mintplayer.com**: users sign in with Microsoft, GitHub,
X or Google, register websites in an organization, and get an SEO / performance / security / accessibility report.
The spec, decisions and milestones are in [seo_analyzer_PRD.md](../seo_analyzer_PRD.md); this folder is the
operator's documentation. Treat changes to `apps/SeoTester` as production changes.

Status: **M7 skeleton** (projects, composition, sign-in, organizations, settings, notifications, `/bot`). Sites,
audits and the crawler arrive with M8–M11; compose, deploy and monitoring with M12.

## Layout

| Project | What |
|---|---|
| `apps/SeoTester/SeoTester` | the host (`Program.cs`) and the Angular SPA (`ClientApp`, nx project `@spark-apps/seo-tester`); `Settings/` (every gate and limit), `Bot/` (the `/bot` page), `Dockerfile` |
| `apps/SeoTester/SeoTester.Library` | the entity library (plain SDK): the crawler's identity (`SeoTesterBot`), the pipeline's queues and messages (`Messaging/SeoQueues.cs`); entities and checks from M8 |
| `apps/SeoTester/SeoTester.Tests` | boots the real `Program` against an embedded RavenDB: composition, administrator-only settings, `/bot`, the route table |

## Running it locally

1. RavenDB at `http://localhost:8080` (the database `SeoTester` is created on first start).
2. SparkId, the demo identity provider: `dotnet run --project apps/SparkId/SparkId --launch-profile https`
   (https://localhost:5011). Its Development migration registers the `seotester` client, so you can sign in without
   registering any OAuth app.
3. `dotnet run --project apps/SeoTester/SeoTester --launch-profile https` → **https://localhost:5013** (http 5014).
   The host starts the Angular dev server itself (`UseAngularCliServer`); do not run `ng serve` beside it. The app is
   ready when the host prints the dev server's `➜ Local:` line.

Development writes every mail as an `.eml` file into `apps/SeoTester/SeoTester/mail-pickup/` (link confirmations,
invitations, notifications). To sign in with a real provider locally, set its keys in user secrets (below) and
register `https://localhost:5013/signin-…` as an extra callback on that provider's development app.

**Becoming an administrator** (the Settings screen): add `"Administrators"` to the `Roles` list of your `SparkUsers/…`
document (RavenDB Studio), then sign out and in again. `security.json` binds the settings package's slot
`settings:administrators` to that group; every other user gets a 404 on the settings.

## Configuration and credentials the owner registers

Never commit a value: `appsettings.json` holds empty placeholders only. Locally use user secrets
(`dotnet user-secrets set "<key>" "<value>" --project apps/SeoTester/SeoTester`), in production the container's
environment (`:` becomes `__`, e.g. `Spark__Auth__Providers__GitHub__ClientSecret`). A provider whose `ClientId` is
empty is simply not offered; with **no** provider at all the app refuses to start (nobody could sign in).

### Sign-in providers (PRD §4.1, D2, D13)

| Provider | Register at | Callback (production) | Scopes / notes | Keys |
|---|---|---|---|---|
| Microsoft (Entra, personal **and** work/school) | Microsoft Entra admin center → App registrations; "Accounts in any organizational directory and personal Microsoft accounts" | `https://seotester.mintplayer.com/signin-microsoft` | `openid`, `profile`, `email` (the preset adds them); the `common` endpoint. Work accounts give no verified email | `Spark:Auth:Providers:MicrosoftAccount:ClientId`, `…:ClientSecret` |
| GitHub | GitHub → Settings → Developer settings → OAuth Apps | `https://seotester.mintplayer.com/signin-github` | `user:email` | `Spark:Auth:Providers:GitHub:ClientId`, `…:ClientSecret` |
| X | developer.x.com → Projects & Apps → User authentication settings: **OAuth 2.0**, type "Web App", enable **Request email from users** (needs a privacy policy and terms URL) | `https://seotester.mintplayer.com/signin-twitter` | OAuth 2.0 client id/secret, not the 1.0a API keys; `users.read`, `tweet.read`, `users.email` | `Spark:Auth:Providers:Twitter:ClientId`, `…:ClientSecret` |
| Google | Google Cloud console → APIs & Services → Credentials → OAuth client ID ("Web application") | `https://seotester.mintplayer.com/signin-google` | `openid profile email` | `Spark:Auth:Providers:Google:ClientId`, `…:ClientSecret` |

### Everything else

| What | Keys | Notes |
|---|---|---|
| Public origin | `Spark:Auth:PublicBaseUrl` | `https://seotester.mintplayer.com`, already in `appsettings.Production.json`; mailed and share links are built from it |
| Mail (SMTP relay) | `Spark:Mail:Smtp:Host` (+ `Port`, `UserName`, `Password` if the relay needs them), `Spark:Mail:From:Address` | The sender defaults to `noreply@seotester.mintplayer.com`. **Required in production:** without a transport the app refuses to start (account-link confirmations need mail). Same relay as CodeCoverage (`docs/code-coverage/hosting.md`) |
| Web Push (VAPID) | `Spark:Notifications:WebPush:PublicKey`, `…:PrivateKey`, `…:Subject` | Generate once with `MintPlayer.Spark.WebPush.VapidDetails.Generate("mailto:…")`; the private key is a secret. Empty = push off, in-app and mail still work. Changing the pair later invalidates every browser subscription |
| PageSpeed Insights / CrUX | `SeoTester:PageSpeed:ApiKey` | A Google Cloud API key with the PageSpeed Insights API enabled (restrict it to that API). Empty = "not measured" (from M10) |
| Data Protection keys | `Spark:DataProtection:KeysPath` | A mounted volume (`/var/lib/seotester/dataprotection-keys` in the image), set by the compose file (M12) |
| RavenDB | `Spark:RavenDb:Urls:0`, `Spark:RavenDb:Database` | Database `SeoTester` on the VPS's RavenDB (Community licence: Changes API yes, ≤ 3 subscriptions, expiration every 36 h) |

### Administrator settings (not configuration)

Every feature gate and limit of PRD §6 and the category weights of §7 are **settings** (`Settings/SeoSettings.cs`,
`Settings/SeoQuotas.cs`): an administrator changes them on the Settings screen without a deploy. Each reads the
stored value, else the configuration key of the same name (`SeoTester:Limits:Unverified:PagesPerRun`, …), else the
PRD's default. They are never secrets.

## The crawler's identity

User agent `MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)`; robots.txt token `MintPlayerSeoTester`
(the only opt-out, D19). `/bot` is rendered by the server (`Bot/BotPageEndpoint.cs`) in English or Dutch and states the
current limits from the settings.
