# MintPlayer.Spark.Files

File and image attributes for MintPlayer.Spark: `BinaryFile` and `Image`.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddFiles();
});

public class Car
{
    public List<SparkImage> Photos { get; set; } = [];     // Image, a list
    public SparkFile? RegistrationDocument { get; set; }   // BinaryFile
}
```

A `BinaryFile` accepts nothing until its model JSON declares what it accepts:

```json
{ "name": "RegistrationDocument", "dataType": "BinaryFile", "fileOptions": { "accept": [".pdf"], "maxBytes": 5000000 } }
```

How it works:

- **Upload first, then save.** The browser asks `/spark/files/prepare` for a slot per file, posts the bytes, and
  commits them (`/spark/files/commit`). The commit sniffs the content, checks the size, runs your
  `IFileUploadHandler`s and scans. The save then carries the upload token, which is single-use and bound to the user,
  the type, the attribute and the object.
- **Fail closed.** Types are recognised by their magic bytes, never by the client's name or MIME type. Markup (HTML,
  SVG, XML) is refused. `*` is refused. An unusual format needs `files.AddSignature(…)`.
- **Stores.** RavenDB attachments are built in. The file-system, Azure Blob and S3 stores are separate packages.
- **Scanning.** Register an `IFileScanner` (`files.UseClamAV(…)` from `MintPlayer.Spark.Files.ClamAV`, or
  `files.UseScanner<T>()`). Only a file the scanner calls clean can be claimed and served. Infected files are deleted.
  A failed scan (a timeout, a reset, an exception) is never treated as a pass. Scanning runs during the commit by default; set
  `o.ScanMode = SparkFileScanMode.Deferred` to scan in the background, in which case the commit answers `Pending` until the verdict.
  **Outside Development the app refuses to start without a scanner** unless `Spark:Files:AllowUnscannedFiles` is set,
  and then it logs a warning on every start.
- **Images.** Register an `IImageProcessor` (`files.UseSkiaSharpImages()` from `MintPlayer.Spark.Files.Images.SkiaSharp`, or
  `files.UseNetVipsImages()` from `MintPlayer.Spark.Files.Images.NetVips`). Every uploaded image is then decoded and re-encoded:
  the pixel count is checked from the header first (`Spark:Files:MaxImagePixels`, 40 million, or `fileOptions.maxPixels`), the
  EXIF orientation is applied, metadata (GPS) is dropped, only the first frame is kept, and a GIF becomes a PNG. Variants
  (`fileOptions.variants`, default `thumb` 160 and `medium` 800) are stored as WebP beside the image. After changing the variants,
  run the app once with `--spark-regenerate-variants` (add `--force` to remake all of them):

  ```csharp
  var app = builder.Build();
  if (await app.RunSparkCommandIfRequestedAsync(args))
      return;
  ```

  That one line runs every Spark command (`--spark-help` lists them); `AddFiles` registers this one.

  Without a processor, an image is stored as uploaded, with no variants, and startup logs a warning.
- **The client.** Add `withFileAttributes()` from `@mintplayer/ng-spark/files` to `provideSpark(...)`. It renders every
  `BinaryFile` and `Image` attribute: an upload control in the form (progress, "Scanning for malware…", the server's reason
  for a refusal; a list adds, removes and reorders), the name, size and download link on the detail page (an image drawn at
  its nearest larger variant), and the name or a thumbnail in a grid. Without it, the form shows "File attributes are not
  enabled in this app." where the attribute would be.

The full guide is `docs/guide-file-attributes.md` in the repository.
