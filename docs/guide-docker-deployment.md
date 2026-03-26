# Docker Deployment

This guide covers deploying a Spark application with RavenDB using Docker Compose.

## Prerequisites

- Docker and Docker Compose installed
- A Docker network for your services (if using a reverse proxy like Traefik)

## Quick Start

### 1. Create `docker-compose.yml`

```yaml
services:

  spark-raven:
    image: docker.io/ravendb/ravendb:latest
    environment:
      - RAVEN_Setup_Mode=None
      - RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork
      - RAVEN_ServerUrl=http://0.0.0.0:8080
    volumes:
      - raven-data:/home/ravendb/RavenData
    restart: unless-stopped

  spark-app:
    image: ghcr.io/mintplayer/mintplayer.spark/webhooks-demo:master
    depends_on:
      - spark-raven
    environment:
      - Spark__RavenDB__Urls__0=http://spark-raven:8080
      - Spark__RavenDb__EnsureDatabaseCreated=true
    ports:
      - "5000:8080"
    restart: unless-stopped

volumes:
  raven-data:
```

> **Note:** The `spark-raven` service does not expose ports to the host — it is only reachable by other services in the same Compose stack. The app is exposed on host port 5000 (mapped to container port 8080).

### 2. Start the services

```bash
docker compose up -d
```

The application will be available at `http://localhost:5000`.

### 3. View logs

```bash
docker compose logs -f spark-app
```

### 4. Update to the latest image

```bash
docker compose pull
docker compose up -d
```

## Configuration Reference

### RavenDB Environment Variables

| Variable | Description |
|----------|-------------|
| `RAVEN_Setup_Mode` | Set to `None` to skip the setup wizard |
| `RAVEN_Security_UnsecuredAccessAllowed` | Set to `PublicNetwork` to allow HTTP access from all interfaces |
| `RAVEN_ServerUrl` | The URL RavenDB listens on inside the container |

### Spark Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `Spark__RavenDB__Urls__0` | RavenDB server URL | `http://localhost:8080` |
| `Spark__RavenDb__Database` | Database name | `Spark` |
| `Spark__RavenDb__EnsureDatabaseCreated` | Auto-create database if it doesn't exist | `false` |
| `Spark__RavenDb__MaxConnectionRetries` | Max retry attempts waiting for RavenDB | `30` |
| `Spark__RavenDb__RetryDelaySeconds` | Seconds between retry attempts | `2` |

### Connection Retry

When using Docker Compose, the application container may start before RavenDB is ready. Spark includes built-in retry logic that waits for RavenDB to become available. With the default settings (30 retries, 2 second delay), the app will wait up to ~60 seconds for RavenDB to start.

### Database Auto-Creation

In Development mode, Spark always creates the database if it doesn't exist. For container deployments running in Production mode, set `Spark__RavenDb__EnsureDatabaseCreated=true` to enable auto-creation.

## Production Deployment with Traefik

The repository includes a production-ready `docker-compose.yml` at `Demo/WebhooksDemo/docker-compose.yml` that uses `${...}` placeholders for secrets. These are resolved from a `.env` file you create on the server.

### 1. Create the `.env` file

On your server, create `/var/www/webhooks-demo/.env` (see `Demo/WebhooksDemo/.env.example` for a template):

```env
GITHUB_WEBHOOK_SECRET=whsec_your_webhook_secret
GITHUB_APP_CLIENT_ID=Iv1.your_client_id
GITHUB_PRODUCTION_APP_ID=123456
TRAEFIK_HOST=spark-webhooks.example.com
```

### 2. Place the GitHub App private key

Copy your GitHub App's `.pem` file to the same directory:

```bash
# Copy or create the file
cp ~/my-app.private-key.pem /var/www/webhooks-demo/github-app.pem
chmod 600 /var/www/webhooks-demo/github-app.pem
```

The `docker-compose.yml` mounts this file read-only into the container at `/run/secrets/github-app.pem`.

### 3. Docker Compose file

The included `Demo/WebhooksDemo/docker-compose.yml` sets up:

```yaml
services:

  spark-raven:
    image: docker.io/ravendb/ravendb:latest
    environment:
      - RAVEN_Setup_Mode=None
      - RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork
      - RAVEN_ServerUrl=http://0.0.0.0:8080
    volumes:
      - raven-data:/home/ravendb/RavenData
    networks:
      - web
    restart: unless-stopped

  spark-app:
    image: ghcr.io/mintplayer/mintplayer.spark/webhooks-demo:master
    depends_on:
      - spark-raven
    environment:
      - Spark__RavenDB__Urls__0=http://spark-raven:8080
      - Spark__RavenDb__EnsureDatabaseCreated=true
      - GitHub__WebhookSecret=${GITHUB_WEBHOOK_SECRET}
      - GitHub__ClientId=${GITHUB_APP_CLIENT_ID}
      - GitHub__ProductionAppId=${GITHUB_PRODUCTION_APP_ID}
      - GitHub__PrivateKeyPath=/run/secrets/github-app.pem
    volumes:
      - ./github-app.pem:/run/secrets/github-app.pem:ro
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.spark-webhooks.rule=Host(`${TRAEFIK_HOST}`)"
      - "traefik.http.routers.spark-webhooks.entrypoints=websecure"
      - "traefik.http.routers.spark-webhooks.tls.certresolver=letsencrypt"
    networks:
      - web
    restart: unless-stopped

volumes:
  raven-data:

networks:
  web:
    external: true
```

> **Note:** The `web` network must already exist. Create it with `docker network create web` if it doesn't.

### GitHub App Environment Variables

| `.env` variable | Maps to | Description |
|---|---|---|
| `GITHUB_WEBHOOK_SECRET` | `GitHub__WebhookSecret` | Webhook secret for HMAC-SHA256 signature validation |
| `GITHUB_APP_CLIENT_ID` | `GitHub__ClientId` | GitHub App Client ID for API authentication |
| `GITHUB_PRODUCTION_APP_ID` | `GitHub__ProductionAppId` | GitHub App ID (used for dev-forwarding routing) |
| `TRAEFIK_HOST` | Traefik router rule | Hostname for HTTPS routing |

The private key is provided via a file mount rather than an environment variable, since PEM content is multi-line.

### CI/CD

The GitHub Actions workflow (`webhooks-demo-deploy.yml`) automatically:

1. Builds and pushes the Docker image to GHCR
2. SSHes into the VPS and downloads the latest `docker-compose.yml` from the repository
3. Pulls the new image and restarts the stack

The workflow only needs VPS SSH credentials as GitHub secrets. Application secrets (webhook secret, GitHub App credentials) live in the `.env` file and `github-app.pem` that you manage directly on the server.

## Data Persistence

The `raven-data` volume persists the RavenDB data directory. Without this volume, all data is lost when the container is recreated.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Connection refused` on startup | RavenDB not ready yet | The retry logic handles this automatically. Check `MaxConnectionRetries` if it times out. |
| `Database 'X' does not exist` | Auto-creation disabled | Set `Spark__RavenDb__EnsureDatabaseCreated=true` |
| `UnsecuredAccessAllowed` error | ServerUrl binds to `0.0.0.0` but security set to `PrivateNetwork` | Change to `RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork` |
| `network web not found` | External Docker network missing | Run `docker network create web` |
