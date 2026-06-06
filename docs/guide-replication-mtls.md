# Spark Replication — mTLS Setup Guide

This guide walks an operator through enabling mutual-TLS authentication between
Spark modules (R2-C1 / R2-C2 / R2-H7). The audit found `/spark/etl/deploy` and
`/spark/sync/apply` shipped without any authentication; mTLS is the design Spark
picked to close that gap.

## Threat model

- Each Spark module (Fleet, HR, Audit, …) deploys with its own X.509 client
  certificate. The certificate **is** the module's identity — same cert on every
  outbound call.
- The first time a module registers in `SparkModules`, its cert thumbprint is
  pinned to `moduleInformations/{Name}.ClientCertificateThumbprint`. Subsequent
  re-registrations with a different thumbprint are refused (key rotation goes
  through an operator-driven delete-then-register flow).
- Inbound `/spark/etl/deploy` and `/spark/sync/apply` look up the
  `RequestingModule` field in the body, find the pinned thumbprint, and refuse
  the request unless the presenting client cert matches.

## Modes

Configured via `Spark.Replication.ClientCertificate.Mode`:

| Mode | Inbound behaviour | Outbound behaviour | When to use |
|------|-------------------|--------------------|-------------|
| `Auto` (default) | `Production` outside `Development` env | Same | Most apps — picks the right mode from `ASPNETCORE_ENVIRONMENT` |
| `Production` | Require cert, verify thumbprint | Attach configured cert | Production-like environments |
| `Development` | Skip thumbprint check, warn per call, still verify the module is registered | Attach cert if configured, skip otherwise | Local dev with multiple modules on `localhost` |
| `Disabled` | Pass-through, no validation, no warning | No cert attached | Legacy demos only — new apps should pick one of the above |

## Quick start (development)

```jsonc
// appsettings.Development.json
{
  "Spark": {
    "Replication": {
      "ModuleName": "Fleet",
      "ModuleUrl": "https://localhost:5101",
      "ClientCertificate": {
        // Auto resolves to Development in this env
        "Mode": "Auto"
      }
    }
  }
}
```

That's it for local dev. Modules register and call each other over plain HTTP;
the framework logs a warning on every accepted call so the relaxed mode is
visible in your terminal.

## Quick start (production)

### 1. Generate per-module certs

```bash
# Self-signed CA (once)
openssl req -x509 -newkey rsa:4096 -keyout spark-ca.key -out spark-ca.crt \
  -days 3650 -nodes -subj "/CN=Spark Internal CA"

# Per-module cert (repeat per module: Fleet, HR, …)
MODULE=Fleet
openssl req -newkey rsa:4096 -keyout "$MODULE.key" -out "$MODULE.csr" -nodes \
  -subj "/CN=$MODULE"
openssl x509 -req -in "$MODULE.csr" -CA spark-ca.crt -CAkey spark-ca.key \
  -CAcreateserial -out "$MODULE.crt" -days 365
openssl pkcs12 -export -in "$MODULE.crt" -inkey "$MODULE.key" \
  -out "$MODULE.pfx" -password pass:<pick-a-password>

# Read the thumbprint to paste into appsettings (uppercase, no spaces, no colons)
openssl x509 -in "$MODULE.crt" -fingerprint -sha256 -noout \
  | sed 's/SHA256 Fingerprint=//;s/://g'
```

### 2. Configure Kestrel to accept client certs

```jsonc
// appsettings.json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5101",
        "ClientCertificateMode": "AllowCertificate"  // or "RequireCertificate"
      }
    }
  }
}
```

`AllowCertificate` lets the cert flow up to the Spark validator (which decides
to accept or reject); `RequireCertificate` fails at TLS handshake for any call
missing a cert (stricter, but blocks non-replication calls too).

### 3. Configure this module's cert

```jsonc
{
  "Spark": {
    "Replication": {
      "ModuleName": "Fleet",
      "ModuleUrl": "https://fleet.internal:5101",
      "ClientCertificate": {
        "Mode": "Production",
        "Thumbprint": "AB12CD34...",   // from step 1
        "CertificateFile": "/secrets/Fleet.pfx",
        "CertificatePassword": "<from-secret-store>"
      }
    }
  }
}
```

Repeat for each module with its own thumbprint + PFX.

### 4. Trust the issuing CA on every peer

```bash
# Linux containers — copy the CA into the trust store
cp spark-ca.crt /usr/local/share/ca-certificates/spark-ca.crt
update-ca-certificates
```

For Windows hosts: import `spark-ca.crt` into the LocalMachine `Trusted Root
Certification Authorities` store.

### 5. (Optional) Per-target overrides

If different peers use different CAs (or you want each module pair to have its
own cert):

```jsonc
{
  "Spark": {
    "Replication": {
      "ClientCertificate": {
        "Mode": "Production",
        "Thumbprint": "AB12CD34...",                // Fleet's identity
        "CertificateFile": "/secrets/Fleet.pfx",     // default for any peer
        "PerTargetOverrides": {
          "Audit": {
            "CertificateFile": "/secrets/Fleet-to-Audit.pfx",
            "CertificatePassword": "..."
          }
        }
      }
    }
  }
}
```

Most deployments don't need this — one identity per module is the simple,
correct shape.

## Rotating a module's certificate

1. Generate the new cert (same `CN=ModuleName`).
2. Stop the module.
3. Delete `moduleInformations/{ModuleName}` from the SparkModules database
   (e.g. via the Raven Studio).
4. Restart the module with the new cert configured — registration pins the new
   thumbprint.

The framework intentionally refuses silent overwrite because an attacker
spinning up a malicious "Fleet" pod is the failure mode the pin prevents — see
R2-H7.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Inbound returns 401 `"Client certificate required"` | Kestrel didn't surface the cert. Check `ClientCertificateMode` is `AllowCertificate` or stricter; check the caller actually presents one (`openssl s_client -cert ...`). |
| Inbound returns 403 `"Forbidden"`, log says `ThumbprintMismatch` | Calling module's pinned thumbprint differs from the cert it presented. Either rotate (delete-then-register) or fix the misconfigured cert. |
| Inbound returns 403 `"Forbidden"`, log says `UnknownModule` | `RequestingModule` field in the body refers to a module that never registered. Verify the module ran far enough to call `RegisterAsync` once. |
| Outbound calls fail TLS handshake | Peer doesn't trust the issuing CA — step 4 above. |
| Pre-mTLS legacy entries fail closed | Existing `moduleInformations/{X}` documents without `ClientCertificateThumbprint`. Delete and re-register each, OR temporarily set `Mode = Development` while rolling out. |

## Related security findings

- R2-C1 — unauthenticated `/spark/etl/deploy` accepting arbitrary RavenDB ETL
- R2-C2 — unauthenticated `/spark/sync/apply` enabling CRUD on any collection
- R2-H7 — trust-on-first-claim module URL pinning
