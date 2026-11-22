# File and image attributes (#700)

**Packages:**
- NuGet `MintPlayer.Spark`, `MintPlayer.Spark.Abstractions` and `MintPlayer.Spark.SourceGenerators` get a minor.
- Seven NuGet packages are new: `MintPlayer.Spark.Files`, `.Files.FileSystem`, `.Files.AzureBlob`, `.Files.S3`,
  `.Files.ClamAV`, `.Files.Images.SkiaSharp` and `.Files.Images.NetVips`. They target `net11.0`, so each starts at `11.0.0`.
- npm `@mintplayer/ng-spark` gets a minor (new entry point `/files`).

The versioning engine computes the numbers from the tags. Everything ships once the merged pull request is backported to
`release/dotnet-11.x` and `release/angular-22.x`.

Guide: [guide-file-attributes.md](guide-file-attributes.md). Decisions and evidence: [file_attributes_PRD.md](file_attributes_PRD.md).

---

## New

1. **`BinaryFile` and `Image` data types.** Declare a `SparkFile?`, a `SparkImage?` or a list of them, call
   `spark.AddFiles(…)`, and add `withFileAttributes()` on the client. Uploads are staged, claimed by a save, size-checked,
   sniffed from their bytes, optionally scanned, and re-encoded (images). Downloads go through the entity's read path.
2. **Stores:** RavenDB attachments (the default), the file system, Azure Blob and S3-compatible storage.
3. **Scanning:** `IFileScanner`, inline or deferred, fail-closed, with a ClamAV client. Outside Development an app with
   file attributes refuses to start without a scanner unless `Spark:Files:AllowUnscannedFiles` is set.
4. **Images:** SkiaSharp (MIT) or NetVips (LGPL natives). They check the pixel count, apply the orientation, strip metadata,
   and make WebP variants. `--spark-regenerate-variants` remakes the variants.
5. **`IFileStore.EraseAsync`** erases a file, its variants and the revisions that hold it, for GDPR erasure.
6. **`SparkFileDownloadUrl`** gives a checked, non-expiring download URL for custom endpoints.
7. **ng-spark renderer registrations take a `dataType`**, which becomes the default renderer for every attribute of that
   data type ([guide](guide-custom-attribute-renderers.md#a-default-renderer-per-data-type-datatype)).
8. **SPARK063:** a stored-file property in an assembly that never calls `AddFiles()`.

## Fixed

9. **A JSON object that a property's complex type cannot be read from is now a validation error** that names the attribute.
   It used to be dropped silently.

## Breaking changes

10. **The `image` string presentation is now the `Image` data type** (owner: "for once allow breaking changes"). A `string`
    attribute with `"dataType": "Image"` still holds an `https:` URL or a `data:` URI and is displayed only.
    `--spark-synchronize-model` rewrites `"image"` to `"Image"` in the model JSON; run it once and commit the result.
    Client code that compared `dataType === 'image'` must compare `'Image'`. In this repository only
    `CodeCoverage.Account.AvatarUrl` used it.
