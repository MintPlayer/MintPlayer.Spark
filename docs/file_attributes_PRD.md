# PRD — File and image attributes (#700)

**Issue:** [#700](https://github.com/MintPlayer/MintPlayer.Spark/issues/700): attributes can only hold basic data types. A
Spark app should be able to declare a `BinaryFile` attribute. ng-spark should render it as an `<input type="file">`
with its own design. The issue also asks for:

- the feature off by default;
- a hook for the app;
- an antivirus abstraction;
- an Image/BinaryFile distinction;
- optional downscaling.

**Plan:** [file_attributes_plan.md](file_attributes_plan.md)
**Status:** drafted 2026-11-22 from a four-agent investigation on `master` at `5433a99dc`. The four areas were:

1. Spark's server model and plugin pattern
2. ng-spark renderers and `bs-file-upload`
3. web research on ASP.NET Core uploads, RavenDB attachments, antivirus and image libraries
4. how Vidyano does it

A fifth agent later read the 2sky upload plugins and the BigFileUploader service (§1.5).

**Grilled with the owner on 2026-11-22.** All decisions are locked in §7. **§0 is the design as decided.** It supersedes §3–§6
wherever they differ; those sections keep the original draft for the record. The open questions are spikes (§8).
**One PR.** It includes:

- the core types;
- the Files packages;
- the store packages;
- the ClamAV package;
- both image packages;
- the ng-spark entry point;
- the Fleet demo;
- CodeCoverage custom avatars (production);
- the VPS compose services;
- all tests;
- the docs.

---

## 0. The design as decided (2026-11-22)

1. **Core owns the data types `BinaryFile` and `Image`** (D2).
   - `SparkFile`/`SparkImage` live in Abstractions, together with the `GetDataType` mapping and the wire shape.
   - Core defines the interfaces (`ISparkFileValueHandler` and others), and the Files plugin implements them.
   - **Libraries cannot add their own data types.** That is a non-goal.
2. **A file attribute without the plugin fails** (D2b).
   - At startup: "`Car.Photo` is a file attribute; call `spark.AddFiles()`".
   - At build time, a SPARK0xx Warning (the SPARK039 pattern, `StampedContractRuntimeAnalyzer.cs`) fires when the host doesn't
     reference `MintPlayer.Spark.Files`.
3. **The data type is always `Image` or `BinaryFile`. The CLR property type decides where the value comes from** (D1, D1b).
   - **`SparkFile`/`SparkImage` (or `List<…>`):** a stored file, the only shape the upload renderer writes.
   - **`string`:** an `https:` URL or a `data:` URI. Spark reads and displays it and never writes base64 into it. A raster MIME
     type is rendered; anything else is served as a download. An editable string Image accepts `https:` only and is never
     rendered as a link.
   - **The old `image` string presentation is removed.** `CodeCoverage.Account.AvatarUrl` becomes `Image` and keeps its URL
     value. URL images are never downloaded and stored again.
4. **Transport: upload first, then the save claims a token. This is the only transport** (D3).
   - The steps are prepare (a batch of N files) → upload → commit (sniff + scan + variants) → the JSON save claims the tokens.
   - Tokens are bound to user + type + attribute (+ id), are single-use and expire quickly. Abandoned staging is deleted, bytes
     and metadata, on **every** store.
   - CSRF/antiforgery applies to every step.
5. **Stores are chosen by the developer** (D12): RavenDB attachments (`Files`), `Files.FileSystem`, `Files.AzureBlob` and `Files.S3`.
   - **Direct-to-bucket uploads are in this PR** (D6 = B). S3 issues a presigned POST policy with `content-length-range` (SP10: a presigned PUT cannot cap size), Azure a write-only SAS. The commit
     step reads the object back, sniffs it and scans it before its token can be claimed. The size limit is enforced through the
     URL's conditions where the store supports them, and always checked again at commit.
   - External stores delete objects driven by the document (on delete, replace and clear), with a reconcile sweep for orphans.
6. **One credential per file, issued in batches** (D15). Read responses embed a presigned URL for each visible file or thumbnail,
   signed when the read is authorised (a local HMAC, so no extra round trip). An `<img>` whose URL has expired asks for a fresh one.
   Proxied stores embed a Spark URL instead.
7. **Downloads are checked, then redirected** (D14). Spark runs the read pipeline (row security, attribute rights, hidden
   attributes, uniform 401/404) and then `302`s to a presigned URL.
   - The TTL is 60 s for downloads and a few minutes for images in responses. Responses are `private, no-store`.
   - **Revoking a right takes effect only after the TTL.** This is documented.
   - FileSystem and RavenDB proxy the bytes through Spark.
8. **Scanning fails closed** (D5, D6 inline).
   - `AddFiles()` refuses to start in Production without an `IFileScanner`, unless the app sets `AllowUnscannedFiles`.
   - Scanning is inline by default; deferred is optional.
   - Only `Clean` files are claimable or served.
   - `Files.ClamAV` talks to clamd over TCP.
   - **A ClamAV pass means "no known malware", not "safe".** The real protection is sniffing, allow-lists, sandboxed serving and
     the separate origin.
9. **The accept list fails closed** (D4).
   - A `BinaryFile` with no `accept` refuses everything, and `*` is refused.
   - Every allowed type must be recognisable by magic bytes. An unusual format needs an `ISparkFileSignature`.
   - `Image` defaults to PNG, JPEG, GIF and WebP. SVG is never an Image.
10. **Options live in the model JSON** (D16).
    - `fileOptions` (`accept`, `maxBytes`, `maxCount`, `variants`, `delivery`) is a declared, hand-editable property on the
      attribute model type, so a synchronize preserves it.
    - At startup, Spark validates it: empty `accept`, `*`, unrecognisable types, a `maxBytes` above the ceiling and malformed
      variants are all reported.
    - Configuration (`Spark:Files`) can only tighten these limits.
11. **Lists** (D9): `List<SparkFile>`/`List<SparkImage>` with `isArray: true`.
    - Each item gets its own token claim.
    - Adding, removing and reordering items are tracked separately.
    - `maxCount` defaults to 10.
    - The grid shows the first thumbnail plus "+N".
12. **Images** (D7, D7b, D11).
    - **Licences:** core, `Files` and the interface packages use only permissive licences. Implementation packages may be LGPL.
    - **Implementations:** `Files.Images.SkiaSharp` (MIT) and `Files.Images.NetVips` (LGPL) both ship, behind `IImageProcessor`,
      and pass **one shared conformance suite**.
    - **Variants** are generated at commit (defaults `thumb` 160 and `medium` 800). Commits process files in parallel with a
      bounded degree. The sizes can be changed or switched off in `fileOptions`. When a size is missing, the renderer falls back
      to the nearest larger one. `--spark-regenerate-variants` rebuilds them.
13. **Storage for this repo's apps** (D13) is a **Garage** S3-compatible container (SP9: static image, 6 MiB idle) on its own volume, ~~behind
    Traefik on its own host. That host gives a cookie-less content origin.~~ **Deferred (M11, 2026-11-22): internal only, with the
    app proxying the bytes, until the host's DNS record exists** (§10 M11). It is used through `Files.S3`. Hard requirements:
    - **No app container mounts the storage volume.**
    - Each app has its own credentials, scoped to its own bucket.
    - Buckets are private, with listing off.
    - Object keys are generated by the server.
    - Nothing is executed on the storage side.
    - ClamAV runs as a `clamav/clamav-debian` container on the internal network only, with `AlertExceedsMax yes` (SP4).
      ~~and only if the VPS has about 1 GiB free~~ **Decided (owner, 2026-11-22): CodeCoverage ships clamd in M11.** The VPS
      had 1.3 GB available before the memory work of PR #703 (4 GB swap with swappiness 10, `coverage-app` capped at 1536m,
      workstation GC for the three .NET apps), and 3.8 GB right after it. Because swap is a safety net and not working memory,
      clamd gets `ConcurrentDatabaseReload no` (no second signature database in RAM during the daily reload, so no ~2 GiB peak;
      scans wait during the reload instead) and a `mem_limit`. Uploads fail closed while clamd is down or starting.
