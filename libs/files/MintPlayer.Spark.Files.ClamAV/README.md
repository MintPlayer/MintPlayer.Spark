# MintPlayer.Spark.Files.ClamAV

A malware scanner for [MintPlayer.Spark.Files](../MintPlayer.Spark.Files/README.md). Every upload is streamed to clamd
over TCP (`INSTREAM`) at commit, and only a clean file can be claimed by a save and served.

```csharp
spark.AddFiles(files => files
    .UseClamAV(o =>
    {
        o.Host = "clamav";                    // internal network only: clamd has no authentication
        o.MaxStreamLength = 25 * 1024 * 1024; // ≤ clamd's StreamMaxLength, ≥ Spark:Files:MaxFileBytes
    })
    .ConfigureOptions(o => o.ScanMode = SparkFileScanMode.Inline)); // the default; Deferred scans in the background
```

Or bind `ClamAVOptions` from configuration: `files.UseClamAV(builder.Configuration.GetSection("ClamAV"))`.

## What the verdicts mean

The client is our own (no package), so what counts as a pass is exact. It is fail-closed:

| clamd reply | Verdict | The upload |
|---|---|---|
| `stream: OK` | Clean | claimable |
| `stream: <signature> FOUND`, `Heuristics.Limits.Exceeded.*` included | Infected | refused; the bytes are deleted, the staging document stays as a tombstone until it expires |
| `… ERROR`, no reply, an unreadable reply, a connection reset, a timeout | Failed | not claimable; committing it again retries the scan (deferred: the worker retries) |
| a file larger than `MaxStreamLength` | Failed | refused without being sent |

A ClamAV pass means clamd's signatures know nothing bad about the bytes. It does not make a file safe to open. Spark
still serves every download with `nosniff`, a sandbox CSP and `attachment` (inline only for raster images).

## Running clamd

Use the `clamav/clamav-debian` image (glibc, no `sshd`) with this package's [`clamd.conf`](clamd.conf) mounted read-only
over `/etc/clamav/clamd.conf`. It sets:

- **`AlertExceedsMax yes`.** Without it, an archive that unpacks to more than clamd will scan answers `OK`, and the part
  over the limit is never looked at (PRD SP4).
- **`MaxFileSize 100M` and `MaxScanSize 100M`. Keep `MaxFileSize ≥ MaxScanSize`.** ClamAV 1.5 trims an archive member over
  `MaxFileSize` to that size, scans the start and alerts on nothing. Only exceeding the `MaxScanSize` total alerts.
  Measured on 1.5.5: with `MaxFileSize 25M` a zip holding one 150 MiB member scanned `OK`. With 100M it answered
  `Heuristics.Limits.Exceeded.MaxScanSize FOUND`.
- **`StreamMaxLength 25M`.** Over it clamd drops the connection instead of answering. Keep
  `StreamMaxLength ≥ MaxStreamLength ≥ Spark:Files:MaxFileBytes`.

```yaml
services:
  clamav:
    image: clamav/clamav-debian:stable
    volumes:
      - ./clamd.conf:/etc/clamav/clamd.conf:ro
      - clamav-db:/var/lib/clamav   # keeps the signatures across restarts
    networks: [internal]            # never publish 3310
```

- **Memory:** about 1.05 GiB once the signatures are loaded. **First start:** about 70 s (signature download); clamd
  is ready when it logs `socket found, clamd started`.
- The image updates its signatures itself (freshclam). The image also bundles a snapshot, so `CLAMAV_NO_FRESHCLAMD=true`
  starts it without the download (CI does this; production should keep freshclam on).

## Health check

`UseClamAV` adds a `clamav` health check (tag `ready`). It scans the EICAR test string and is healthy only when clamd
detects it: a clamd with missing or broken signatures still answers `PING`. Map it with
`app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") })`. The EICAR string is assembled
at runtime, so no file in the package carries the signature.

Licence: MIT. clamd itself (GPL-2.0) runs as a separate service; nothing of it is linked.
