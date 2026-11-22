# Plan — File and image attributes (#700)

**PRD:** [file_attributes_PRD.md](file_attributes_PRD.md). §0 holds the design as decided and §7 the locked decisions.
**Branch:** `feat/700-binary-file-attributes` off `master`, draft PR #702.
**One PR.** It includes:

- the core types;
- the Files packages;
- the stores (FileSystem, AzureBlob, S3);
- ClamAV;
- both image packages;
- the ng-spark entry point;
- the Fleet demo;
- CodeCoverage custom avatars (production);
- the VPS compose services;
- all tests;
- the docs.

Tests run once, in the final CI run (M12). Earlier milestones are checked by building, reading the code, and running single
red→green test classes.

## Status

| Milestone | State |
|---|---|
| Investigation (5 agents: server, client, web, Vidyano, 2sky plugins) | ✅ 2026-11-22 → PRD §1 |
| Grill with the owner: D1–D16, Q15, C1 | ✅ 2026-11-22 → PRD §0, §7 |
| M0 spikes SP1–SP11 | ✅ 2026-11-22 → PRD §9: SP1, SP2, SP3, SP4, SP5, SP6, SP8, SP9, SP10 (S3), SP11. SP7 runs after M6 and SP10 (Azure/Azurite) in M5, as planned |
| M1 core: `BinaryFile`/`Image` types, `SparkFile`/`SparkImage`, `ISparkFileValueHandler`, `fileOptions` model property + validation, removal of the `image` presentation, the swallowed-conversion fix | ✅ 881899af9 (PRD §10 M1) |
| M2 missing-plugin startup error + SPARK0xx analyzer | ✅ startup error in M1; SPARK063 `FileAttributeRuntimeAnalyzer` |
| M3 `Files`: prepare/upload/commit endpoints, sniffer, `ISparkFileSignature`, sanitiser, staging + expiry, `IFileUploadHandler` | ✅ |
| M4 save-path claims (single + list), clear/replace/reorder, deletion driven by the document | ✅ |
| M5 stores: RavenDB, FileSystem, AzureBlob, S3; direct-to-bucket presigned POST policy (SP10: a PUT can't cap size); orphan reconcile; store conformance suite | ✅ (+ `EraseAsync` from SP8; Range moved to M6) |
| M6 downloads: read-path check → presigned redirect or proxy; URLs embedded in read responses; safe headers; Range | ✅ (PRD §10 M6; SP7 browser check runs with M10's Fleet demo) |
| M7 scanning: `IFileScanner`, Production guard, inline/deferred, `Files.ClamAV` | ✅ (PRD §10 M7; corrects SP4: `MaxFileSize ≥ MaxScanSize` in `clamd.conf`; the "scanning…" renderer state moves to M9) |
| M8 images: `IImageProcessor`, `Files.Images.SkiaSharp` + `Files.Images.NetVips`, variants, `--spark-regenerate-variants`, conformance suite | ✅ (PRD §10 M8; `keepOriginal` from SP5 not shipped, NetVips uses `Resize`) |
| M9 ng-spark `files` entry point: edit/detail/column renderers for single + list, URL/data-URI rendering, URL refresh on expiry | ✅ (PRD §10 M9; deviation: the edit contract gains `objectTypeId`/`objectId`) |
| M10 Fleet demo + browser E2E; SP7 serving check in the browser; file attributes inside AsDetail rows (upload, claim, checked download; PRD §8b) | ✅ (PRD §10 M10; deviations: the AsDetail path is `Parent.Field`, not `detail.index.attr`; the browser test leaves the grid thumbnail, Deferred states and AsDetail invoice to the HTTP and spec tests) |
| M11 CodeCoverage: `AvatarUrl` → `Image`, `CustomAvatar`, resolved avatar everywhere, compose services (S3 server + ClamAV, no `sshd`), Trivy CI scan, docs. ClamAV is in (owner, 2026-11-22, after PR #703's swap + memory caps): `ConcurrentDatabaseReload no`, `mem_limit`, an internal network to `coverage-app` plus an egress network for freshclam | ✅ (PRD §10 M11; deviation: Garage is internal only, the app proxies the bytes until the public host's DNS exists) |
| M11b docs (owner, 2026-11-22: "make sure the documentation is up-to-date/created"), see "Docs" below | ✅ `guide-file-attributes.md`, `guide-docker-deployment.md` "File uploads", ng-spark README `/files`, `release-notes-700.md`; the renderer guide, package READMEs and `code-coverage/hosting.md` were already written by M7–M11 |
| M12 push, mark ready, CI green, coverage via MCP | ⏳ |

## Docs (M11b, each written by the milestone that ships the feature and finished here)

- **`docs/guide-file-attributes.md` (new):**
  - enabling the feature (`AddFiles()` and the missing-plugin error);
  - declaring `SparkFile`/`SparkImage`/string attributes;
  - `fileOptions`;
  - choosing a store;
  - scanning;
  - images and variants;
  - lists;
  - the hooks;
  - erasure (`EraseAsync`, History retention, SP8);
  - the security model, what a ClamAV pass does and does not mean, and the revocation TTL.
- **`docs/guide-docker-deployment.md`, new section "File uploads":**
  - the measured native-dependency table per base image (Ubuntu `aspnet:11.0`, Alpine, chiseled; SP5 runtime check);
  - the Garage service with its own volume and per-app keys;
  - the ClamAV service with `AlertExceedsMax yes` and its RAM cost;
  - no `sshd` and the Trivy check;
  - that no app container mounts the storage volume.
- **`docs/guide-custom-attribute-renderers.md`:** the `dataType` field on a registration (SP6) and `withFileAttributes()`.
- **Package READMEs:** every new package (`Files`, `Files.FileSystem`, `Files.AzureBlob`, `Files.S3`, `Files.ClamAV`,
  `Files.Images.SkiaSharp`, `Files.Images.NetVips` (with its LGPL note)) and the ng-spark `files` entry point.
- **Breaking change note** in the release notes: the `image` string presentation became the `Image` data type.
- **`docs/code-coverage/`:** custom avatars, the storage and ClamAV services, and backups of the storage volume.

## Tests

Every milestone ships its own tests, written red first. PRD §8b has the full list, including "Added by the grill":

- unit tests;
- `SparkTestDriver` integration;
- the store and image conformance suites;
- browser E2E on the Fleet host;
- ng-spark vitest specs;
- CodeCoverage avatar and existence-oracle tests;
- the container scan.

Where they go:

- New project `tests/MintPlayer.Spark.Files.Tests` holds the unit tests, the `SparkTestDriver` integration tests and both
  conformance suites. CI service containers provide Azurite, the SP9 server and ClamAV. Join an existing CI group so the run stays
  at ≤ 20 concurrent jobs.
- Core type tests go in `MintPlayer.Spark.Tests`, the analyzer test in `MintPlayer.Spark.SourceGenerators.Tests`, and the browser
  tests in `MintPlayer.Spark.E2E.Tests`.
- Between milestones, run only the single new test class.

## M0 — Spikes

Order: **SP1 → SP11 → SP2 → SP3 → SP10 → SP9 → SP6 → SP5 → SP4 → SP7 → SP8**.

- SP1 (where core branches on `dataType`) and SP11 (whether `fileOptions` survives a sync) decide M1's shape. Stop and revisit the
  PRD if either fails.
- SP7 needs M6, so it runs as a browser check once M6 exists.
- Every verdict goes into PRD §9, with its evidence.

## Notes per milestone

- **M1:** removing `image` touches `SparkStringPresentations`, `ModelSynchronizer.cs:997`, ng-spark `input-type.pipe.ts`, the
  `po-presenter` and `grid-cell` templates, and `CodeCoverage/App_Data/Model/Account.json`. Release note: a breaking change shipped
  as a minor version.
- **M5:** object keys are generated by the server. The local stores proxy the bytes. The external stores sign single-object URLs.
  Deleting a document deletes its objects, and a sweep reconciles orphans.
- **M6:** the TTL is 60 s for downloads and a few minutes for images embedded in responses, with `private, no-store`. Document
  that revoking a right takes effect only after the TTL.
- **M7:** the `clamav/clamav-debian` image (glibc, no `sshd`), on the internal network only.
- **M11:** CodeCoverage is production. The compose services get health checks. Per-app S3 credentials stay secrets and are never
  printed. Backups for the storage volume are documented in `docs/code-coverage/`.
- **Guards, always in the same commit as the change that moves them:**
  - `CsrfSurfaceTests` and the route snapshot (`SPARK_UPDATE_ROUTE_SNAPSHOT`);
  - the translation goldens (`SPARK_UPDATE_GOLDEN`);
  - `--spark-synchronize-model`, and `--spark-synchronize-security` + `--spark-verify-security` for security.json edits.