14. **Container hygiene (owner, 2026-11-22: \*sshole).**
    - Background: [CVE-2026-123158](https://www.stepsecurity.io/blog/cve-2026-123158-sshhole-openssh-musl-password-bypass)
      (musl `crypt()` returns `"*"` on failure, and `sshd` without PAM then accepts the login). It affects Alpine 3.16–3.24
      `openssh-server` and is fixed in OpenSSH 10.6p2.
    - New containers prefer glibc or distroless images (e.g. `clamav/clamav-debian`) and **never run `sshd`**.
    - A CI Trivy scan of the compose images fails on CVE-2026-123158 and on any `openssh-server` package.
    - Today no image in this repo runs `sshd` (checked 2026-11-22). The VPS host is outside this PR.
15. **Adopters in this PR** (D10).
    - The Fleet demo: `Car.Photos` (`List<SparkImage>`) and `Car.RegistrationDocument` (`SparkFile`).
    - **CodeCoverage custom profile images** (production, Q15). `AvatarUrl` stays read-only and synced from the forge.
      `CustomAvatar` (`SparkImage?`) can be uploaded by the account's owner and cleared by admins. Every place shows the same
      resolved `CustomAvatar ?? AvatarUrl`. Its visibility is the account's, and its download returns the uniform 401/404, so it
      can't confirm that an account exists.
    - The storage and ClamAV services deploy through `apps/CodeCoverage/docker-compose.yml`, with health checks.

---

## 1. What the investigation found

### 1.1 Spark today (server)

- **The data-type set is closed.** `SparkModelShape.GetDataType` is a hard-coded switch
  (`libs/spark/MintPlayer.Spark.Abstractions/Model/SparkModelShape.cs:191-216`). An unknown complex type becomes `AsDetail`,
  and anything else becomes `string`.
  - Saving goes through a hard-coded coercion chain in `EntityMapper` (`libs/spark/MintPlayer.Spark/Services/EntityMapper.cs:1350-1414`).
    Its fallback is `Convert.ChangeType`, and **a failure there is silently swallowed**.
  - Reading goes through `ConvertValueForWire` (`EntityMapper.cs:579-592`).
  - **There is no server-side extension point for a data type.** A plugin cannot add one today.
- **`image` is already taken.** `SparkStringPresentations` (`SparkStringPresentations.cs:29-42`) has a string presentation
  `image` that means "a string holding an image **URL**". The model synchronizer keeps it (`ModelSynchronizer.cs:997`). The client
  renders it as `type=url` (`ng-spark/pipes/src/input-type.pipe.ts:18-22`), as `<img src>` on the detail page
  (`po-detail/src/spark-po-presenter.component.html:162`), and in the grid (`grid/src/spark-grid-cell.component.html:33`).
- **Plugin pattern** (e.g. `libs/soft_delete`, `SparkSoftDeleteExtensions.cs:29-53`):
  - `ISparkBuilder.AddX(Action<Options>?)` binds the `Spark:X` config section, then applies the code delegate (code wins).
  - It registers interceptors and adds endpoints through `builder.Registry.AddEndpoints(...)`; the endpoints are source-generated.
  - It declares its store with `[SparkRequiresStore]`.
  - It declares new rights with `[assembly: SparkReservedActions]`, and nobody has them until `security.json` grants them.
  - **A plugin that isn't added is off.** That is the repo's existing "disabled by default".
- **Guards:**
  - CSRF: `SparkAntiforgeryMiddleware.cs:34-60`, plus endpoint metadata `RequireAntiforgeryTokenAttribute(true)` (`Update.cs:20`).
    `CsrfSurfaceTests` and the route snapshot (`RouteTableSnapshot.cs:159,178`, which records `request-size-limit` per route) police
    every new endpoint.
  - Rate limiting is opt-in (`AddRateLimiter()`).
  - **No Spark endpoint sets a request-size limit today.**
- **Binary handling already in the repo, all app-level and none in `libs/spark`:**
  - CodeCoverage uploads: `[FromForm] IFormFileCollection`, `[RequestSizeLimit]`, a file-count cap, and RavenDB attachments with a
    reaper cron (`apps/CodeCoverage/CodeCoverage/Controllers/UploadsController.cs:140-160`, `Ingestion/ReapReportAttachmentsCronJob.cs`).
  - Avatars: `CommitGraphAvatars.cs:153-214`.
  - SeoTester PDFs: `ReportPdf.cs:88,203`.
  - Iconic stores scene attachments.
  - Nothing uses `MultipartReader`.

### 1.2 ng-spark today (client)

- **Renderer registry:**
  - `SPARK_ATTRIBUTE_RENDERER_SETS` / `withAttributeRenderers([...], order)` (`renderers/src/spark-attribute-renderer-registry.ts:5-83`),
    with detail, column, edit, row and leading slots. An app's set wins over the shipped one.
  - The edit contract takes `value`, `attribute`, `options` and a `valueChange` callback (`spark-attribute-renderer.ts:80`).
  - **The lookup is by `attr.renderer` only, never by `dataType`** (`po-form/src/spark-po-form.component.ts:684-701`).
- **Entry-point pattern to copy:** `@mintplayer/ng-spark/score-renderers` with `withScoreRenderers()`
  (`score-renderers/src/provide-spark-score-renderers.ts:29`).
- **Saves are JSON only.** `SparkService.create/update` → `postWithEnvelope` (`services/src/spark.service.ts:264,281,455`).
  Nothing uses `FormData`.
  - XSRF is `withXsrfConfiguration({cookieName:'XSRF-TOKEN', headerName:'X-XSRF-TOKEN'})` (`auth/src/lib/provide-spark-auth.ts:38`).
    Angular's interceptor also adds the header to a `FormData` POST.
- **`bs-file-upload` is already installed** (`@mintplayer/ng-bootstrap/file-upload`, 22.35.0, which is ng-spark's peer floor):
  - Model and inputs: `files` two-way `FileUpload[]`, `accept`, `multiple`, `disabled`, captions, and aria-live announcement functions.
  - Output: `filesDropped`.
  - UI: a drop zone around a native `<input>`, plus one row per file with a progress bar that the host updates.
  - Template slot: `*bsFileUploadFileTemplate`.
  - **It does no HTTP**, which is what we want: the renderer owns the upload.

### 1.3 How Vidyano does it

All line numbers are in `C:\Repos\Vidyano.Service` / `C:\Repos\Vidyano`. The server source is decompiled, so lines are approximate.

- **Two data types, `BinaryFile` and `Image`** (`DataTypes.cs:160-161`).
  - BinaryFile: a CLR `BinaryFile { FileName; byte[] Data }`. On the wire **and in the document** it is the string `"filename|base64"`
    (`BinaryFile.cs:19-105`).
  - Image: a `byte[]`, base64 (`DataTypes.cs:316`). A `byte[]` property whose name contains PHOTO, IMAGE or PICTURE becomes Image
    automatically.
- **Transport:** `ExecuteAction` takes multipart (a `data` JSON part plus one part per attribute path, `detail.index.attr` for detail
  rows). It buffers each part fully in memory, cleans the file name, and converts the part back to base64 (`WebController.cs:661-746`).
  A cross-origin client base64s the file itself; a same-origin client uses a hidden-iframe form post (`service.ts:891-961`).
- **Checks:**
  - Images: magic bytes for JPEG/PNG/GIF/WEBP/BMP, at least 50 bytes, and `MaximumImageByteLength` 20 MB (`Advanced.cs:305`,
    `VidyanoConfiguration.cs:172`). The check is virtual, so apps can override it. SVG is rejected because it isn't listed.
  - **BinaryFile has no server-side check at all.** There is only the browser `accept` hint.
- **Downloads:** `GetStream` (`WebController.cs:1225`) runs an action and streams "the first BinaryFile attribute", or uses user-bound
  registered streams. The response is `Content-Disposition: attachment` with an octet-stream default.
- **Antivirus is not in Vidyano.** CronosCore adds `IScannable` and a `MalwareScanner` sweep worker that quarantines files
  (`CronosCore.Common/AntiVirus/IScannable.cs`, `CronosCore.RavenDB/AntiVirus/MalwareScanner.cs:148,188`).
  CronosCore's `IStorageContainer` + `StorageKey` keeps bytes out of the document.
- **No thumbnails.** Grids paint the full-size image as a base64 data-URI background (`query-grid-cells.ts:130-155`).
- **Copy:**
  - multipart keyed by attribute path;
  - the file-name cleaning;
  - overridable magic-byte checks, with no SVG;
  - attachment downloads;
  - user-bound download tokens;
  - a storage seam with scan-before-serve.
- **Avoid:**
  - base64 in the JSON (+33 %, fully buffered);
  - bytes in the document;
  - full-size images in grids;
  - "first BinaryFile attribute" download rules;
  - unchecked generic files.

### 1.5 The 2sky upload plugins and BigFileUploader (`C:\Repos\Plugins`, `C:\Repos\BigFileUploader`)

- **BigFileUpload and TinyFileUpload** are front ends for the separate **BigFileUploader** service.
  - BigFileUpload handles several files in an iframe dialog that reports back with `postMessage`. TinyFileUpload handles one file
    inline, with an explicit close-session action.
  - The flow: a server action creates a session (`x-authKey`), the browser uploads 1 MB chunks (staged as Azure block blobs and
    committed together), and the result is a 1-hour SAS download URL. That URL is passed through `attribute.Options`, and the app
    copies the bytes on save (`BigFileUpload/GenerateBigFileSession.g.cs.pp:30-57`, `AzureBlobStorageRepository.cs:19-64`).
- **FileUpload** uses Resumable.js in 5 MB chunks to the Cronos Documents module. **ImageUpload** uploads straight to Azure Blob
  with a SAS token handed to the client, and stores `"url|thumbUrl"` in a string.
- **All of them upload first and link afterwards.** None sends files inside the save. This is evidence for D3.
- **The service's own `STRIDE.md`** lists 9 medium findings:
  - unauthenticated chunk uploads, where the GUID is the only secret (E-01);
  - CORS open to any origin (E-02);
  - no size or count limits (T-02/T-03);
  - no rate limit (D-01);
  - whole chunks held in memory (D-02).

  Beyond the report:
  - the receiving side of the `postMessage` does no origin check;
  - blobs outlive their metadata, because nothing deletes them;
  - **no virus scanning.**
- **What we copy:**
  - a session created on the server and bound to the target;
  - stage-then-commit;
  - a storage abstraction with a local version;
  - short-lived read URLs generated at read time;
  - expiry of abandoned sessions.

  **What we do differently:** every finding listed above.

### 1.4 Platform facts (web research)

- **No FTP. Plain HTTP(S) `multipart/form-data` is the mechanism.**
  - Buffered `IFormFile` spills to a temp file above 64 KB. Streaming uses `MultipartReader`.
  - Default limits: Kestrel `MaxRequestBodySize` is 30,000,000 bytes, `FormOptions.MultipartBodyLengthLimit` is 128 MB, and IIS
    `maxAllowedContentLength` is 30,000,000 bytes (attributes can't override IIS). `[RequestSizeLimit]` / `IHttpMaxRequestBodySizeFeature`
    override per endpoint. ([file-uploads](https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads))
- **Microsoft's own advice:**
  - never trust the client file name or content type;
  - use an extension allow-list **and** magic-number check;
  - store outside the web root;
  - enforce size limits server-side;
  - scan every upload, and for volume, quarantine and scan in a background service.
- **Minimal APIs (.NET 8+) auto-require antiforgery** for `IFormFile`/`[FromForm]`
  ([breaking change](https://learn.microsoft.com/dotnet/core/compatibility/aspnet-core/8.0/antiforgery-checks)).
  Spark's own middleware already covers this.
- **Resumable uploads:** tus via tusdotnet 2.12 (PATCH `application/offset+octet-stream`). This is not needed for v1; keep the storage
  seam able to accept chunks later.
- **RavenDB attachments:**
  - There is no hard size cap, but large attachments cost disk and replication.
  - **Remote attachments** (7.2) move bytes to Azure Blob or S3. Caveats:
    - non-sharded databases only;
    - a deleted document does **not** delete the remote blob;
    - backups exclude the remote blobs.
  - Revisions keep a reference to the attachment, so a replaced file survives in revision history (GDPR, §5).
  - The session API has `GetRange`, but RavenDB documents no HTTP Range support, so a Range request must be mapped by Spark's endpoint.
  - ([attachments FAQ](https://docs.ravendb.net/7.2/document-extensions/attachments/faq),
    [remote](https://docs.ravendb.net/document-extensions/attachments/configure-remote-attachments))
- **Antivirus:**
  - **No engine can be bundled in a NuGet package.** ClamAV is GPLv2 and runs as a daemon. A clamd client is Apache/MIT:
    nClam 9.0 (Apache-2.0, maintenance unclear), or our own client for the small `INSTREAM` protocol.
  - AMSI (`AmsiScanBuffer`) is Windows-only, and whether it works on Server without Defender is unconfirmed.
  - Defender for Storage is Azure Blob only.
  - **VirusTotal's public API shares uploads with third parties: never a default**
    ([ToS](https://docs.virustotal.com/docs/terms-of-service)).
- **Image libraries:**
  - **ImageSharp's Six Labors Split License** requires a paid licence for for-profit users above USD 1M revenue. That makes it
    unsuitable as a transitive dependency of an open-source framework.
  - **SkiaSharp** is MIT, with native assets (Linux needs `SkiaSharp.NativeAssets.Linux.NoDependencies`).
  - **NetVips** wraps libvips (LGPL) and is the fastest and leanest.
  - **Magick.NET** is Apache, but ImageMagick has a long CVE history.
  - Hardening applies to any of them:
    - cap the pixel count before decoding (decompression bombs);
    - apply the EXIF orientation, then re-encode, which strips EXIF/GPS;
    - **treat SVG as a BinaryFile, never an image** (script);
    - serve with `X-Content-Type-Options: nosniff`, a server-determined `Content-Type`, `Content-Disposition: attachment` (inline only
      for allow-listed raster types) and `Content-Security-Policy: sandbox; default-src 'none'`;
    - use a cookie-less content origin where available;
    - use RFC 6266 `filename*`.

---

## 2. Goals

- **G1** An app declares a file attribute in its entity library as a typed property, e.g. `public SparkFile? Contract { get; set; }`
  or `public SparkImage? Photo { get; set; }`.
  - The model synchronizer maps it to a data type (`BinaryFile` / `Image`). The developer writes no endpoint, no storage code and no
    renderer.
- **G2** **Off unless asked for.**
  - Without the Files plugin, a `SparkFile` property is a startup error that names the missing `AddFiles()` (an analyzer if feasible),
    never a silently dead attribute.
  - With the plugin, an attribute accepts nothing until its allow-list (extensions/MIME) and size cap are known. The defaults are
    conservative and stated in §4.4.
- **G3** **Bytes never travel in the PO JSON and never live in the document.**
  - Upload is multipart and streamed. Storage is a RavenDB attachment by default, behind a storage seam.
  - The PO carries metadata only.
- **G4** **The server decides what a file is.**
  - The server sniffs the content type from magic bytes and checks it against the allow-list. The client's content type and extension
    are hints only.
  - The file name is sanitised and only ever displayed or used as a download name, never as a path.
- **G5** **A file is served only to someone who may read that attribute of that object**, through the same read-path rules as the
  attribute's value (#509: hidden stays hidden).
  - Serving is safe by construction: attachment disposition, nosniff, a CSP sandbox, inline only for allow-listed raster images, and
    Range support.
- **G6** **A scanner seam with fail-closed semantics.**
  - `IFileScanner` plus a scan status on the file. A file that isn't `Clean` is not downloadable.
  - In Production, the app must either register a scanner or opt out explicitly. A ClamAV provider ships as its own package.
- **G7** **Image is a subtype of BinaryFile.**
  - It adds raster-only validation, pixel caps, and re-encoding (orientation applied, metadata stripped).
  - The detail page shows the image. The grid shows a thumbnail, never the original.
  - Optional variants (downscaled sizes) live in an image-processing package.
- **G8** **Developer hooks.** The app can:
  - validate or refuse an upload (after sniffing, before the file is accepted);
  - react to an accepted, clean file;
  - plug in its own storage (e.g. Azure/S3) and scanner.
- **G9** **ng-spark ships the renderers** in an entry point `@mintplayer/ng-spark/files`, `withFileRenderers()`.
  - They are built on `bs-file-upload`: drag-drop, progress, clear/replace, and a download link. Images get a preview.

### Non-goals (v1)

- Resumable/chunked uploads (tus).
- Multiple files in one attribute (`List<SparkFile>`; §7 D9 may pull it in).
- In-browser image editing/cropping.
- Video or audio transcoding.
- SVG rendering.
- Public, anonymous, CDN-cacheable file URLs.

---

## 3. Shape of the solution (original draft)

> **Superseded where it differs from §0.** The main differences:
> - The data types live in core (no generic extension point).
> - The `image` presentation is removed, not renamed to `imageUrl`.
> - Direct-to-bucket uploads and the Azure/S3/FileSystem stores are in.
> - Lists are in.
> - Options live in the model JSON `fileOptions`, not a C# attribute.
> - Both image implementations ship.
> - The demo is Fleet plus CodeCoverage, not HR.

### 3.1 Packages

| Package | Contents | Depends on |
|---|---|---|
| `MintPlayer.Spark.Files.Abstractions` (libs/files) | `SparkFile`, `SparkImage`, `SparkFileScanStatus`, `[SparkFileOptions(...)]` attribute, `IFileScanner`, `IFileStore`, `IFileUploadHandler` hooks | nothing (an entity library references it) |
| `MintPlayer.Spark.Files` | `AddFiles(o => ...)`, upload/download endpoints, staging, the save-path claim, magic-byte sniffer, RavenDB-attachment `IFileStore`, scan orchestration | Spark, RavenDB |
| `MintPlayer.Spark.Files.ClamAV` | `AddClamAvScanner(o => host/port)`: a clamd `INSTREAM` client | Files.Abstractions |
| `MintPlayer.Spark.Files.Images` | `AddImageProcessing(o => variants)`: decode with a pixel cap, orient, re-encode, variants | Files, the image library chosen in SP5 |
| ng-spark `files` entry point | `withFileRenderers()`: edit/detail/column renderers for `BinaryFile` and `Image` | `@mintplayer/ng-bootstrap/file-upload` |

**Core change (needed by any design): a data-type extension point** (SP1). Two halves:

- In Abstractions, a registry that maps a CLR type to a `dataType` name, consulted by `SparkModelShape.GetDataType` before the
  `AsDetail` fallback.
- In `EntityMapper`, a per-data-type value converter (`ToWire` / `FromWire`) consulted before `Convert.ChangeType`.

While there, fix the swallowed `Convert.ChangeType` failure: a value that can't be converted must be a validation error.
The Files plugin is the extension point's first user.

### 3.2 Upload: two phases, JSON saves stay JSON (recommended, D3)

1. **The user picks a file, and the renderer uploads it immediately.**
   - The request is `POST /spark/files/upload` (multipart, streamed, antiforgery, rate-limited), with the fields
     `objectTypeId`, `attribute`, an optional `id` (an existing object), and the file.
   - The server checks:
     - the user may create or edit that type and the attribute is editable for them;
     - the size cap (enforced as a stream limit, not after buffering);
     - extension allow-list → magic-byte sniff → MIME allow-list;
     - for images, the pixel cap and re-encode;
     - the `IFileUploadHandler.ValidateAsync` hook.
   - It stores the bytes in **staging**: a `SparkFileUploads/` document plus an attachment, with `@expires` (default 24 h), bound to
     the user, type and attribute.
   - It scans inline, or queues the scan (§3.4).
   - It returns `{ token, fileName, length, contentType, scanStatus }`. The renderer shows progress through `HttpClient`'s `reportProgress`.
2. **The PO save carries `{ "uploadToken": "…" }` as the attribute's value.**
   - The save path's Files converter **claims** the token in the same session: same user, type and attribute, not expired, not already
     claimed, and scan status not `Infected`.
   - It moves the attachment onto the entity document and writes the `SparkFile` metadata into the property.
   - An unclaimed upload expires with its staging document.
   - Clearing the value (`null`) deletes the entity's attachment.
   - Any other value (a forged `SparkFile` JSON, another user's token) is refused, and the stored value is copied back. This is the same
     pattern as forged `CreatedBy`.
3. **Why not Vidyano's multipart save?** Multipart saves would change every save request, `SparkService`, the client-operation envelope,
   CSRF tests and AsDetail paths. They would also block the form until the bytes arrive, with no per-file progress. The two-phase design
   keeps the save path JSON, gives real progress, and lets the scan run while the user fills in the rest of the form.

### 3.3 Storage and the stored value

`SparkFile` (stored in the document, also on the wire) holds:

- `FileId` (the attachment name, server-generated and never derived from the client name)
- `FileName` (sanitised: NFC normalisation, control and invalid characters stripped, `Path.GetFileName`, a length cap)
- `ContentType` (sniffed)
- `Length`
- `Sha256`
- `ScanStatus`
- `UploadedAt`
- `UploadedBy` (a user id)

`SparkImage` adds `Width`, `Height` and `Variants` (name → width/height/attachment name).

- **Default `IFileStore` = RavenDB attachments on the entity document.** Deleting the document deletes its attachments. Revisions keep
  references (§5).
- **Seam for Azure/S3:** `IFileStore` (Put/Open/OpenRange/Delete/Move). RavenDB 7.2 remote attachments are an ops-level alternative that
  needs no code (SP3 checks the delete caveat).

### 3.4 Scanning

- `IFileScanner.ScanAsync(Stream, FileScanContext, ct) → Clean | Infected(signature) | Error`.
- Status lifecycle: `Pending → Clean | Infected | ScanFailed`.
- **Only `Clean` is downloadable.** `Infected` attachments are deleted, keeping a tombstone record. `ScanFailed` can be retried.
  The rule is fail-closed.
- **Mode (D6):** `Inline` scans during the upload request (simple; the token comes back already `Clean`). `Deferred` uses a messaging
  queue or subscription that scans `Pending` files. The renderer shows "scanning…" and polls or refetches.
- **No scanner registered:**
  - In Development, every file is `Clean`, and a startup warning says so.
  - **In Production, `AddFiles()` refuses to start** unless the app registers a scanner or sets `o.AllowUnscannedFiles = true`,
    which logs a warning on every start (D5).
- **ClamAV provider:** a clamd TCP `INSTREAM` client.
  - Our own client (about 150 lines) or nClam, depending on SP4.
  - CI tests use a `clamav/clamav` service container and the EICAR test string.

### 3.5 Download

`GET /spark/files/{objectTypeId}/{id}/{attribute}[/{variant}]`:

- **The authorization is the read path's own.** The server loads the PO through the normal read pipeline: row security, attribute
  rights, hidden attributes.
  - If the user cannot see the attribute's value, the file does not exist: 404, or a uniform 401 when anonymous, per the existing denial
    policy.
  - Only `Clean` files are served.
- Headers:
  - `Content-Type` comes from the stored, sniffed type.
  - `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox; default-src 'none'`.
  - `Content-Disposition: attachment; filename*=UTF-8''…`. It is `inline` only for allow-listed raster images and only from an `Image`
    attribute, so a `BinaryFile` that happens to be a PNG still downloads.
- Range support (`enableRangeProcessing` or `GetRange`, SP3). ETag = `Sha256`. Cache is `private`.
- **Optional `Files:ContentOrigin`:** a separate cookie-less host for serving, using a signed short-lived URL. This is optional in v1 (D8).

### 3.6 Images

- Accepted: PNG, JPEG, GIF (first frame for variants) and WebP. **No SVG, BMP or TIFF.**
- The pixel cap is read from the header before decoding (default 40 MP).
- The image is re-encoded, which applies the EXIF orientation and strips metadata (GPS).
- **Variants (opt-in, `Files.Images`):** named sizes, e.g. `thumb` 160 and `medium` 800, are generated on upload and stored as extra
  attachments.
  - The grid uses the `thumb` variant, the detail page uses `medium`, and a click opens the original.
  - Without the Images package, the grid shows a file icon plus the name, never the original.
- Library: chosen in SP5. The leading candidate is SkiaSharp (MIT). **ImageSharp only if the owner accepts its licence for downstream
  users** (D7).

### 3.7 Client

- `@mintplayer/ng-spark/files`, `withFileRenderers()` (the `score-renderers` pattern).
- **The registry needs a `dataType` key** (SP6). Either the registry gains `dataType`-matched registrations, or the model synchronizer
  writes `renderer: "BinaryFile"` for these attributes. The first is cleaner.
- Renderers:
  - **Edit:** `bs-file-upload` (single, `accept` from the attribute's options). It uploads on drop or browse, shows progress, shows the
    file name and size, and offers replace and clear. The image renderer adds a preview, using a local object URL before the save.
    It calls `valueChange({uploadToken})`.
  - **Detail:** the file name, size and a download link. An image shows `medium` (or the original when there are no variants) with a
    click-to-enlarge.
  - **Column:** the name, or a thumbnail.
  - **Scan state:** "scanning…" or "refused".
- The existing `image` string presentation is renamed to `imageUrl` (D1, no backward compatibility in preview). Its templates follow.

---

## 4. Configuration

### 4.1 Registration

```csharp
spark.AddFiles(o =>
{
    o.MaxFileBytes = 10 * 1024 * 1024;     // global ceiling; an attribute may only lower it
    o.StagingLifetime = TimeSpan.FromHours(24);
    o.ScanMode = FileScanMode.Inline;
    // o.AllowUnscannedFiles = true;        // required in Production without a scanner
});
spark.AddClamAvScanner(o => { o.Host = "clamav"; o.Port = 3310; });
spark.AddImageProcessing(o => o.Variants.Add("thumb", 160).Add("medium", 800));
```

All of these are also bound from `Spark:Files` (code wins).

### 4.2 Per attribute

```csharp
[SparkFileOptions(Accept = ".pdf,.docx", MaxBytes = 5_000_000)]
public SparkFile? Contract { get; set; }
```

An `Image` attribute's `Accept` defaults to the raster set in §3.6.

### 4.3 Endpoints

The endpoints are only mapped when `AddFiles()` is called.

### 4.4 Defaults

| Setting | Default |
|---|---|
| global max | 10 MB |
| request body limit on the upload endpoint | max + 64 KB |
| BinaryFile `Accept` | **empty = refuse everything** until declared (D4) |
| rate limit | 30 uploads/min/user |

---

## 5. Risks and security checklist

- **Read-path leaks (#509):** the download must use the read pipeline, never a direct attachment load by id.
  Test: a user without the attribute right gets 404.
- **Token forgery and replay:** a token is bound to user + type + attribute (+ id when editing) and is single-use.
  Test: user B claims user A's token → refused, stored value copied back.
- **Content confusion:** an HTML or SVG file renamed `.png` is refused by the sniffer. A PDF served from a BinaryFile is
  `attachment` + sandbox. Test with polyglot fixtures (real files only, per the repo's real-fixture rule).
- **Decompression bomb:** a header with 50,000×50,000 pixels is refused before decoding.
- **Size DoS:** a stream limit on the endpoint. The IIS limit is documented for hosts behind IIS.
- **Path traversal:** the client file name is never a path or attachment name.
- **GDPR:**
  - EXIF/GPS is stripped from images.
  - A replaced or cleared file still lives in revisions while revisions are kept. Document this, and offer a purge hook (SP8).
  - Remote attachments don't delete remote blobs on document delete (SP3).
- **RQL:** no string splicing. Ids and tokens go through parameters (repo rule).
- **New endpoints → CsrfSurfaceTests + route snapshot, in the same commit.**

---

## 6. Developer hooks

```csharp
public interface IFileUploadHandler     // [Register]-able, many allowed, run in order
{
    // after sniff + size check, before staging; return a refusal message to reject
    ValueTask<FileUploadDecision> ValidateAsync(FileUploadContext ctx, CancellationToken ct);
    // after the save claimed a Clean file (or after a deferred scan made it Clean)
    ValueTask OnFileAcceptedAsync(FileAcceptedContext ctx, CancellationToken ct);
}
```

Existing PO hooks (`OnBeforeSave`, actions) see the `SparkFile` metadata like any other value, and can open the bytes through `IFileStore`.

---

## 7. Decisions (locked with the owner, grill of 2026-11-22)

Each decision's evidence is the owner's answer in the grill (owner preference), together with the findings in §1 that the
question cited.

| # | Question | Decision |
|---|---|---|
| D1 | Data-type names; the `image` presentation | **`BinaryFile` + `Image`. The `image` string presentation is removed.** Its only user, `CodeCoverage.Account.AvatarUrl` (`Account.json:83-84`), becomes `Image` (owner: "for once allow breaking changes"). |
| D1b | Value shapes | **The CLR property type decides.** `SparkFile`/`SparkImage` = stored. `string` = `https:` URL or `data:` URI, read and displayed only, raster rendered, anything else downloaded. No re-upload of URL images (owner). |
| D2 | Core or plugin | **The types and interfaces are in core; the Files plugin implements them.** Libraries can't add data types (owner). |
| D2b | A file attribute without the plugin | **Startup error**, plus a SPARK0xx Warning analyzer following the SPARK039 pattern. |
| D3 | Transport | **Upload first, then the save claims a token, as the only transport.** Every 2sky plugin works the same way (§1.5). The owner first leaned to a multipart save, then chose this, because direct-to-bucket uploads need it. |
| D4 | `BinaryFile` with no `accept` | **Refuse everything.** `*` is refused, magic bytes are required, and an unusual format needs an `ISparkFileSignature`. |
| D5 | Production without a scanner | **Refuse to start** unless `AllowUnscannedFiles`. |
| D6 | Scan mode | **Inline by default**, deferred optional. |
| D6b | Direct-to-bucket | **In this PR** (owner chose B), with the commit step's sniff and scan before a token can be claimed. |
| D7 | Licences | **Core, `Files` and the interface packages: permissive only. Implementation packages may be LGPL** (owner). |
| D7b | Image implementations | **SkiaSharp + NetVips both ship**, with one shared conformance suite (owner: "we just need to ship the proper tests"). |
| D8 | Content origin | The S3-compatible storage host is the cookie-less origin (D13). Proxied stores keep the safe headers, and an optional `ContentOrigin` setting. |
| D9 | Several files per attribute | **Yes:** `List<SparkFile>`/`List<SparkImage>`, `maxCount` defaults to 10. |
| D10 | Adopters | **Fleet demo + CodeCoverage custom profile images (production) + the `AvatarUrl` conversion.** The storage and ClamAV containers deploy on the VPS. |
| D11 | Variants | **Generated at commit**, defaults `thumb` 160 and `medium` 800. The sizes can be changed or switched off per attribute in `fileOptions`. |
| D12 | Stores | The developer chooses: RavenDB, FileSystem, Azure Blob or S3. |
| D13 | This repo's VPS storage | An **S3-compatible container (Garage/SeaweedFS)** on its own volume. **Harmful files must never be able to harm the apps or the other stored files** (owner): no volume mounts in app containers, per-app scoped credentials, private buckets, keys generated by the server. |
| D14 | Authorized downloads | **Check the read path, then redirect** to a short-TTL presigned URL. Revoking a right takes effect only after the TTL. |
| D15 | One token for many files? | **One single-object credential per file, issued in batches.** Read responses embed presigned URLs signed at read time. |
| D16 | Where options live | **The model JSON's `fileOptions`**, a declared, hand-editable property validated at startup. The owner: "the model-json files are NOT generated" (confirmed: `modelHashes.json` fingerprints the entity classes, `ModelHashFile.cs:8`; declared hand-editable fields survive a sync, `MintPlayer.Spark/AGENTS.md:100-106`). |
| Q15 | CodeCoverage avatar | **Two properties:** `AvatarUrl` (forge, read-only) + `CustomAvatar` (`SparkImage?`), displayed as `CustomAvatar ?? AvatarUrl` everywhere. Only the owner uploads; admins can clear. |
| C1 | Container hygiene | **No `sshd`** in any container, with glibc/distroless images preferred. A CI Trivy scan fails on CVE-2026-123158 (\*sshole) and on `openssh-server` (owner). |

---

## 8. Spikes

Run each spike on a scratch branch and throw the code away. Write each verdict, with its evidence, into §9.

| # | Question | Done when |
|---|---|---|
| SP1 | Core `BinaryFile`/`Image` (D2): which sites in `SparkModelShape.GetDataType`, `EntityMapper` (read and save), the JSON converters and the client templates branch on `dataType`? How does `ISparkFileValueHandler` plug into the save path? | A throwaway `SparkFile` property syncs to `dataType: "BinaryFile"` and round-trips through read/save via the handler interface, with a list of the touched call sites. |
| SP2 | A streamed multipart endpoint inside Spark's endpoint generator and antiforgery middleware: stream size limit, `MultipartReader` vs `IFormFile`, how the route snapshot records it | A 5 MB upload with the XSRF header passes, an 11 MB upload is refused before being buffered (memory measured), and an upload without the header gets 400. |
| SP3 | RavenDB: move an attachment from the staging document to the entity in one session (`Attachments.Move`/`Copy`); `@expires` on staging (is expiration enabled in our databases?); `GetRange` for Range; the remote-attachment delete caveat | Each answered with a measured run and RavenDB version. |
| SP4 | ClamAV: our own `INSTREAM` client vs nClam (maintenance, async, streaming). The CI service container on GitHub Actions: start time, image size | EICAR is detected and a clean file passes, locally in Docker and in one CI job. Added time measured. |
| SP5 | Image library on Windows + the Linux container: SkiaSharp vs NetVips (vs ImageSharp for reference). Header-only pixel read, EXIF orientation, re-encode quality and size, native asset size, a bomb file | A table: correctness, ms per image, MB added to publish, licence. Pick one. |
| SP6 | ng-spark registry keyed by `dataType`, vs a synchronizer-written `renderer` | Spike renderer shows in edit, detail and grid for a `BinaryFile` attribute. |
| SP7 | Serving safety in a real browser (playwright_node): an uploaded HTML, SVG and PDF served from the endpoint don't execute script on the app origin; inline image works; download name with Unicode | Screenshots plus console. No script runs. |
| SP8 | Revisions and GDPR: does a cleared file stay readable through revisions? A purge path? What does a revision show when it points to an external object that has been deleted? | Documented behaviour plus a recommendation. |
| SP9 | The S3-compatible server for the VPS: Garage vs SeaweedFS (MinIO's community edition is cut back; verify its status). Presigned PUT with a size condition, per-bucket scoped keys, private buckets, image base (glibc/static, no `sshd`), RAM, backups | Both run locally in Docker against `Files.S3`. A table with the pick. |
| SP10 | Direct-to-bucket: S3 presigned POST/PUT size conditions vs Azure SAS (no size condition), the commit read-back for sniffing, scanning and variants, and how orphaned objects are removed | An over-size direct PUT is refused or caught at commit on both stores (Azurite + the SP9 server), and an unclaimed object is gone after expiry. |
| SP11 | `fileOptions` as a declared, hand-editable model property: is it preserved by `--spark-synchronize-model`, and does it compose across library layers (`--spark-describe model … --layers`)? | A hand-edited `fileOptions` survives two synchronizes, and the startup validation reports a bad value. |

---

## 8b. Tests (all in this PR)

Owner, 2026-11-22: "all necessary tests (unit + e2e using the SparkTestDriver)". Every milestone lands with its tests, written red
first. Per the repo rules, the full suite runs once, on CI, at the end.

### Unit tests: new project `tests/MintPlayer.Spark.Files.Tests`

It falls into CI's catch-all "Other suites". Move it to a named group if it turns out long.

| Area | Cases |
|---|---|
| Sniffer | Each allow-listed type is detected from **real fixture files** (repo rule: no synthetic bytes). HTML, SVG, EXE (MZ) and a polyglot (GIF header + HTML) are detected and refused. An extension/content mismatch is refused. An empty file is refused. |
| File-name sanitiser | Path segments (`..\`, `/etc/passwd`), control characters, RTL override (U+202E), NFC normalisation, reserved Windows names, the length cap, an empty result falling back to `file`. |
| Options | Attribute `MaxBytes` above the global max → startup error. A `BinaryFile` with no `Accept` → diagnostic and refuse-all (D4). `Spark:Files` binding with code overriding config. |
| Startup guard | Production without a scanner → throws. With `AllowUnscannedFiles` → starts and warns. Development → starts and warns. |
| Scanner orchestration | Clean / Infected / Error → status lifecycle. An `Error` is never served (fail closed). |
| ClamAV client | Protocol framing against a fake clamd (`INSTREAM` chunks, `stream: OK`, `FOUND`, size-limit reply). The live EICAR test runs only when `SPARK_CLAMAV_HOST` is set (the CI service container). |
| Images | Header-only pixel-cap refusal (bomb fixture), EXIF orientation applied, GPS stripped after re-encode, variant sizes, an animated GIF yielding a first-frame variant. |
| Core extension point (in `MintPlayer.Spark.Tests`) | `GetDataType` consults the registry. A converter round-trips. An unconvertible value is now a validation error, not swallowed (red against today's `Convert.ChangeType` swallow). |

### Integration (end-to-end over HTTP) with `SparkTestDriver`

These tests host a test app with `AddFiles()` on an embedded RavenDB, through `libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs`,
and drive the real endpoints. They live in `MintPlayer.Spark.Files.Tests`.

| Flow | Cases |
|---|---|
| Upload | 1. A valid file → token + metadata. 2. Over the cap → 413 **before being buffered** (assert the server read at most cap + slack bytes). 3. No XSRF header → 400. 4. Anonymous → 401. 5. No edit right on the type, or a read-only attribute → refused. 6. `IFileUploadHandler.ValidateAsync` refusal → message returned. 7. Rate limit → 429. |
| Claim on save | 1. Create and update with a token → `SparkFile` stored, attachment on the entity document. 2. Another user's token → refused, stored value copied back. 3. An expired, reused or wrong-attribute token → refused. 4. Forged `SparkFile` JSON → refused. 5. `null` → attachment deleted. 6. Replace → old attachment deleted. 7. Unclaimed staging expires (`@expires`). |
| Download | 1. Owner gets the bytes with the safe headers (`nosniff`, CSP sandbox, `attachment`, `filename*`). 2. Image → `inline`. 3. A BinaryFile PNG → `attachment`. 4. A user without the attribute right → 404. 5. Anonymous → uniform 401. 6. Row security hides the object → 404. 7. `Pending`/`Infected` → not served. 8. Range request → 206 with the right slice. 9. ETag → 304. |
| Deferred scan | The worker moves `Pending` to `Clean` and the file becomes downloadable. `Infected` → attachment gone, tombstone kept. |
| Delete | Deleting the entity removes its attachments. The revisions behaviour is asserted as SP8 documents it. |
| AsDetail | A file attribute inside an AsDetail row uploads, claims and downloads (attribute path ~~`detail.index.attr`~~ `Parent.Field`: a new row has no index or key to bind to; see §10 M10). |

### Browser E2E (`tests/MintPlayer.Spark.E2E.Tests`, Fleet host)

- Edit a Car: drop a photo and a PDF → progress shows → save → the detail page shows the photo (`medium` variant) and a download link
  with the right name.
- The grid shows the thumbnail, never the original (assert the requested URL is the variant).
- Clear and replace.
- A refused type shows the server's message in the renderer.
- Scan state: "scanning…" → done (Deferred mode).
- SP7 regression: an uploaded HTML/SVG (refused at upload). Where a PDF is served, nothing executes on the app origin (no console
  errors, no dialog).

### Client unit tests (ng-spark vitest, next to the renderers as today's `renderers/src/*.spec.ts`)

- The edit renderer uploads on drop, reports progress, and emits `valueChange({uploadToken})`.
- Clear emits `null`.
- `accept` is taken from the attribute options.
- The detail and column renderers pick the variant URL.
- The registry resolves by `dataType` (SP6).

### Added by the grill (2026-11-22)

- **Store conformance suite:** one set of `IFileStore` tests run against RavenDB, FileSystem, Azure Blob (Azurite) and S3 (the SP9
  server as a CI service container). Cases:
  - put / open / range / delete;
  - a direct presigned PUT, an over-size PUT, and a presigned read that expires;
  - orphan reconciliation;
  - keys generated by the server (a client name can't escape the prefix).
- **Image conformance suite** (`IImageProcessor`), the same fixtures for SkiaSharp and NetVips:
  - pixel-cap bomb, EXIF orientation, GPS stripped, variant sizes, an animated GIF's first frame;
  - corrupt and truncated input refused;
  - the native-asset startup self-test.
- **Lists:** claims of mixed tokens, removing one item deletes only its object, reordering keeps the objects, over `maxCount` →
  refused.
- **Batching:** a prepare for N files returns N single-object credentials. A credential for object A can't write object B. One
  commit handles N files, and one infected file fails only its own item.
- **Read-embedded URLs:** a query response signs only the files the user may see. A hidden attribute embeds no URL. An expired URL
  gets refreshed in the renderer.
- **String-backed Image/BinaryFile:**
  - an `https:` URL renders;
  - a `data:image/png` URI renders;
  - `data:image/svg+xml` and `data:text/html` are never rendered (download only);
  - an editable string Image refuses `javascript:` and `http:`.
- **`fileOptions` startup validation:** empty `accept`, `*`, an unknown type, a `maxBytes` above the ceiling, a malformed variant,
  and configuration that tries to loosen a limit.
- **Missing plugin:** the startup error, and the analyzer Warning in the source-generator tests.
- **CodeCoverage avatars** (in `CodeCoverage.Tests`):
  - the owner uploads, and another user can't;
  - an admin clears;
  - every view shows the same resolved avatar;
  - a hidden account's avatar returns the uniform 401/404 (existence-oracle tests).
- **Containers:** the CI Trivy scan of the compose images (CVE-2026-123158, `openssh-server`), and the compose health checks.

### Guards updated in the same commits

- `CsrfSurfaceTests`.
- The route snapshot (`SPARK_UPDATE_ROUTE_SNAPSHOT`).
- The layering/translation goldens if translations change (`SPARK_UPDATE_GOLDEN`).
- `tools/test-shard-weights.json` if the new project joins a sharded job.

---

## 9. Spike results and deviations

### SP1: where core branches on `dataType` (read 2026-11-22, on `5433a99dc`) ✅ feasible, no generic extension point needed

**Server: 4 sites.**

1. **Model.** `SparkModelShape.GetDataType` (`Abstractions/Model/SparkModelShape.cs:191-216`). `TranslatedString` is the precedent:
   a class in Abstractions with its own data type, matched *before* the `IsComplexType → AsDetail` fallback (`:209-212`).
   `SparkFile`/`SparkImage` go in the same place.
   - ⚠️ The collection branch (`:195-197`) classifies `List<SparkImage>` by its element type, and `IsComplexType(SparkImage)` would
     make it `AsDetail`. The file types must be tested **before** the collection branch's `IsComplexType`, otherwise lists silently
     become AsDetail.
   - The hash records the derived data type (`:84`), so the new types move the hash exactly once, as intended.
2. **String-backed `Image`/`BinaryFile`.** These ride the existing presentation mechanism: `SparkStringPresentations`
   (`Abstractions/SparkStringPresentations.cs:22-44`), preserved by `ModelSynchronizer.cs:997`.
   - `Image` replaces `image`, and `BinaryFile` is added. `MultiLineString` and `url` stay.
   - `Preserves` compares case-insensitively, so an existing `"image"` would survive as `image`. The synchronizer must
     **normalise `image` → `Image`** on string properties. That is the migration for `CodeCoverage.Account.AvatarUrl`.
3. **Save.** `EntityMapper.WritePropertyAsync` (`Services/EntityMapper.cs:701-722`) dispatches AsDetail, then `TranslatedString`,
   then arrays. **The `ISparkFileValueHandler` dispatch goes right after AsDetail**, before `TranslatedString` and before the
   `IsArray` branch (`:728`), because `List<SparkImage>` would otherwise take the array coercion path.
   - The claim needs the async path with a session. The sync `PopulateObjectValues` (`:618-644`) runs with `session: null`, so a
     file value reaching it must fail loudly, never be dropped.
   - The schema write gate `IsWritableBySchema` (`:676-684`) runs first, so a read-only file attribute is already refused.
4. **Read: one choke point, the "boundary net"** (`Services/SparkPresentation.cs:8-62`, installed in `SparkMiddleware.cs:102-105`).
   - It is a `JsonTypeInfo` modifier on the HTTP JSON options, and it fires for **every** outbound persistent object: envelopes,
     query rows, AsDetail rows, retry prompts, client operations, custom-action results and Live WebSocket frames
     (`Live/LiveConnection.cs:69`).
   - Attributes the caller may not read have already been **removed** by `IAttributeRightsEnforcement.PresentAsync`
     (`Services/AttributeRightsEnforcement.cs:83-100`: "Removed, not blanked"), or the net prunes them itself.
   - **Decision from the spike: presigned URLs are signed in a modifier for `SparkFile`/`SparkImage` installed beside the net.**
     It resolves an `ISparkFileUrlSigner` from `HttpContext.RequestServices`, as `Check` does (`:71`).
     - A value only reaches serialization if its attribute survived presentation, so **no code path can sign a URL for a hidden
       attribute**, including ones added later.
     - With no request (jobs, tests) there is no URL.
     - This replaces "sign in each read path" (D15), which would have needed 6+ call sites (Refresh, Live, QueryExecutor,
       SaveResponsePresenter, SparkCatalogue, SparkSelectionResolver), each one a chance to forget.
   - `ConvertValueForWire` (`EntityMapper.cs:579-592`) passes a `SparkFile` through unchanged, so the value is still typed when it
     reaches the serializer. Index projections must declare `SparkFile`-typed properties for the same to hold in query rows. That
     goes into the M1 tests.
- **The save keeps the stored value when the client echoes one back.** The client posts the object back with the signed URL
  inside the `SparkFile`. The handler treats a value carrying a known `fileId` as "keep" (for lists: keep, at this position), a value
  carrying `uploadToken` as new, and anything else as refused, with the stored value copied back. The echoed URL and metadata are
  ignored.
- `Convert.ChangeType` swallow: `SetPropertyValue`'s fallback (`EntityMapper.cs:1350-1414`) stays in M1 scope as planned.

**Client: three templates plus the text pipes.**

- `po-form/src/spark-po-form.component.html` (edit), `po-detail/src/spark-po-presenter.component.html:162` (detail) and
  `grid/src/spark-grid-cell.component.html:33` (cell) hold the `'image'` branches. `pipes/src/input-type.pipe.ts:19` maps `image`
  to `url`.
- **SP6 shape (taken from this spike):** the registry gains registrations matched by `dataType`. The three templates already ask
  the registry before their `dataType` branches (`spark-po-form.component.ts:684-701` resolves by `attr.renderer`), so adding a
  `dataType` fallback in that one resolver covers all three.
- The text pipes (`attribute-value.pipe.ts`, `query-cell.pipe.ts`, `array-value.pipe.ts`) and `po-edit/src/conflict-merge.ts` /
  `history` / `contributions/src/spark-line-diff.component.ts` compare or print values. A `SparkFile` object must print as its
  file name and compare by `fileId`, not as `[object Object]` or by reference (an M9 checklist item).

### SP11: does `fileOptions` survive a synchronize? (read 2026-11-22) ✅ yes; and it must be structural

- **It survives.** On an existing attribute, `ModelSynchronizer` mutates the stored object in place and assigns only derived fields
  (`Services/ModelSynchronizer.cs:984-1060`). The hand-authored flags `CanSort`/`CanFilter`/`CanListDistincts` are deliberately
  left unassigned and so survive (comment at `:1031-1038`). A declared `FileOptions` property on the attribute model type, which
  the synchronizer never assigns, survives the same way. That matches `MintPlayer.Spark/AGENTS.md:100-106`: declared fields
  survive, undeclared ones are destroyed.
- **It must be hashed.** `ModelFileShape` (`Abstractions/Model/ModelFileShape.cs:18-43`) hashes the structural fields of the model
  files, and its rule is "**every write gate is structural**". Validation rules are hashed because "silently dropping a rule is an
  attack, not a restyling" (`:30`).
  - `fileOptions` (`accept`, `maxBytes`, `maxCount`) is a write gate, so it joins `StructuralAttributeFields`.
  - Consequence: hand-editing `fileOptions` requires re-running `--spark-synchronize-model`, which rewrites `modelHashes.json`.
    Without that, the app refuses to start outside Development and warns in Development. **A limit loosened on a server by
    editing the file is caught.**
  - `variants` and `delivery` don't gate writes. They are hashed anyway, as part of the one `fileOptions` object, to keep a single
    rule. That is a deliberate deviation from "presentational stays unhashed", recorded here.
- The layer composition (`--spark-describe model … --layers`) still needs a run once M1 exists. Recorded as an M1 test: a library
  ships `fileOptions`, and the app narrows it.

### SP2: streamed multipart inside Spark's endpoints (read 2026-11-22) ✅ shape confirmed; the measurement moves into M3's tests

- **Raw endpoint.** Spark already has a raw endpoint form: `IEndpoint` with `HandleAsync(HttpContext)` (`Endpoints/Live/LiveSocket.cs:23-47`),
  next to the bound `IPostEndpoint<TReq>` used by `/update` (`Endpoints/PersistentObject/Update.cs:14-32`). The upload and commit
  endpoints use `IEndpoint` and read the body with `MultipartReader`, so no `IFormFile` buffering happens.
- **Metadata.** `IEndpointBase.Configure` adds `RequireAntiforgeryTokenAttribute(true)` (as `Update.cs:20` does) and a
  `RequestSizeLimitAttribute(maxFileBytes × maxCount + slack)`. The endpoint layer honours that metadata before the body is read,
  and `RouteTableSnapshot` already records it (`libs/testing/MintPlayer.Spark.Testing/RouteTableSnapshot.cs:178`), so the snapshot
  shows each upload route's limit.
- **Antiforgery.** `SparkAntiforgeryMiddleware` validates the `X-XSRF-TOKEN` header and never reads the body, so it works the
  same for multipart.
- **Deviation:** the spike's three measurements stay as written, but they **become M3's red→green `SparkTestDriver` tests** rather
  than a throwaway run. The three are: 5 MB passes; 11 MB is refused before being buffered, with the bytes read asserted; no XSRF
  header gives 400. The tests prove the same thing, and are kept.

### SP3: RavenDB expiration and attachments (read 2026-11-22) ✅ expiration solved; move and Range go to M5's store conformance suite

- **Expiration.** The plugin calls `builder.Registry.RequireDocumentExpiration("Files")`, the same way as
  `SparkMailManagerExtensions.cs:99`, `SparkAppsExtensions.cs:113`, `SparkIdentityProviderExtensions.cs:189` and `SparkMcpExtensions.cs:94`.
  `UseSpark()` turns expiration on once, with the shared frequency.
  - Staging documents carry `@expires`. For the RavenDB store, the staged attachment goes with its document.
  - **For the external stores, an expiring staging document does not delete its object.** The orphan reconcile sweep (M5) deletes
    staged objects whose staging document has gone. That is the BigFileUploader gap (§1.5), closed explicitly.
- **Moving the attachment from staging to the entity document in one session, and serving a Range through the session API's
  `GetRange`.** Both are checked by the store conformance suite in M5 (put/open/range/move per store), against the RavenDB
  version the test driver embeds. They are not a separate throwaway run.

### SP9: desk research on the S3-compatible server (2026-11-22). The local run is still open.

- **MinIO is out.** Its community edition went into maintenance mode in December 2025 and has since been archived. Sources
  disagree on the date (April or November 2026). Existing installs get no more security fixes
  ([ayedo](https://ayedo.de/en/posts/minio-im-maintenance-mode/), [TT Lab timeline](https://ttlab.hopto.org/blog/2026-07-17-minio-archived-garage-seaweedfs-ceph-rgw?lang=en)).
- **SeaweedFS** (Apache-2.0) is rated the more mature option, and is used by Kubeflow Pipelines as a default backend
  ([bex.co](https://bex.co/blog/2026/07/09/minio-death-garage-seaweedfs-rustfs)).
- **Garage** (AGPL) has a deliberately narrower S3 API, with **no bucket lifecycle policies and no object versioning** in stable
  ([elest.io](https://blog.elest.io/rustfs-vs-seaweedfs-vs-garage-which-minio-alternative-should-you-pick/)). We don't need either:
  the reconcile sweep replaces lifecycle rules, and revisions live in RavenDB.
- **Unknown, and decisive for D6b and SP10:** whether presigned PUT size conditions (`content-length-range` in a presigned POST
  policy) work on Garage and SeaweedFS. No source covers it. This needs the local Docker run.

### SP9 + SP10 (S3 half): local run, 2026-11-22 ✅ **Garage**, and direct uploads use a presigned **POST policy**

**Setup:** `dxflrs/garage:v1.1.0` and `chrislusf/seaweedfs:latest` in Docker. Each had two buckets (`appa`, `appb`) and one key per
bucket. The client was a throwaway .NET script using AWSSDK.S3 3.7, with the SigV4 POST policy built by hand.

| Test | SeaweedFS | Garage |
|---|---|---|
| T1 own key, own bucket: put/get | ✅ | ✅ after `UseChunkEncoding = false` (otherwise "Invalid payload signature") |
| T2 presigned PUT 1 KB | ✅ 200 | ✅ 200 |
| **T3 presigned PUT 2 MB: nothing can cap a PUT** | 200 (accepted) | 200 (accepted) |
| **T4a POST policy `content-length-range` ≤ 1 MB, 1 KB body** | ✅ 204 | ✅ 204, once the form carries a `bucket` field ("Key 'bucket' is required in policy") |
| **T4b same policy, 2 MB body** | ✅ 400 | ✅ 400 "File size does not match policy" |
| T5a/b app A's key writes or reads app B's bucket | ✅ 403 / 403 | ✅ 403 / 403 |
| T6a/b anonymous list / get | ✅ 403 / 403 | ✅ 403 / 403 |
| T7 presigned GET after expiry | ✅ 403 | ✅ refused (400, not 403) |
| Idle memory | 135 MiB | **6 MiB** |
| Image | 1.08 GB, **Alpine** (no `sshd`) | **33 MB, static binary, no shell** |

**Verdicts:**

- **D13 = Garage.** It's 20× lighter in memory, and the static image has no OS to patch, no shell and no musl `sshd`. That meets C1
  by construction. It does everything we need, and it lacks only lifecycle and versioning, which we don't use (SP9 desk). AGPL
  binds changes to Garage itself, not our code, which talks to it over HTTP. SeaweedFS also passes and stays the documented
  alternative.
- **SP10, S3: direct uploads use a presigned POST policy, not a presigned PUT.** T3 shows that a presigned PUT can't cap size on
  either server, while the POST policy's `content-length-range` is enforced by both (T4b). The prepare step therefore returns a
  POST policy (url + fields) for each file. The commit step still checks the object's length (defence in depth).
- **`Files.S3` defaults, from what Garage needed:**
  - turn off chunked payload signing;
  - always send the `bucket` field in the policy form;
  - treat any refusal of an expired URL as expired, whether 400 or 403.

  The store conformance suite runs against Garage in CI, so these stay covered.
- **Closed for SP10 in M5** (§10 M5):
  - Azure: a SAS has no size condition, so the commit-time length check is the only cap there. Against Azurite, a direct PUT
    through the SAS lands (`A_direct_upload_lands_under_its_key`), and the store reports that it can't cap.
  - A refused over-size POST leaves no object behind on Garage (`A_direct_upload_above_the_cap_is_refused_where_the_store_can_cap`
    reads the key back as null).

### SP4: ClamAV over clamd `INSTREAM`, local run 2026-11-22 ✅ our own client; the shipped `clamd.conf` must close two fail-open defaults

**Setup:** `clamav/clamav-debian:stable` (ClamAV 1.5.5, signatures 28188) in Docker, port 3310. The client was a throwaway .NET
script of about 50 lines (`zINSTREAM\0`, 4-byte big-endian chunk lengths, a zero-length terminator) plus `clamdscan`/`clamscan`
inside the container.

| Test | Result |
|---|---|
| `PING` / `VERSION` | `PONG` / `ClamAV 1.5.5/28188` |
| EICAR (68 bytes) | `stream: Eicar-Test-Signature FOUND`, 6 ms |
| 10 MB random bytes | `stream: OK`, 1.3 s |
| **120 MB stream, over the default `StreamMaxLength` of 25M** | **no reply at all**: clamd logs "INSTREAM: Size limit reached" and drops the connection, so the client sees a reset (10053), not a verdict |
| zip holding EICAR and a 150 MB file | `Eicar-Test-Signature FOUND` (archives are unpacked) |
| **zip whose inner file is over `MaxScanSize`/`MaxFileSize`, default settings** | **`OK`**: the over-limit part is skipped silently. This is fail-open. |
| same zip with `AlertExceedsMax yes` | `Heuristics.Limits.Exceeded.MaxScanSize FOUND` |
| Memory with signatures loaded | **1.05 GiB** |
| First start (signature download, then clamd up) | about 70 s; `socket found, clamd started` is the readiness line |
| Image | 555 MB, Debian 13, glibc 2.41, **no `sshd`** (C1 holds) |

**Verdicts:**

- **D9 stands (fail-closed, inline).** `Files.ClamAV` maps the replies like this:
  - `OK` is clean.
  - Any `FOUND`, including `Heuristics.Limits.Exceeded.*`, is infected and refused.
  - Anything else is a scanner failure, which refuses the upload: `ERROR`, an unparsable reply, a connection reset, or a timeout.
  - A reset in the middle of a stream is a failure, never a pass.
- **The `clamd.conf` that the compose files and docs ship sets `AlertExceedsMax yes`.** Without it, a small archive can hide an
  over-limit payload behind an `OK`. It also sets `StreamMaxLength`, `MaxFileSize` and `MaxScanSize` to at least the largest
  `fileOptions.maxSize` the app allows. **Superseded in part (M7):** `AlertExceedsMax` alone is not enough on 1.5.5. A member
  over `MaxFileSize` is trimmed without an alert, so the conf must keep `MaxFileSize ≥ MaxScanSize` (§10 M7, measured).
- **The adapter enforces its own size cap before streaming.** It refuses a file larger than its configured `MaxStreamLength`
  without sending it, so the refusal is a clear error rather than a socket reset.
- **The health check scans EICAR, not just `PING`.** A clamd with no signatures, or with broken ones, still answers `PONG`. The
  `IFileScanner` health probe streams the EICAR string and expects `FOUND`. The string is assembled at runtime, so no repository
  file carries the signature, and no scanner ever flags our own build output.
- **Our own `INSTREAM` client, no package.** It's about 60 lines, and we need exact control over the reset/timeout ⇒ refuse
  semantics. It also keeps `Files.ClamAV` free of a dependency.
- **Deployment cost: about 1 GiB of RAM**, so scanning stays opt-in. ~~CodeCoverage (M11) adds clamd only if the VPS has the
  headroom. Measure the VPS free memory before M11's compose change. Otherwise avatars ship with the image re-encode (M8) as the
  only defence, and the doc says so.~~ **Superseded (owner, 2026-11-22):** CodeCoverage adds clamd in M11, with
  `ConcurrentDatabaseReload no` and a `mem_limit`, after PR #703 freed memory on the VPS (see §0 item 13).
- **Tests for M7:** fake-scanner unit tests cover every reply class above. One integration test runs the real clamd container in
  CI and covers EICAR, clean, the over-limit stream reset, and the `AlertExceedsMax` zip.

### SP6: client renderers keyed by `dataType` (read 2026-11-22, on `f4bf9d56f`) ✅ one shared resolver, plus a `dataType` field on the registration

**Found:** the renderer registry (`renderers/src/spark-attribute-renderer-registry.ts`) is keyed only by `name`, matched against
`attr.renderer`. There are **9 lookup sites in 5 files**, and each repeats `registry.find(r => r.name === x.renderer)`:

- `po-form/src/spark-po-form.component.ts:686`, `:706` and `:726`
- `po-detail/src/spark-po-presenter.component.ts:163`, `:168` and `:194`
- `grid/src/spark-grid-renderers.ts:34` and `:40`
- `dashboards/src/spark-dashboard.component.ts:125`

An attribute without a `renderer` falls through to the built-in input or display. For a file attribute, that fall-through would be
a text box holding a JSON value.

**Verdict:**

- **`SparkAttributeRendererRegistration` gets an optional `dataType`.** This makes the registration the default renderer for every
  attribute of that data type that doesn't name its own.
- **All 9 sites go through one exported `resolveAttributeRenderer(registry, attr)`.** It tries `attr.renderer` by name first,
  then the first registration whose `dataType` equals `attr.dataType`. An app's registration still wins over a shipped one,
  because of the existing set order.
- **`@mintplayer/ng-spark/files` (new entry point) exports `withFileAttributes()`.** It registers the upload, detail and column
  components for `BinaryFile` and `Image` through this field, so core ng-spark knows no file components. That mirrors D1 on the
  client.
- **Fail-safe without the plugin.** When the form meets a `BinaryFile`/`Image` attribute and finds no renderer for it, it shows a
  read-only "file attributes are not enabled" notice, never the default text input. The grid and the detail page show the file
  name, through the text/compare pipe mapping. The `'image'` string branches in `spark-grid-cell.component.html:33` and
  `spark-po-presenter.component.html:162` are removed, because `Image` is now a data type with its own renderer.
- ~~**The edit renderer contract is unchanged** (`value`, `attribute`, `options`, `valueChange`).~~ Superseded in M9: it gains the
  optional `objectTypeId` and `objectId` (§10 M9). The upload component uploads
  through the Files endpoint itself and hands the form a claim value through `valueChange`, so the form's save path needs no file
  knowledge.
- **Tests (M9):** resolver unit tests:
  - name beats `dataType`;
  - an app beats a shipped renderer;
  - no match returns `null`;
  - a file `dataType` with no plugin shows the notice, never an `<input type="text">`.

### SP5: SkiaSharp vs NetVips, Windows and Linux, local run 2026-11-22 ✅ both pass; D12 stands (SkiaSharp default, NetVips separate)

**Setup:** a throwaway .NET 11 script using SkiaSharp 3.119.0 (+ `SkiaSharp.NativeAssets.Linux.NoDependencies`) and NetVips 3 with
libvips 8.18.7 (`NetVips.Native`). It ran on Windows 11 and in `mcr.microsoft.com/dotnet/sdk:11.0` (Ubuntu 26.04). There were three
fixtures, all built with libvips:

- a 4000×3000 JPEG with EXIF `Orientation=6` and an `Artist` tag;
- a 30000×30000 PNG decompression bomb (900 Mpx, 107 KB on disk);
- a GIF with `<script>` HTML appended.

| Test | Skia Win | Skia Linux | vips Win | vips Linux |
|---|---|---|---|---|
| Bomb: dimensions from the header only, refused before decode at a 40 Mpx cap | ✅ 70 ms | ✅ 26 ms | ✅ 7 ms | ✅ 13 ms |
| EXIF orientation honoured (portrait 3000×4000) | ✅, applied **by hand** from `SKCodec.EncodedOrigin` | ✅ | ✅ `thumbnail` autorotates | ✅ |
| Variant 1600 / 400 / 96 (WebP q80) | 165 / 17 / 1 ms | 275 / 22 / 5 ms | 238 / 34 / 22 ms | 247 / 123 / 99 ms |
| `Artist` tag in any output | gone | gone | gone (`keep: None`) | gone |
| Polyglot GIF re-encoded: `<script>` in the output | gone | gone | gone | gone |
| Peak working set (whole run) | 170 MiB | 227 MiB | (same process) | (same process) |

**Verdicts:**

- **D12 stands.**
  - `Files.Images.SkiaSharp` (MIT) is the default.
  - `Files.Images.NetVips` is the separate LGPL package behind the same `IImageProcessor` interface.
  - Both pass every safety test on both OSes, so the choice is licence and footprint, not safety.
- **Every processor reads the header before decoding.**
  - `SKCodec.Create(...).Info`, or `Image.NewFromFile(access: Sequential)` for vips.
  - Pixels over `fileOptions.maxPixels` are refused. The default is 40 Mpx.
  - This is the decompression-bomb guard, and it costs milliseconds.
- **Skia doesn't apply EXIF orientation by itself.** The Skia processor reads `EncodedOrigin` and rotates before resizing, for all
  8 origins. The unit tests cover each origin with a fixture, generated at test time with no personal data.
- **Re-encoding is the sanitiser.**
  - An image is never stored as uploaded. It's decoded and re-encoded, which drops EXIF/GPS and any appended payload.
  - ~~The original bytes are kept only when the app sets `fileOptions.keepOriginal`, and are then served as an attachment
    download, never inline (SP7).~~ **Superseded by M8 (§10):** `keepOriginal` is not shipped. An Image is always stored
    re-encoded; an app that must keep the uploaded bytes declares a `BinaryFile`.
- **Linux needs `SkiaSharp.NativeAssets.Linux.NoDependencies`.** The aspnet images have no fontconfig, and we don't draw text. The
  package reference goes in `Files.Images.SkiaSharp`.
- **Native packages in a container: none (measured 2026-11-22, owner asked).** The spike was published (`dotnet publish -r
  linux-x64` and `-r linux-musl-x64`, framework-dependent) and run in the runtime images an app actually deploys. `ldd` reported
  no missing library in any of them, and every test above passed:

  | Base image | OS | `apt`/`apk` packages needed | Result |
  |---|---|---|---|
  | `mcr.microsoft.com/dotnet/aspnet:11.0` | Ubuntu 26.04, glibc | none | ✅ |
  | `mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled` | Ubuntu 26.04, distroless, no shell | none | ✅ |
  | `mcr.microsoft.com/dotnet/aspnet:11.0-alpine` | Alpine 3.24, musl | none (publish for `linux-musl-x64`) | ✅ |

  - Both libraries ship their natives in NuGet `runtimes/<rid>/native`: `libSkiaSharp.so` from
    `SkiaSharp.NativeAssets.Linux.NoDependencies`, and `libvips.so.42` from `NetVips.Native`.
  - What the docs must say is about **packages, not OS packages**: the plain `SkiaSharp` package has no Linux native, and fails
    with `DllNotFoundException: libSkiaSharp`. `Files.Images.SkiaSharp` therefore references the `NoDependencies` assets itself,
    and the app adds nothing.
  - A RID-specific publish must match the base image: `linux-musl-x64` for Alpine. A portable publish (no `-r`) carries every RID.
  - Alpine works, but C1 (\*sshole) still points this repo's own images at glibc. The guide says Alpine is supported, and that an
    Alpine image must never install `openssh-server`.
- **Variants are made from the decoded, oriented bitmap once,** not by re-opening the file for each size. vips' per-variant
  `thumbnail` re-opens the file, which is why its Linux 400/96 runs are slow. ~~Its processor uses `thumbnail_image` on the loaded
  image.~~ **Superseded by M8 (§10):** the shipped NetVips processor uses `Resize` on the loaded image, so both processors
  produce the sizes `ImageProcessing.Fit` computes.

### SP8: revisions and erasure, local run 2026-11-22 against RavenDB 7.2.6 ✅ revisions keep a removed attachment; erasure must delete revisions too

**Setup:** a throwaway database with revisions on `Cars`:

1. Store `cars/1` with attachment `photo.jpg`.
2. Delete the attachment (the user replaces or erases the photo).
3. Read the revisions.
4. Run `DeleteRevisionsOperation("cars/1")`.

**Results:**

- After step 2 the current document has no attachment, but **the revision from step 1 still serves the photo's bytes**
  (`Attachments.GetRevision`).
- `DeleteRevisionsOperation` removed all 3 revisions. The database then held **0 attachments**, so the bytes were really gone.

**Verdicts:**

- **History and file storage interact, and this is a GDPR hazard for the RavenDB attachment store.** Replacing or erasing a file
  doesn't erase it while a revision references it. The default is 30 days (`Spark:History:Revisions`).
- **The value in a revision is only a reference**: the stored-file id, name, type and length. External stores keep no copy per
  revision, so an external blob is erased when the store deletes it. The old revision then refers to nothing, and downloading a
  file from history returns 404 by design.
- **`IFileStore.EraseAsync(owner, attribute)` is the explicit erasure hook.** For the RavenDB store it deletes the attachment and
  then the owning document's revisions that reference it (`DeleteRevisionsOperation`). For external stores it deletes the blob.
  Spark Files never calls it implicitly on a plain replace, because history keeps working within the retention window. Apps call
  it from their erasure flow, and the docs say so next to the History package's retention.
- **History never serves file bytes.** The history UI shows the file name and size of an old revision, never a download link, so
  the retention window can't widen who can fetch a file.
- **Tests (M5):** the conformance suite checks that a replaced attachment is still in the revision, and that `EraseAsync` leaves
  0 attachments and 0 referencing revisions.

## 10. Implementation decisions and deviations

### M1 (core), 2026-11-22

- **`SparkFile`/`SparkImage` live in `MintPlayer.Spark.Model`, not Abstractions.** That's where `TranslatedString` lives
  (namespace `MintPlayer.Spark.Abstractions`), and it's the package an entity library references without ASP.NET Core. §0.1 said
  Abstractions; the namespace is the same, so entity code doesn't notice.
- **`SparkImage : SparkFile`.** `GetFileDataType` tests Image first. One handler, store and signer path covers both types.
- **No `url` or `uploadToken` member on the stored type.** The read-side URL is added by a serializer modifier (M6), so it can never
  be persisted. The claim token is read from the posted JSON by the handler (M4). The document holds only server-written fields.
- **The create path is now async** (`IEntityMapper.ToEntityAsync`, used by `DefaultPersistentObjectActions.MapAsync`). Creates
  used the synchronous `ToEntity`, which has no session, so a claim on a create could not have worked.
  - `ToEntity` stays, as a probe: the natural-id collision check in `DatabaseAccess`. It leaves file attributes unset.
  - The public synchronous `PopulateObjectValues` throws on a file attribute rather than dropping it.
- ~~**Files inside AsDetail rows are refused at startup in v1** (`SparkFileAttributeStartupCheck`). A claim is bound to type + id +
  attribute, and a row has no id of its own. Reference a separate entity instead. Lifting this is a later issue, not this PR.~~
  **Superseded (owner, 2026-11-22: "Build it in #702"):** M10 supports one AsDetail level, binding a claim to the document and
  `Parent.Field`. Only files deeper than one level are still refused at startup. See §10 M10.
- **D2b's runtime half is in M1.** `UseSpark()` refuses a stored-file attribute without `ISparkFileValueHandler`, naming
  `Type.Attribute` and `spark.AddFiles()`. A string Image (a URL) needs no plugin. M2 adds the build-time analyzer.
- **The core validates the shape of `fileOptions`; the plugin validates its meaning.**
  - Core (`SparkFileOptions.ShapeErrors`, at startup) checks for wildcards, an entry that is neither a MIME type nor an
    extension, SVG on an Image, non-positive sizes, `maxCount` on a single file, variants on a BinaryFile, and variant names and
    edges.
  - The plugin (M3) checks whether each accept entry has a magic-byte signature, and the global ceiling, because the signature
    registry lives there.
- **`fileOptions.delivery`** is `Redirect | Proxy`, written as a name in the model file.
- **The swallowed conversion.** The scalar `Convert.ChangeType` failure had already become a validation error before #700
  (`SetPropertyValue`'s `FormatException`/`InvalidCastException` catch). What was still swallowed was a JSON **object** that the
  property's complex type couldn't be read from. It is now a `SparkValidationException` naming the attribute
  (`FileAttributeCoreTests.An_object_the_property_cannot_be_read_from_…`).
- **The `image` → `Image` migration is the synchronizer's** (`SparkStringPresentations.Canonical`).
  - CodeCoverage's `Account.json` still says `image` until M11's synchronize, which adds `CustomAvatar` in the same run.
  - Until then the case-insensitive `Preserves` keeps it working, and the client handles both spellings until M9 removes the old
    branch.

### M3 (`MintPlayer.Spark.Files`: the upload steps), 2026-11-22

- **No `Files.Abstractions` package.** `IFileStore`, `IFileUploadHandler` and `ISparkFileSignature` live in
  `MintPlayer.Spark.Files`. Every store and scanner package references Files anyway, and core needs only `ISparkFileValueHandler`,
  which is already in `Spark.Abstractions` (M1).
- **The Files tests live in `MintPlayer.Spark.Tests/Files/`**, not in a new test project. They need `SparkTestDriver`, the shared
  security helpers and the `OrganizationsTestHost` XSRF minting, which are all there already. A new project would also join CI's
  catch-all "Other suites" job.
- **`InternalsVisibleTo MintPlayer.Spark.Files` on core**, for `SparkTypeResolver`, which finds the CLR property behind an
  attribute. This gives the same CS0436 warnings as the Search package's existing IVT.
- **Office formats are recognised as the ZIP container plus their extension.** The first version also required
  `[Content_Types].xml` as the first entry. python-docx writes a different entry first (fixture `letter.docx`), so that check
  refused real files. An Office file is therefore only as trustworthy as any ZIP: it is never rendered inline (D8).
- *(Superseded in M5: Azure's direct upload is a SAS `PUT`, so a slot now carries `method` and `headers` too.)*
  **One upload request shape for every store:** a multipart POST of `token` followed by `file`. A direct store's slot carries its
  own URL and policy fields (S3 POST policy, SP10). The Spark store's slot URL is `/spark/files/upload`. The client has one code
  path either way.
- **The RavenDB store spools a request body to a temporary file.** `PutAttachmentOperation` refuses a non-seekable stream
  ("we might failover to a different node"). The spool is a `DeleteOnClose` file, never memory. It is bounded by
  `LengthLimitedStream`, so an over-cap body still stops at the cap
  (`An_upload_over_the_slot_cap_is_413_and_read_no_further_than_the_cap`).
- **A refused file's bytes are deleted after the commit's save, not before.** The commit session loaded the staging document.
  Deleting the attachment first and then saving left the attachment in place: the six refusal cases of
  `A_refused_file_is_reported_at_commit_and_its_bytes_deleted` were red, finding 1 attachment where 0 was expected. If a delete
  fails after the save, the document is already Refused and its expiry removes the bytes.
- **Fixed in M1's code: `UseSpark()` resolved the scoped `ISparkFileValueHandler` from the root provider** to see whether Files is
  on. Under Development scope validation that threw at startup in every app calling `AddFiles()`. The route-snapshot host, which
  runs as Development, found it. It now asks `IServiceProviderIsService`. The Files test host also runs as Development now, so a
  scoped service resolved from the root fails there too.
- **Services register through `[Register]`.** `[Register(typeof(FileUploadService), Scoped)]`, naming the class itself as the
  service type, produced no registration and no diagnostic. A class registered as itself takes `[Register(ServiceLifetime.Scoped)]`.
  The cause is in MintPlayer.Dotnet.Tools: `ServiceRegistrationsGenerator.cs:176-179` accepts a non-generic service type only when it
  is one of the class's interfaces or base types, and `BaseTypes()` starts at `type.BaseType`. The lifetime-only overload works.

### M4 (the save-path claim), 2026-11-22

- **A second phase on core's handler: `ISparkFileValueHandler.OnStoredAsync`**, which `DatabaseAccess` calls right after
  `StoreAsync` and before the commit.
  - A created entity has no id until it is stored, and that is after the before-save interceptors (`SaveContext.Id` is null there).
    The bytes, though, belong on the entity's own document.
  - `WriteAsync` decides (claim, keep, clear) and sets the value. `OnStoredAsync` hands the bytes side to the store.
  - A scoped `FileClaimLedger` carries the decisions between the two phases, keyed by entity instance.
- **RavenDB: the attachment moves inside the save's own batch.** `RavenAttachmentFileStore.AttachAsync` defers
  `Attachments.Copy(staging, "original", entity, fileId)` into the request session, and `DetachAsync` defers `Attachments.Delete`. The
  file therefore appears and disappears exactly when the entity commits, and a refused save leaves both untouched
  (`A_refused_save_claims_nothing`). The staged original stays on its upload document and expires with it.
- **`IFileStore` gained `EntityKey`, `AttachAsync` and `DetachAsync`** (`SparkFileTransfer`). *(Superseded in M5: `AttachAsync`
  and `DetachAsync` moved to `ISessionFileStore`, which only the RavenDB store implements. The external stores are driven by
  Files around the commit instead.)*
- **Single use holds against concurrent saves.** The claim stores the staging document with the change vector this save read
  (`StoreAsync(upload, version, id)`), so a second save claiming the same token fails at commit. The sequential case is
  `A_token_is_claimed_once`.
- **Every claim refusal is the same 400** ("The upload for 'X' can't be used. Upload the file again."), whether the token is unknown,
  another user's, for another type, attribute or object, uncommitted, not Clean, expired or already claimed. Which check failed
  stays on the server.
- **A keep takes nothing from the echoed value.** Only `fileId` is read, and it must be one the entity holds, each once. The client's
  `fileName` and `url` are ignored (`Echoing_the_stored_value_keeps_the_file`). A `fileId` the entity doesn't hold is refused
  (`A_forged_file_value_is_refused`).
- **The stored `FileId` is a fresh server GUID,** not the upload token, so a file id never reveals a token.
- **`OnFileAcceptedAsync` runs from an `IAfterSave` interceptor** (`FileAcceptedInterceptor`, renamed `FileStoreInterceptor` in
  M5), after the commit. A failing handler is
  logged and never fails the committed save. A deferred scan's "made Clean later" path arrives with M7.
- **Deleting an entity deletes its files** on the RavenDB store: attachments go with their document
  (`Deleting_the_entity_deletes_its_files`). The external stores need an after-delete step, which M5 adds with them.
- **Image variants are not moved yet.** The staging document has no variants until M8, which moves them in `AttachAsync`.

### M5 (the stores), 2026-11-22

- **Two kinds of store.** The RavenDB store joins the save's unit of work (`ISessionFileStore`: `AttachAsync`/`DetachAsync`,
  deferred into the session, as in M4). Every other store is an `ObjectFileStore`, which can't commit with the entity. For those,
  Files drives the store around the commit:
  - **Before the commit** (`SparkFileValueHandler.OnStoredAsync`): `CopyAsync(staging, entity key)`. The entity therefore never
    commits pointing at bytes that aren't there.
  - **After the commit** (`FileStoreInterceptor`, an `IAfterSave`): delete the claimed uploads' staged objects and the files the saved
    value no longer holds. A refused save deletes nothing (`A_refused_save_leaves_the_store_as_it_was`).
  - **After a hard delete or purge** (the same interceptor, as an `IAfterDelete`): delete every object under the entity's prefix,
    variants included (`Deleting_the_entity_deletes_every_object_it_held`). A soft delete (`IsReplaced`) keeps the row, and so its
    files.
  - A failure in between is logged, never thrown, and left to the reconcile.
- **One key layout for every object store** (`ObjectFileStore`): `staging/{token}` and
  `entities/{base64url(entityId)}/{fileId}[.{variant}]`.
  - The entity id is base64url-encoded, so no id, with its slashes, can reach outside its own prefix.
  - Every key and listing prefix is matched against the layout's pattern before any I/O
    (`Keys_and_prefixes_the_layout_did_not_make_are_refused`: `../`, `staging/a/b`, a leading `/`, a foreign root).
  - The file system store also checks that the resolved full path stays inside its root.
- **The orphan reconcile (`FilesReconciler`, hourly, `Spark:Files:ReconcileInterval`/`ReconcileGrace`, both 1 h).** For an
  object store only. It deletes an object older than the grace when:
  - it is staged, and its upload document is gone (expired), Claimed or Refused (this closes SP3's "an expiring staging document
    doesn't delete its object");
  - it is an entity's file, and that entity is gone or holds that file id nowhere in its document.

  Documents are read raw (`GetDocumentsCommand`), and every `FileId` property at any depth counts, so the sweep depends on no CLR
  type. That is the same reason `LoadAsync<object>` is avoided elsewhere. The grace covers a save in flight, whose copy exists
  before its entity commits. Evidence: `The_reconcile_removes_only_unheld_objects_past_the_grace` covers a held file and variant,
  a fresh orphan, an old orphan, a vanished entity, an expired staging object, and a Committed and a Claimed upload.
- **Direct uploads, per store:**
  - **S3** returns a POST policy (SP10). It is built with `Utf8JsonWriter`, never by splicing, signed with SigV4, and its form
    fields are kept in order in an `OrderedDictionary`.
  - **Azure** returns a create+write SAS `PUT` with `x-ms-blob-type: BlockBlob`, when the client can sign; otherwise there is no
    target.
  - **FileSystem** and **RavenDB** proxy the bytes through Spark.

  `SparkFileUploadTarget` and `PreparedFile` therefore carry `Method` and `Headers`. This supersedes M3's single request shape.
- **S3 client settings, beyond SP9's `UseChunkEncoding = false`:**
  - `RequestChecksumCalculation`/`ResponseChecksumValidation = WHEN_REQUIRED`, because S3-compatible servers differ in which
    checksum headers they accept.
  - A forward-only body is spooled to a temporary file (`SeekableSpool`, now shared with the RavenDB store), because the SDK needs
    the length once chunked signing is off.
  - `PublicUrl` is the endpoint browsers post to, when it differs from the server-side `ServiceUrl`.
- **Azure copies by streaming through the app.** A server-side copy needs the source readable by URL, through a SAS or OAuth,
  which a credential-only client lacks. S3 uses `CopyObject`, and the file system copies to a temporary file, then moves it into
  place.
- **`IFileStore.EraseAsync(entityId, fileId)`** is the explicit erasure hook from SP8. Files never calls it.
  - **RavenDB:** it deletes the attachment and its variants, then, through `DeleteRevisionsOperation(id, changeVectors)`, only the
    revisions whose `@attachments` still list the file. Evidence: `Erase_deletes_the_file_and_the_revisions_that_still_serve_it`.
    Before the erase, a revision serves the replaced file. After it, none does, the other revisions stay, and the current file is
    untouched.
  - **Object stores:** it deletes the file and its variants (`Erase_removes_the_file_and_its_variants_only`, where `f10` survives
    erasing `f1`).
  - §9's signature `EraseAsync(owner, attribute)` became `(entityId, fileId)`, because one attribute can hold several files.
- **The store conformance suite (`Files/Stores/FileStoreConformanceTests`)** has 10 tests per store, run against FileSystem,
  Azurite and Garage. It covers:
  - a forward-only round trip;
  - a missing key;
  - overwrite and delete;
  - copy;
  - listing prefixes;
  - refused keys;
  - key parsing;
  - erase;
  - a direct upload;
  - a capped direct upload.

  The RavenDB store is covered over HTTP by the claim, delete and erase tests, because its keys hang off live documents.
  - **A gated store's tests are reported Skipped**, not passed: `[StoreFact]` plus a discoverer reads `[StoreGate(...)]` from the
    derived class. xunit 2 can't otherwise skip an inherited test per class.
  - **CI** starts Azurite 3.35.0 and Garage v1.1.0 on the Spark shards (`tools/ci/start-garage.sh`). The RPC secret and the access
    key are generated per run, and the key is masked in the log. Locally, all 30 conformance tests ran green against the same
    containers.
- **Range reads move to M6** (downloads), from SP3's "put/open/range/move" list. No store needs a range read until something serves
  one.
- **Packages:** `MintPlayer.Spark.Files.FileSystem`, `MintPlayer.Spark.Files.AzureBlob` (Azure.Storage.Blobs 12.30.1, MIT) and
  `MintPlayer.Spark.Files.S3` (AWSSDK.S3 4.0.104.8, Apache-2.0). All are permissive, as the licence policy requires. Each package
  configures its store with one line: `files.UseFileSystem(path)`, `files.UseAzureBlob(...)` or `files.UseS3(...)`.

### M6 (downloads), 2026-11-22

- **`GET /spark/files/download?type=&id=&attribute=&file=[&variant=]`** (`FileDownloadService.DownloadAsync`). It runs the core read
  path, `IPersistentObjectReadPath.LoadAsync`, the same one `/spark/po/load` and the live socket use: type right, row security,
  `OnLoadAsync`, a withheld Read, and attribute rights (a Read-denied attribute is absent from the object). It then serves only a file
  that the attribute's value still holds and that is `Clean`.
  - **Every refusal is the read path's own**: `SparkDenial.RefuseJson`, which is 404, or the uniform 401 where signing in would
    help. Evidence:
    - `A_hidden_row_refuses_the_download_as_the_read_path_refuses_the_load` checks, for a signed-in and an anonymous caller,
      that the download's status equals the load's.
    - `An_attribute_the_caller_may_not_read_has_no_url_and_no_download` checks that a denied attribute and an unknown file id
      give the same status and the same body.
  - The path is a query, not route segments, because document ids contain `/`.
  - It is a GET with no side effect, so it needs no antiforgery token (route snapshot: `GET /spark/files/download`).
- **Redirect or proxy, decided by the store.** `IFileStore.CreateReadUrl(key, SparkFileReadRequest(type, disposition, expires))`
  is signed locally, with no round trip.
  - **S3** returns a presigned GET with `response-content-type`, `-content-disposition` and `-cache-control` overrides. A second,
    connection-less client signs for `PublicUrl` when that differs from `ServiceUrl`, because the signature covers the host.
  - **Azure** returns a read-only SAS on the one blob, with the same three overrides.
  - **RavenDB** and **FileSystem** return null, so Spark serves the bytes itself.
  - Each external store has a `DirectDownloads` option (default true). False proxies the bytes instead.
  - The download redirect carries `Cache-Control: private, no-store` (`A_store_with_presigned_reads_gets_a_redirect_that_is_not_cached`).
  - Conformance evidence, run against Garage and Azurite (`A_read_url_serves_the_object_with_the_asked_headers_until_it_expires`):
    the URL serves the bytes with the requested type, disposition and cache headers; the same URL with a neighbour's key is
    refused; and an expired URL is refused by the store's own server.
- **Safe headers on everything Spark serves** (`SparkFileServing`, `ServeAsync`):
  - `Content-Type` is the stored, sniffed type;
  - `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox; default-src 'none'`;
  - `Content-Disposition` comes from `ContentDispositionHeaderValue.SetHttpFileName` (an ASCII `filename` plus
    `filename*=UTF-8''…`). It is `inline` only for a `SparkImage` whose type is PNG, JPEG, GIF or WebP, so a `BinaryFile` PNG
    downloads (`Only_a_raster_image_of_an_image_attribute_is_inline`);
  - `Cache-Control: private, no-store`;
  - the ETag is the file's SHA-256, or the variant's id (a key never holds other bytes).

  Range and `If-None-Match` go through `TypedResults.Stream(..., enableRangeProcessing)`. A forward-only stream (a RavenDB
  attachment) is spooled to a temporary file only when a `Range` header asks for it. Evidence:
  `A_download_serves_the_bytes_with_the_safe_headers`, and `A_download_answers_a_range_and_a_matching_etag` (206 with the first 10
  bytes, then 304).
- **Embedded URLs (D15, the SP1 decision):** the signing happens in core's serializer, not in each read path.
  - `SparkPresentation`'s modifier now also runs `SparkFileUrls.Sign` on every outbound `PersistentObject`, after `Check`. It walks the
    attributes that survived presentation, AsDetail rows included (their files live on the parent document), and asks
    `ISparkFileUrlSigner` (new in Abstractions; Files implements it) for each file and variant.
  - The answers are kept per request, by instance. A write-only `url` property added to the `SparkFile`/`SparkImageVariant`
    contracts reads them back.
  - The table is cleared when the outermost object finishes serializing, so a live socket, which keeps one `HttpContext` for its
    whole life, does not accumulate URLs.
  - The URL is not a CLR member, so RavenDB never stores it (`A_load_embeds_a_download_url_for_a_file_and_a_content_url_for_an_image`
    reads the raw document).
- **What each file embeds** (`SparkFileUrlSigner`):
  - **An image, its variants, and any file inside an AsDetail row** get a short-lived signed URL (`EmbeddedUrlLifetime`, 5 min), so
    an `<img>` needs no further request.
  - **A `BinaryFile` of a top-level attribute** gets the checked download URL. It never expires, and a revoked right takes effect
    at the next click.
  - **A file that is not `Clean`** gets no URL and no download (`A_file_that_is_not_clean_is_neither_linked_nor_served`, for
    Pending, Infected and ScanFailed).
- **Content tickets** stand in for presigned URLs on the proxied stores: `GET /spark/files/content/{ticket}`, `allow-anonymous`.
  - A ticket is a `ServedFile(key, type, disposition, etag)` protected by `ITimeLimitedDataProtector` (purpose
    `MintPlayer.Spark.Files.Content.v1`): encrypted, authenticated, and carrying its expiry. This is the "local HMAC" of D15,
    using the app's data-protection keys, so nothing new needs a key.
  - The ticket is the credential, as a presigned URL is, so it works from a cookie-less `ContentOrigin` (D8,
    `Spark:Files:ContentOrigin`, validated as a bare http(s) origin).
  - A changed or garbage ticket gives 404 (`A_changed_ticket_serves_nothing`), and so does an expired one (`A_ticket_expires`, 1 s
    lifetime).
- **TTLs** (`Spark:Files:DownloadUrlLifetime` 60 s, `EmbeddedUrlLifetime` 5 min), as the plan states. **Revoking a right takes
  effect for a signed URL only once it expires**; the option docs and the S3/Azure READMEs say so.
- **Deviation from §3.5's path:** `/spark/files/{objectTypeId}/{id}/{attribute}[/{variant}]` became the query form above. One
  attribute can hold several files, so `file=` is needed, and ids contain `/`.
- **Not in M6:**
  - **The SP7 browser check** needs a running app with file attributes, so it runs with M10's Fleet demo.
  - ~~**AsDetail files** embed signed URLs only; the checked download covers top-level attributes. Uploads into AsDetail rows were
    not built in M3/M4 either; that gap is tracked under M10.~~ Superseded by M10: the checked download also takes
    `attribute=Parent.Field`, and uploads into rows are built. A row's file still *embeds* a signed URL.
- **Test fixture:** `FiCarActions` implements `ISparkOwnsRowSecurity`, because a security file that grants the type, rather than the
  permissive baseline, must declare its rows. The permissive test baseline does not apply attribute-level denials, so the
  attribute-rights test uses a file that grants FiCar to signed-in users only.

### M7 (scanning), 2026-11-22

- **`IFileScanner`** (`Files/Scanning`): `ScanAsync(Stream, FileScanContext, ct)` returns `FileScanResult`, which is Clean,
  Infected(signature) or Failed(reason). `FileScanContext` carries the type, attribute, user, sanitised name, sniffed type, length
  and SHA-256. Register a scanner with `files.UseScanner<T>()` or `UseScanner(factory)`.
- **Where it runs:** last in `CommitOneAsync`, after the size check, the sniff and the `IFileUploadHandler`s, so every cheaper
  refusal comes first (`An_upload_handler_refusal_comes_before_the_scan`). `FileScanRunner` is shared by both modes.
  - It bounds a scan with `Spark:Files:ScanTimeout` (2 min).
  - It turns a timeout, an exception and a null result into `Failed`: fail-closed, D9.
- **Verdicts** (`FileScanningTests`):
  - **Clean** → `Committed`/`Clean`, claimable.
  - **Infected** → `Refused`/`Infected`, the staged bytes deleted after the save. The upload document stays as the tombstone
    until it expires, and the signature goes to the log. The uploader reads "The file was refused by the malware scanner."
  - **Failed** → `ScanFailed`. The bytes are kept and the token is not claimable. The document goes back to `Uploaded`, so
    committing again retries the scan (§3.4 "ScanFailed can be retried"). The commit reports the error. Before M7, `ToCommitted`
    reported errors only for `Refused`, so a failed scan read as a success with no error; it now reports the error.
    Theory `A_scan_without_a_verdict_is_never_a_pass_and_committing_again_retries_it`: failed, throws, times out, null.
- **Deferred mode (D6), a deviation from §3.4's "a queue that scans Pending files":** the worker scans the **staging upload**, not
  the entity.
  - The commit answers `Committed`/`Pending`. The claim already requires `Clean`, so a pending token can't be saved, and the
    client commits the token again to read the verdict.
  - `DeferredFileScanner` keeps an in-process queue (`Channel`, deduplicated). It runs a sweep that streams the
    `SparkFileUploads/` prefix (no index) every `ScanSweepInterval` (1 min), for what a restart dropped and for `ScanFailed`
    retries. Writes use optimistic concurrency.
  - Why the staging upload rather than the entity: an entity never holds an unscanned file. A "made Clean later" path would
    have to patch documents of any type, and every read path would have to deal with Pending files.
  - Evidence: `Deferred_answers_pending_and_the_token_is_claimable_only_after_the_worker_found_it_clean`,
    `Deferred_infected_deletes_the_bytes_and_refuses_the_token`,
    `The_deferred_sweep_picks_up_what_the_queue_lost_and_retries_a_failed_scan`.
  - Not done: the renderer's "scanning…" state lands with M9.
- **Startup guard (D5):** with no `IFileScanner`, `AddFiles()` behaves by environment:
  - in Development it logs a warning;
  - outside Development it refuses to start;
  - with `AllowUnscannedFiles` it starts and warns on every start.

  A missing `IHostEnvironment` counts as "not Development" (fail-closed). This covers every non-Development environment, not
  only `Production`, so a Staging host can't quietly skip the scanner. Evidence: `FileScanningStartupTests` boots a Production
  host both ways, plus the unit `Outside_Development_without_a_scanner_…`.
- **`MintPlayer.Spark.Files.ClamAV`** (MIT, no package dependency; SP4 decided on our own client):
  - `ClamAVScanner` speaks `zINSTREAM\0` with 4-byte big-endian chunk lengths and a zero terminator, and reads the reply up to
    `\0`.
  - `Parse` maps the replies: `stream: OK` → Clean; `stream: <x> FOUND` → Infected; everything else → Failed. That includes
    `ERROR`, an empty `FOUND`, `OKAY`, an unterminated reply, EOF and unknown text. The `Every_reply_class_maps_fail_closed`
    theory has 12 cases.
  - A fake clamd on loopback checks the wire format: 4096 + 4096 + 1808 chunks, then the terminator. It also checks that a drop
    before the reply is Failed, that silence times out as Failed, and that a port with no listener is Failed.
  - `MaxStreamLength` (25 MiB) refuses a file without connecting. The bytes actually read are capped as well, so a lying length
    is caught (`A_file_over_MaxStreamLength_is_refused_without_being_sent`).
  - The EICAR string is assembled at runtime from four array elements, so no single literal carries it.
  - `ProbeAsync` scans EICAR, and the `clamav` health check (tag `ready`) is healthy only on a detection.
- **The shipped `clamd.conf` (package root): SP4 needs a correction.** SP4 found the archive gap closed by `AlertExceedsMax yes`
  alone. On ClamAV 1.5.5 that holds only if **`MaxFileSize ≥ MaxScanSize`**:
  - `cli_unzip` trims a member over `MaxFileSize` to that size, scans the start, and raises no alert (clamscan `--debug`:
    "cli_unzip: trimming output size to maxfilesize (26214400)"). Only exceeding the `MaxScanSize` total alerts.
  - Measured with one zip holding a single 150 MiB member of zeros (153 KB on the wire), `--alert-exceeds-max=yes
    --max-scansize=100M`: `--max-filesize=25M` → **OK**; 100M → `Heuristics.Limits.Exceeded.MaxScanSize FOUND`; 101M → FOUND.
  - Five 30 MiB members: FOUND with alerts on, OK with alerts off.
  - The conf therefore sets `StreamMaxLength 25M` (the upload), and `MaxFileSize 100M` + `MaxScanSize 100M` (unpacking). The
    package README states the rule.
  - The live test `An_archive_hiding_an_over_limit_file_is_infected_with_AlertExceedsMax` failed red at 30, 110 and 150 MiB with
    `MaxFileSize 25M`, and passes with the shipped conf.
- **CI:** a "ClamAV" step on the Spark shards runs `clamav/clamav-debian:1.5.5` detached:
  - with the package's `clamd.conf` mounted, so the shipped file is the one tested;
  - with `CLAMAV_NO_FRESHCLAMD=true`, using the image's bundled signatures, so no CDN download per run;
  - "Run tests" waits for `socket found, clamd started`, bounded at 3 min.

  `SPARK_CLAMAV_HOST` gates `ClamAVIntegrationTests` through the existing `StoreGate`. It covers EICAR plus the probe, a clean
  PDF, a 30 MiB stream over clamd's `StreamMaxLength` (the reset is Failed), and the archive above. All four pass locally against
  the same image and conf. Locally `clamd started` took about 50 s, with signatures loaded from the image.
- **Local run:** 66 tests (FileScanningTests, FileScanningStartupTests, ClamAVScannerTests, ClamAVIntegrationTests,
  FilesStartupValidationTests, FileUploadEndpointTests), all green with `SPARK_CLAMAV_HOST` set.

### M8 (images), 2026-11-22

- **`IImageProcessor`** (`Files/Images`): `ProcessAsync(Stream, ImageProcessingRequest(ContentType, MaxPixels, Variants), ct)`
  returns a `ProcessedImage` (type, size, content, variants). A processor refuses with `ImageRejectedException` (too many pixels,
  undecodable). Register one with `files.UseSkiaSharpImages()`, `files.UseNetVipsImages()` or `files.UseImageProcessor<T>()`.
  The contract, which `ImageProcessorConformanceTests` runs against both processors (38 tests, 19 each):
  - **the pixel cap is read from the header before decoding.** A 30000×30000 PNG (900 Mpx, 113 KB on the wire) is refused in
    under 5 s; the theory refuses 401 px against a 400 cap and accepts 400;
  - **EXIF orientation is applied**, all 8 origins, each checked by the colour of the four quadrants;
  - **re-encoding is the only output**: EXIF, GPS and the artist tag are gone from the original and from the variants, and a
    GIF/HTML polyglot loses its `<script`;
  - **first frame only, and a GIF becomes a PNG** (the hand-built 2-frame GIF keeps red, not blue). JPEG and WebP keep their type
    at quality 90; PNG stays PNG;
  - **variants are WebP at quality 80**, fitted inside the edge (`ImageProcessing.Fit`, rounded, at least 1 px), never upscaled,
    all from the one decoded bitmap;
  - garbage, truncated input and a format that doesn't match the sniffed type are refused.
- **Where it runs in the commit:** read back (size + SHA-256) → sniff → **process** → `IFileUploadHandler`s → scan. Handlers and
  the scanner therefore see the bytes that will be stored. Processing replaces the staged original **in place** (same staging
  key); its length and SHA-256 are recomputed while writing (`HashingReadStream`), so the committed hash is that of the stored
  bytes (`A_commit_re_encodes_the_image_in_place_and_stages_its_default_variants`; a bomb:
  `A_bomb_is_refused_at_commit_and_its_bytes_deleted`).
  - `upload.ImageProcessed` makes a retried commit (after `ScanFailed`) skip both the sniff and the processing: the staged bytes
    are already the processor's output, so processing them twice would degrade a JPEG twice.
  - **The name follows the type** (`FileNameSanitizer.WithExtensionFor`): `wave.gif` is stored as `wave.png`
    (`A_gif_is_stored_as_a_png_named_png`). A name that already fits (`.jpeg`, `.jfif` for JPEG) is kept.
  - **Fail-closed:** `ImageRejectedException` refuses with its own message. Any other exception is logged and refuses with "The
    image could not be read." (`A_processor_that_throws_refuses_the_upload_with_a_generic_message`). A processor that returns a
    variant it was not asked for, or one twice, is refused too. A refusal deletes every staged key, variants included
    (`SparkFileKeys.StagedKeys`, also used by the scan path and `DeferredFileScanner`).
- **The pixel cap:** `Spark:Files:MaxImagePixels` (40 million, §3.6), tightened per attribute by the new
  `fileOptions.maxPixels` (only on an Image, positive; core `ShapeErrors`). Evidence: `An_attributes_maxPixels_tightens_the_global_cap`.
  `maxPixels` is hashed with the rest of `fileOptions` from the raw JSON, so existing model hashes don't move.
- **Concurrency:** `ImageProcessingGate` holds at most `Spark:Files:MaxConcurrentImageProcessing` (2) decodes at once. A 40 Mpx
  RGBA bitmap is 160 MB, so the gate is what bounds memory, not the pixel cap alone.
- **Variants:** `fileOptions.variants`, or `Spark:Files:DefaultImageVariants` (`thumb` 160, `medium` 800) when the attribute names
  none. Names are 1–64 of `[A-Za-z0-9_-]` (core caps the length at 64 now, so a name always fits a key).
  - **Key layout:** a variant's file id is `{fileId}.{variant}` (`SparkFileKeys.VariantFileId`). Staged, it is
    `staging/{token}.{variant}` on object stores and `SparkFileUploads/{token}/original.{variant}` on RavenDB
    (`IFileStore.StagingVariantKey`). `ObjectFileStore.KeyPattern` allows one `.variant` suffix, nothing else
    (`staging/abc.`, `staging/abc.a.b`, `staging/abc..x` and `staging/abc./x` are refused in the conformance suite).
  - **Claims move the variants with the file** (`FileClaimLedger.Attach.Variants`): `AttachAsync` on RavenDB, `CopyAsync` on
    object stores. Clearing or replacing the image deletes them too. Evidence:
    `The_claim_moves_the_variants_with_the_file_and_a_variant_downloads`,
    `Clearing_an_image_removes_its_variants_too`, and on the file-system store
    `An_images_variants_are_staged_beside_it_copied_with_it_and_removed_with_it`.
- **Without a processor**, an Image is stored as uploaded (sniffed, no variants, no size) and startup logs a warning that names
  the Image attributes. It isn't refused, because the sniff already refuses markup, and a refusal would make the processor a
  hidden requirement of `AddFiles()` (D12 keeps the processors separate packages). §3.6's "the grid shows a file icon" covers
  the client side (M9).
- **`--spark-regenerate-variants [--force]`** (`app.RegenerateSparkImageVariantsIfRequested(args)` on the built app, before
  `app.Run()`): makes the variants again for every top-level Image whose variants don't match the configuration (names and fitted
  sizes, `ImageVariantRegenerator.UpToDate`), from its stored original. It runs on the built app because it needs the database and
  the store; it returns exit code 1 when an image could not be processed.
  - Two phases per entity: write the variants, then load the entity again and save it with its change vector. Writing a RavenDB
    attachment moves the document's change vector, so saving from the first load would always conflict (found while writing it).
    An edit in between is retried once.
  - Variants no longer configured are deleted after the save. Evidence:
    `Regenerating_skips_images_that_match_remakes_missing_variants_and_removes_obsolete_ones` (up to date → 0 regenerated; a
    missing `thumb` and a stale `old` → `thumb` remade at 160×80, `old` deleted; `--force` remakes it).
  - Not visited: images inside AsDetail rows (documented on the class; M10 adds AsDetail file attributes).
- **Packages** (versions at least two weeks old on 2026-11-22):
  - `MintPlayer.Spark.Files.Images.SkiaSharp` (MIT): `SkiaSharp` 3.119.2 + `SkiaSharp.NativeAssets.Linux.NoDependencies` 3.119.2
    (published 2026-02-07; 4.155–4.157 were all under two weeks old). Decodes with `SKCodec` into RGBA8888, orients with the 8
    matrices, scales with linear + mipmap filtering.
  - `MintPlayer.Spark.Files.Images.NetVips` (MIT code, LGPL natives, README says so; the licence policy keeps LGPL out of core):
    `NetVips` 3.2.0 (2026-01-01) + `NetVips.Native` 8.18.7 (2026-09-28). Sets `NetVips.BlockUntrusted = true` (refuses the
    loaders libvips marks untrusted) and `Cache.Max = 0` once; loads with `failOn: Error`, checks the `vips-loader` against the
    sniffed type, `Autorot`, saves with `keep: None`.
- **Local run:** ImageProcessorConformanceTests 38, FileImageProcessingTests 9, and 107 across the store, claim, download, upload,
  scanning, startup and sanitiser classes; FileAttributeCoreTests, FilesStartupValidationTests and FileNameSanitizerTests 70. All
  green.

### M9 (ng-spark `files` entry point), 2026-11-22

- **Query cells carry signed URLs too.** D15 signed only the URLs of a loaded PersistentObject, so a grid had file values with no
  `url`. `QueryResultProjector` now tags a cell that holds a `SparkFile` (or a list of them) with its owner
  (`QueryResultItemValue.FileOwner`, `[JsonIgnore] internal`: the row's type and id), and `SparkPresentation` signs it while
  serializing, as for a PO (`SparkFileUrls.SignCell`, released by the generalised `Release`). The cell's key is the attribute.
  Evidence: `FileDownloadTests.A_query_cell_embeds_the_same_urls_as_a_load`.
- **`storesFile` on the attribute definition.** An empty value cannot tell the client whether an `Image` holds a stored file or
  a URL string (§3.2 keeps the URL form). `SparkCatalogue.MarkStoredFiles` sets `storesFile: true` on the copies it serves, from
  the property's CLR type (element type for a list), never on the loader's definitions; it is not written to the model JSON
  and not hashed (the hash reads named raw properties only). Evidence:
  `FileDownloadTests.The_type_tells_the_client_which_file_attributes_are_uploaded_and_which_hold_a_url`.
- **SP6 as built.** `resolveAttributeRenderer(registry, attr, slot?)` takes a **slot**: the named registration must provide the
  slot asked for, else the data-type registration that does. Without the slot, `spark-image` named for a column-only use would
  hide the data-type edit renderer. All hosts use it (detail presenter ×3, form ×3, grid ×2, dashboard). Evidence: the
  `resolveAttributeRenderer (#700 SP6)` block of `files/src/spark-files.spec.ts` (name beats data type, a registration without
  the slot is skipped, an app registration beats `withFileAttributes()`, no match is `null`).
  - The form's fail-safe is `isUnrenderedFileAttribute`: `storesFile`, a file data type, and no edit renderer → the
    `files.notEnabled` notice, never the text input.
  - The `'image'` presentation branches are gone: `Image` (a URL string) renders through `isDisplayableImageUrl` (https, a
    same-origin path, or a raster `data:image/(png|jpeg|gif|webp)` URI; never SVG, `javascript:` or `http:`). Evidence:
    `spark-grid-cell.component.spec.ts` (non-URL, `javascript:`, `http:` and SVG data URI show as text) and
    `never draws a non-displayable url as an image`. `input-type` maps `Image`/`BinaryFile` to `url`.
- **Deviation from SP6: the edit contract gained `objectTypeId` and `objectId`.** SP6 said the contract stays unchanged, but the
  prepare needs the type and the object (§3.3: a token is bound to both), and `value`/`attribute` don't carry them. Both are
  optional inputs, filtered by `withDeclaredInputs` like the rest, so existing renderers are untouched. ~~AsDetail cells don't
  get them; AsDetail files are M10.~~ M10: AsDetail cells and the AsDetail modal's form get the document's ids plus
  `attributePath`.
- **`@mintplayer/ng-spark/files`:** `withFileAttributes()` registers `spark-file` (`BinaryFile`) and `spark-image` (`Image`) by
  data type at `builtInRegistrations`.
  - `SparkFilesService`: prepare / upload / commit / `commitUntilScanned` (2 s × 30 by default, bounded, abortable). **A direct
    upload uses a bare `HttpClient` on `HttpBackend`**, so neither the antiforgery header nor an app's interceptors reach the
    store's presigned URL; a Spark upload uses the app client and gets the antiforgery header like every Spark POST. Evidence:
    `sends a direct upload past the interceptors, with only the slot headers` / `sends a Spark upload through the app client`.
  - The edit renderer writes `{ uploadToken }` only for a file the commit answered `Clean` with no error; uploading, checking,
    scanning, refused and failed files are not in the value, so a save can't claim them. The server's refusal text is shown as
    text (interpolation, never HTML). Stored files are echoed as read, removing one drops it, a list reorders; the value is
    compared by file id (`sameFiles`), so a re-read with fresh signed URLs doesn't reset the rows. A local preview is an object
    URL of a raster image only, revoked when the row goes. `maxBytes` and `maxCount` are checked before sending (the server
    checks again).
  - Detail: download link only for a `Clean` file with a URL; an image is drawn at its nearest larger variant to 640 px and
    enlarges to the original on click.
  - **URL refresh on expiry** (§0.12): an image or variant URL lives five minutes. On the `<img>` error the detail page and the
    form switch that image to the checked download (`/spark/files/download?type&id&attribute&file&variant`), which is authorized
    per request and serves a raster image inline; no new endpoint. A grid cell has no object type, so it falls back to the name.
    Evidence: `draws an image at its variant and falls back to the checked download when the url fails`.
- **Local run:** FileDownloadTests 14/14. Vitest: `files/` + `spark-po-form.component.spec.ts` 92/92 (the form test checks the
  notice and that no text/url input is drawn), `spark-grid-cell.component.spec.ts` 27/27, `files/` + `pure-pipes.spec.ts` +
  grid cell 146 in the first run (one failure, a spec that configured TestBed twice in a loop, now `it.each`). ngc type-check of the
  library clean. Translation goldens regenerated (17 `files.*` keys, codecoverage and qna).

### M10 (AsDetail files, Fleet demo, browser E2E), 2026-11-22

- **Files inside AsDetail rows ship in this PR** (owner, 2026-11-22, asked because the M1 note and §8b disagreed: "Build it in
  #702"). This supersedes the M1 refusal.
- **What an upload is bound to: the document and `Parent.Field`** (`ServiceEntries.Invoice`), not the row. A new row has no
  key until the save, and a row's index moves with every reorder, so §8b's `detail.index.attr` could not have worked. The
  claim checks the same four things as a top-level one, against the root: user, root type id, path, root id (`ObjectId`).
  - One level only: `Parent` must be an AsDetail attribute of the root and `Field` a file attribute of its row type.
    `SparkFileAttributePath.Resolve` is the one reader of the path, used by prepare, commit, the deferred scan and the
    download. A row inside a row holding a file is still refused at startup, naming it. Evidence:
    `FileAttributeStartupCheckTests.A_file_inside_an_AsDetail_row_starts` / `…_of_an_AsDetail_row_is_refused`, and
    `FileAsDetailTests.Prepare_refuses_a_path_that_names_no_row_file_attribute` (5 cases: a non-file field, three segments, a
    non-AsDetail parent, the bare parent, an empty parent).
  - Prepare's rights: the save right on the document (as before), the AsDetail attribute writable for the verb, and the row
    attribute allowed for the verb on the **row type** and not read-only. The row attribute's `fileOptions` apply. Evidence:
    `Prepare_applies_the_row_attributes_fileOptions`.
  - Plain row classes work, not only `[ValueObject]` ones: the test row `FiService` is a plain class with an `Id`.
- **Keep and remove.** A row has no stored value of its own to keep from, since a dropped row is never written to and a new
  row starts empty. So `ISparkFileValueHandler` gained **`BeginAsync`**, called once by the outermost
  `PopulateObjectValuesAsync`. It records the files every row holds per path before the write.
  - A keep may name any file in that snapshot for its path, each once across all rows (a shared kept set). So a file can move
    from one row to another, but two rows can't both keep it.
  - What to remove is decided after the write, in `OnStoredAsync`: the rows are walked again, and every snapshot file no row
    holds now is detached. That covers dropped rows, cleared and replaced files, and rows the caller may not edit (restored
    unchanged, so their files are still held).
  - The same walk refuses a document where two rows hold one file. That can only happen when a restored row and a new row
    keep the same file; the shared set stops it in every other case.
  - The ledger is keyed by the root, so the bytes go to the root's attachments, which is where the download and the erase
    look. Evidence: `FileAsDetailTests` (create, keep by echo, drop, replace, unposted rows untouched, move once / two rows
    refused, foreign and top-level ids refused, tokens bound to path and document, download).
- **`EntityMapper`:** a private `fileScope` (root, root type name, AsDetail prefix), set by the outermost populate and pushed
  by `WritePropertyAsync` around an AsDetail. `WriteFileAsync` passes `SparkFileWriteContext.Owner` when the prefix is set.
  The mapper is scoped, so the field is per request.
  - **`RowWouldChangeAsync` no longer writes a file property onto its scratch row.** That write would claim the upload token,
    and the real write would then find it spent. It compares file ids instead: any token, or a different list, counts as a
    change.
- **Download:** `attribute=Parent.Field` finds the file in the rows of the AsDetail as the read path returned it, so a row
  type's attribute right or a hidden AsDetail removes it from what can be served. Evidence:
  `A_rows_file_downloads_through_the_documents_read_path_and_only_one_it_holds`. A row's file still *embeds* a signed URL
  (M6). The checked download is the fallback when that URL expires.
- **Client:** the edit contract gained `attributePath`. An inline AsDetail cell gets the document's `objectTypeId`/`objectId`
  and `Parent.Field`. The AsDetail modal's nested `spark-po-form` gets a `fileOwner` input (memoized per attribute) and
  hands the same to its fields. `spark-file-edit` prepares with `attributePath ?? attribute.name`, and its expired-URL
  fallback uses the path too. Evidence: `binds a file field of an AsDetail row to the document and Parent.Field (#700 M10)`
  and `prepares a field of an AsDetail row against the document and its Parent.Field path (M10)`.
- **Fleet demo:** `Car.Photos` (`List<SparkImage>`), `Car.RegistrationDocument` (`SparkFile?`) and `ServiceEntry.Invoice`
  (`SparkFile?`, the AsDetail sample). `Program.cs` calls `spark.AddFiles(files => files.UseSkiaSharpImages().UseScanner<FleetDemoScanner>())`;
  the bytes stay in RavenDB attachments (the default store). The client adds `withFileAttributes()`.
  - Both BinaryFiles carry a hand-written `fileOptions` (`accept: [".pdf"]`, `maxBytes` 5 MB): without one a BinaryFile refuses
    every upload (D4). It is structural, so the synchronize re-ran after the edit (`modelHashes.json` moved).
  - `FleetDemoScanner` is **demo only**: it knows the EICAR test string and nothing else, so the demo and its browser test can
    show an infected upload refused without ClamAV, and Fleet starts outside Development (the E2E host's environment) without
    `AllowUnscannedFiles`. A real app uses `UseClamAV(…)`.
- **Two shape fixes the Fleet synchronize found**, both because a stored file is a class with properties:
  - SPARK017 ("needs a row key") fired on `List<SparkImage>`. `ValueObjectCompletenessAnalyzer.EmbeddedTypeOf` now skips a type
    deriving from `SparkFile`. Evidence: the synchronize's build failed with SPARK017 on `Car.Photos`;
    `ValueObjectCompletenessAnalyzerTests.A_list_of_stored_files_is_not_reported` (14/14).
  - The synchronize wrote `SparkFile.json` and `SparkImage.json` as embedded models: `CollectEmbeddedTypes` and
    `ModelShapeDiscovery` ask `SparkModelShape.IsComplexType`, which said yes. It now says no for a stored file, as it already
    did for `TranslatedString` (`GetDataType` classified both correctly all along). Evidence: the first synchronize's log
    ("Synchronized model (embedded): SparkImage", "…: SparkFile"); after the fix neither file is written;
    `SparkModelShapeTests.A_stored_file_is_not_a_nested_model` (2 cases). The test was written alongside the fix, so its red
    evidence is the synchronize log, not a red run.
- **Browser E2E:** `Files/FileAttributeBrowserTests` (Fleet collection, one test for the shared rate-limit bucket), green
  locally 2026-11-22. On the real file input it covers:
  - SP7: `page.html` (`<script>alert(1)</script>`) is refused, the row shows the server's message, and no dialog opens at any
    point.
  - The demo scanner refuses an EICAR-carrying PDF.
  - A photo and a PDF reach `ready`, the save claims them, and the detail page shows the image and a `scan.pdf` link.
    Downloading the link gives the exact bytes with `attachment` and `nosniff`; the image is served as `image/*` with
    `nosniff`.
  - Replace (`renewed.pdf`), then clear; the stored value follows and the photo is untouched.
  - The fixtures are the Files integration tests' real files, linked into the E2E project, not copied.
  - Not covered in the browser: §8b's grid thumbnail (Fleet has no query column holding a file; the column renderer's variant
    URL is in `spark-files.spec.ts`), the Deferred scan states (proven over HTTP in M7), and the AsDetail invoice (proven over
    HTTP in `FileAsDetailTests` and in the po-form spec).

### M11 (CodeCoverage custom avatars, Garage, ClamAV, Trivy), 2026-11-22

- **Model.** The synchronize moved `AvatarUrl` from `image` to `Image` (M1's rename) and added `CustomAvatar` (`SparkImage?`,
  `[IgnoreForIndex]`), shown on the account page in the Identity group. Its hand-written `fileOptions`: `accept` png/jpg/jpeg/webp,
  `maxBytes` 2 MB, one `thumb` variant at 160 px. The synchronize re-ran after the edit (`modelHashes.json` moved).
- **One resolved avatar** (Q15): `AccountAvatar.Resolve` returns the custom avatar's **checked** download URL
  (`SparkFileDownloadUrl`, the `thumb` variant), or else the forge's `AvatarUrl`. A custom avatar that would answer 404
  (not `Clean`, no thumbnail) never replaces a working forge avatar. `MyAccountsService` uses it, so the Home grid and
  `/api/me/accounts` draw the same picture. The checked URL never expires and runs the account's read path on every
  request, so handing it out decides nothing about who may see it. Evidence: `AccountAvatarTests` (7 cases).
  - `AccountAvatar.AccountType` holds the account type id once. `PersistentObjectIds` is generated per assembly, so the
    test project could not name the app's (CS0117 on the first run).
- **Visibility is the account's.** `CustomAvatar` joins `OwnerSettings.PublicAccountAttributes`, next to `AvatarUrl`, and
  the test copy in `OwnerPrivacyEndToEndTests` with it. Only a manager of the account can write it: `Edit/Account` is
  narrowed to managed owners by `GetRowFilterAsync` and `OnDisableActionsAsync` (D-A4), unchanged.
- **Admins can clear: the `ClearAvatar` custom action**, on the account page, granted to the Administrators alone
  (`ClearAvatar/Account`, right key `…00b7`). A custom action, because an administrator manages no account and widening Edit
  would hand them every setting. The parent must be an `Account` by type id, because a load by id alone would read any
  document as an account. It clears, commits, then calls `IFileStore.EraseAsync`, which erases the original, its variants
  and the revisions that still hold it. An erase that fails after the commit is logged and leaves unreferenced bytes for the
  reconcile, never an account pointing at nothing. Evidence: `ClearAvatarActionTests` (clear + erase, failed erase, no
  avatar, parent of another type).
- **ClamAV in production** (owner, 2026-11-22, §0 item 13):
  - `coverage-clamav` runs `clamav-debian:1.5.5` with `ops/clamd.conf`, which is the package's conf plus
    `ConcurrentDatabaseReload no`. `ClamdConfTests` checks that every directive of the package's file is there with its
    value, so a limit tightened in the package cannot be left behind in production.
  - It runs with `mem_limit: 1400m`. It is on an internal network shared with the app alone, plus its own egress network
    for freshclam.
  - `UseClamAV` is called only when `ClamAV:Host` is set. The test host sets `Spark:Files:AllowUnscannedFiles` instead.
- **Garage in production, internal only. Deviation from D13's public host:**
  - `coverage-garage` runs `dxflrs/garage:v1.1.0` (the SP9 version) with `ops/garage.toml`, which holds no secret. The RPC
    secret comes from `.env`.
  - Its volume `garage-data` is mounted there only, and it sits on the internal `coverage-files` network, shared with the
    app alone. `verify-backend-networks.test.mjs` asserts the networks, and that no other service mounts the volume.
  - D13 also put it behind Traefik on its own host, for a cookie-less presigned-read origin. That host needs a DNS record
    that does not exist yet, plus a hard-coded router rule (`docs/code-coverage/hosting.md`: an interpolated rule once
    served Traefik's self-signed certificate). So `Files__S3__DirectUploads=false` and `DirectDownloads=false`: the app
    proxies the bytes, with the same checks. For 2 MB avatars that costs little. Switching to the public host later only
    needs those two flags, `PublicUrl`, the router labels and the DNS record.
  - `GARAGE_RPC_SECRET` uses `:-`, not `:?`, so a server without it fails Garage alone, never the whole stack's deploy.
  - The app uses S3 only once `COVERAGE_FILES_BUCKET` is set, and keeps files as RavenDB attachments until then.
  - The bootstrap (layout, bucket, a bucket-scoped key) and the backup are in `docs/code-coverage/hosting.md`, "File storage".
- **Trivy** (C1, §0 item 14):
  - `tools/ci/trivy-no-sshd.sh` scans images, and `tools/ci/trivy-no-sshd.mjs` fails a report that has an
    `openssh-server*` package or CVE-2026-123158.
  - `code-coverage-image-check.yml` loads the three images it builds and scans them. The app job also scans every
    third-party image the compose file pins. These are steps in the existing jobs, not new jobs (the 20-runner budget).
  - Evidence: `tools/trivy-no-sshd.test.mjs` (3 cases). A local run against `clamav-debian:1.5.5` (Debian 13.7, 112 OS
    packages listed) and `garage:v1.1.0` (no OS) passed both.
- **Deploy lists kept in step:**
  - The deploy fetches `clamd.conf` and `garage.toml` next to the compose file.
  - The deploy path filter, `code-coverage-image-check.yml` and the Dockerfile COPY list gained the four files libraries,
    and `libs/apps/MintPlayer.Spark.Apps`, which was already missing from all three.
- **Client:** `withFileAttributes()`, so `CustomAvatar` gets its upload renderer. The `Image` renderers already draw a
  string URL, so `AvatarUrl` keeps rendering.
- **Goldens:** `codecoverage-translations.golden.txt` (+4 keys). There is no new endpoint, because the action goes through
  the generic custom-action route, so the route snapshot and `CsrfSurfaceTests` do not move.
