# Data Protection (the key ring)

Every authentication cookie, antiforgery token and protected payload a Spark application issues is
encrypted with ASP.NET Core Data Protection. Spark calls `AddDataProtection()` itself (#460) — an
application does not — and decides **where the key ring is kept** from `Spark:DataProtection`.

> ⚠️ **A key ring that does not survive a redeploy signs every user out on every redeploy.** That is
> what ASP.NET Core's default does inside a container: the default key folder is part of the
> container filesystem, which a redeploy throws away. So outside Development Spark **refuses to
> start** until you say where the keys live.

## Configuration

| Key | Meaning | Default |
|---|---|---|
| `Spark:DataProtection:Storage` | `RavenDb` — keys stored as documents under `DataProtectionKeys/` in the application's database | unset |
| `Spark:DataProtection:KeysPath` | keys stored as XML files in this folder (relative paths are relative to the content root) | unset |
| `Spark:DataProtection:ApplicationName` | the application discriminator; payloads protected under one name cannot be read under another | the entry assembly's name |

Set **exactly one** of `Storage` and `KeysPath`; setting both refuses to start.

| Environment | Neither set |
|---|---|
| Development | keys in a per-user folder: `%LOCALAPPDATA%/MintPlayer.Spark/DataProtection-Keys/<ApplicationName>` |
| anything else (Production, Staging, E2E, Testing …) | **startup error** from `UseSpark()`, explaining the redeploy sign-out |

## The recommended pattern: location per environment, never in the base file

Put only `ApplicationName` in the base `appsettings.json`, and supply the key location from the
environment that runs the app:

```json
"Spark": {
  "DataProtection": {
    "ApplicationName": "MyApp"
  }
}
```

| Where | Set | How |
|---|---|---|
| Production | `KeysPath` → a folder on a **mounted volume** | environment variable `Spark__DataProtection__KeysPath=/var/lib/myapp/dataprotection-keys` in the deployment (e.g. `docker-compose.yml`) |
| Test hosts (E2E, `WebApplicationFactory`, spawned processes) | `Storage=RavenDb` | the test host's own configuration: `UseSetting("Spark:DataProtection:Storage", "RavenDb")`, an `appsettings.{TestEnvironment}.json` override, or `Spark__DataProtection__Storage=RavenDb` on the child process |
| Development | nothing | the per-user folder above |

**Never put `Storage` or `KeysPath` in the base `appsettings.json`.** Configuration layers merge
rather than replace, so a base `Storage=RavenDb` plus a production `KeysPath` environment variable
is "both set" and refuses to start. Keeping the base file free of either lets each environment
choose one without having to unset the other.

This is what the apps in this repository do: CodeCoverage's `docker-compose.yml` mounts a
`dataprotection-keys` volume and sets `KeysPath`; `FleetTestHost` (E2E) and CodeCoverage's
`CoverageWebAppFactory` / `ActionDogfoodHarness` set `Storage=RavenDb`.

## Which one to choose

**`KeysPath`** (production default in this repository) keeps the keys in a folder that must be a
durable volume — see the warning below.

**`Storage=RavenDb`** keeps the keys next to the rest of the application's state, so they are backed
up, restored and persisted exactly as the data they protect. It suits test hosts, whose database is
created per run anyway. The keys are not additionally encrypted at rest — whoever can read the
database can read them, the same posture as the file-system repository.

> ⚠️⚠️ **An unmounted key folder loses its keys on redeploy, exactly like the default.** `KeysPath`
> is only as durable as the folder it names. Inside a container it must be a **mounted volume**
> (`volumes: - app-keys:/keys`), and on a multi-instance deployment every instance must share it.
> Spark cannot tell a mounted folder from an ephemeral one — **making it durable is the operator's
> responsibility**. The startup check only proves you *chose* a location, not that it survives.

## Changing the application name

`ApplicationName` is part of every ciphertext. Changing it on a running deployment is equivalent to
losing the key ring: every cookie issued before the change stops decrypting and every user is
signed out once. The default is the entry assembly's name, so **renaming the application's project
changes it too** — set it explicitly on anything deployed.

## Migrating from a hand-written setup

Delete your own `AddDataProtection()` / `SetApplicationName(...)` / `PersistKeysTo...()` calls and set
the configuration above. If you keep them, they still win — they are registered after Spark's — but
the startup check does not know about them, so you still have to set one of the two keys outside
Development.

CodeCoverage (coverage.mintplayer.com) migrated in #460. Its old key ring was written by the
repository Spark's `RavenDb` storage is lifted from (the same `DataProtectionKeys/` prefix, the same
`{ "Xml": "…" }` shape, the same `KeyDocuments` collection), and `SparkDataProtectionTests` pins that
such a document still decrypts under Spark with `ApplicationName=CodeCoverage`. Production
nevertheless moved to `KeysPath` on a volume (owner decision): users are signed out once on the
first deploy, and the orphaned `KeyDocuments` can be deleted once that deploy is healthy.
