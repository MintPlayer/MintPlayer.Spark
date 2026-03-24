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

For production deployments behind a reverse proxy like Traefik:

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
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.spark-app.rule=Host(`spark.example.com`)"
      - "traefik.http.routers.spark-app.entrypoints=websecure"
      - "traefik.http.routers.spark-app.tls.certresolver=letsencrypt"
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

## Data Persistence

The `raven-data` volume persists the RavenDB data directory. Without this volume, all data is lost when the container is recreated.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Connection refused` on startup | RavenDB not ready yet | The retry logic handles this automatically. Check `MaxConnectionRetries` if it times out. |
| `Database 'X' does not exist` | Auto-creation disabled | Set `Spark__RavenDb__EnsureDatabaseCreated=true` |
| `UnsecuredAccessAllowed` error | ServerUrl binds to `0.0.0.0` but security set to `PrivateNetwork` | Change to `RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork` |
| `network web not found` | External Docker network missing | Run `docker network create web` |
