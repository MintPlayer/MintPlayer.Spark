# WebhooksDemo Deployment (Sliplane)

This guide covers deploying the WebhooksDemo to [Sliplane](https://sliplane.io/) with RavenDB as a separate service.

## Architecture

```
GitHub ──POST──► WebhooksDemo (Sliplane service, port 8080)
                      │
                      ▼
                 RavenDB (Sliplane service, port 8080)
```

Two Sliplane services in the same project, communicating over the internal network.

## Step 1: Deploy RavenDB

Add a new service in Sliplane:

| Setting | Value |
|---|---|
| **Image URL** | `docker.io/ravendb/ravendb:latest` |
| **Service Name** | `ravendb` (this becomes the internal DNS hostname) |
| **Public** | Enable (to access RavenDB Studio for management) |
| **Health check path** | `/` |
| **Volume** | Create a volume, mount on `/opt/RavenDB/Server/RavenData` |

Configure the following environment variables:

| Variable | Value |
|---|---|
| `RAVEN_Setup_Mode` | `None` |
| `RAVEN_License_Eula_Accepted` | `true` |
| `RAVEN_Security_UnsecuredAccessAllowed` | `PublicNetwork` |
| `RAVEN_ServerUrl` | `http://0.0.0.0:8080` |
| `RAVEN_License` | Your RavenDB license JSON (single line, see below) |

> **Note**: Sliplane does not have a port configuration field — the RavenDB image exposes port 8080 by default, and Sliplane routes traffic to it automatically.

### RavenDB license

Get a free community license at [ravendb.net/license/request](https://ravendb.net/license/request). The license is a JSON object — pass it as a single-line string in the environment variable:

```
{"Id":"your-license-id","Name":"your-name","Keys":["key1","key2","..."]}
```

### Verifying RavenDB

Once the service is running, access the RavenDB Studio through the public URL Sliplane assigns (e.g., `https://ravendb-xxxxx.sliplane.app/`). You should see the Studio dashboard.

## Step 2: Deploy WebhooksDemo

Add a new service in Sliplane:

| Setting | Value |
|---|---|
| **Image URL** | `ghcr.io/mintplayer/mintplayer.spark/webhooks-demo:master` |
| **Service Name** | `webhooks-demo` |
| **Public** | Enable |
| **Health check path** | `/health` |

Configure the following environment variables:

| Variable | Value | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Required: enables auto-creation of the RavenDB database on startup. Without this, the app crashes with `DatabaseDoesNotExistException`. |
| `Spark__RavenDb__Urls__0` | `http://ravendb:8080` | Internal Sliplane hostname of RavenDB (use the Service Name from Step 1) |
| `Spark__RavenDb__Database` | `WebhooksDemo` | Database name (auto-created when `ASPNETCORE_ENVIRONMENT=Development`) |
| `GitHub__WebhookSecret` | Your webhook secret | Must match the secret in your GitHub App settings |

The `__` double-underscore maps to nested config sections in ASP.NET Core (e.g., `Spark__RavenDb__Urls__0` becomes `Spark:RavenDb:Urls:0`).

> **Important**: Deploy RavenDB first and wait until it is healthy before deploying WebhooksDemo. The app will crash on startup if it cannot reach RavenDB.

### Optional: DataProtection keys

The ASP.NET Core DataProtection system stores encryption keys at `/home/app/.aspnet/DataProtection-Keys`. For production use, add a persistent volume at this path. For this demo, the warning can be safely ignored.

## Step 3: Configure GitHub App webhook URL

Update your GitHub App's webhook URL to point to the deployed WebhooksDemo:

```
https://your-webhooks-demo-url.sliplane.app/api/github/webhooks
```

You can find your app's settings at:
- **Personal account**: https://github.com/settings/apps
- **Organization**: `https://github.com/organizations/{org_name}/settings/apps`

## Step 4: Set up CI/CD

The repository includes a GitHub Actions workflow (`.github/workflows/webhooks-demo-deploy.yml`) that automatically builds and deploys on push to master.

Add the following secret to your GitHub repository settings (`Settings > Secrets and variables > Actions`):

| Secret | Value |
|---|---|
| `SLIPLANE_WEBHOOKS_DEMO_DEPLOY_HOOK` | Your Sliplane deploy hook URL |

You can find the deploy hook URL in your Sliplane service settings.

## Troubleshooting

| Problem | Solution |
|---|---|
| `DatabaseDoesNotExistException` | The database auto-creation only runs when `ASPNETCORE_ENVIRONMENT=Development`. Set this env var, or manually create the database through RavenDB Studio. |
| `Connection refused (localhost:8080)` | The WebhooksDemo is trying to reach RavenDB at localhost. Set `Spark__RavenDb__Urls__0` to the RavenDB service's internal hostname (e.g., `http://ravendb:8080`). |
| RavenDB health check fails on `/` | Ensure all environment variables are set, especially `RAVEN_Setup_Mode=None`. Without this, RavenDB shows the setup wizard which may not return 200 on `/`. |
| `DataProtection-Keys` warning | Add a persistent volume at `/home/app/.aspnet/DataProtection-Keys`, or ignore for non-production use. |
| RavenDB setup wizard appears | Ensure `RAVEN_Setup_Mode=None` is set on the RavenDB service. |
| License not accepted | Ensure both `RAVEN_License` and `RAVEN_License_Eula_Accepted=true` are set. |
| 403 on webhook delivery | Verify the `GitHub__WebhookSecret` env var matches your GitHub App's webhook secret exactly. |
