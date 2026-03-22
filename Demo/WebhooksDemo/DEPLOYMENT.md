# WebhooksDemo Deployment (Sliplane)

This guide covers deploying the WebhooksDemo to [Sliplane](https://sliplane.io/) with RavenDB as a separate service.

## Architecture

```
GitHub ──POST──► WebhooksDemo (Sliplane service)
                      │
                      ▼
                 RavenDB (Sliplane service)
```

Two Sliplane services in the same project, communicating over the internal network.

## Step 1: Deploy RavenDB

Add a new service in Sliplane:

| Setting | Value |
|---|---|
| **Image URL** | `ravendb/ravendb:latest` |
| **Port** | `8080` |
| **Persistent volume** | Mount on `/opt/RavenDB/Server/RavenData` |

Configure the following environment variables:

| Variable | Value |
|---|---|
| `RAVEN_Setup_Mode` | `None` |
| `RAVEN_License_Eula_Accepted` | `true` |
| `RAVEN_Security_UnsecuredAccessAllowed` | `PrivateNetwork` |
| `RAVEN_ServerUrl` | `http://0.0.0.0:8080` |
| `RAVEN_License` | Your RavenDB license JSON (single line, see below) |

### RavenDB license

Get a free community license at [ravendb.net/license/request](https://ravendb.net/license/request). The license is a JSON object — pass it as a single-line string:

```
{"Id":"your-license-id","Name":"your-name","Keys":["key1","key2","..."]}
```

### Verifying RavenDB

Once the service is running, you can access the RavenDB Studio through the public URL Sliplane assigns to verify the database is operational.

## Step 2: Deploy WebhooksDemo

Add a new service in Sliplane:

| Setting | Value |
|---|---|
| **Image URL** | `ghcr.io/mintplayer/mintplayer.spark/webhooks-demo:master` |
| **Port** | `8080` |
| **Health check path** | `/health` |

Configure the following environment variables:

| Variable | Value | Description |
|---|---|---|
| `Spark__RavenDb__Urls__0` | `http://your-ravendb-hostname:8080` | Internal Sliplane hostname of your RavenDB service |
| `Spark__RavenDb__Database` | `WebhooksDemo` | Database name (auto-created in development) |
| `GitHub__WebhookSecret` | Your webhook secret | Must match the secret in your GitHub App settings |

The `__` double-underscore maps to nested config sections in ASP.NET Core (e.g., `Spark__RavenDb__Urls__0` becomes `Spark:RavenDb:Urls:0`).

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
| `Connection refused (localhost:8080)` | The WebhooksDemo is trying to reach RavenDB at localhost. Set the `Spark__RavenDb__Urls__0` env var to point to the RavenDB service's internal hostname. |
| `DataProtection-Keys` warning | Add a persistent volume at `/home/app/.aspnet/DataProtection-Keys`, or ignore for non-production use. |
| RavenDB setup wizard appears | Ensure `RAVEN_Setup_Mode=None` is set on the RavenDB service. |
| License not accepted | Ensure both `RAVEN_License` and `RAVEN_License_Eula_Accepted=true` are set. |
| 403 on webhook delivery | Verify the `GitHub__WebhookSecret` env var matches your GitHub App's webhook secret exactly. |
