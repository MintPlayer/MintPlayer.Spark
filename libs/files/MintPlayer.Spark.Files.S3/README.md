# MintPlayer.Spark.Files.S3

Keeps the bytes of Spark's file and image attributes in an S3-compatible bucket instead of RavenDB attachments. It
works with AWS S3, Garage, Ceph RGW, Cloudflare R2 and others.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddFiles(files => files.UseS3(builder.Configuration.GetSection("Files:S3")));
});
```

```json
"Files": { "S3": { "ServiceUrl": "http://garage:3900", "PublicUrl": "https://s3.example.com", "Region": "garage", "Bucket": "myapp-files" } }
```

Keep `AccessKey` and `SecretKey` in user secrets or the environment, never in `appsettings.json`.

- **Keep the bucket private, with a key scoped to it.** Spark serves files only through its download endpoint, and
  that endpoint runs the read path first.
- **Direct downloads.** After that check, Spark redirects to a presigned GET on the one key, signed for `PublicUrl`
  when it is set. It is valid for 60 s (`Spark:Files:DownloadUrlLifetime`), or 5 min for images embedded in read
  responses (`EmbeddedUrlLifetime`), and it overrides the response's type, disposition and
  `Cache-Control: private, no-store`. Revoking a right takes effect only once the URLs already issued expire. Set
  `DirectDownloads = false` to proxy the bytes through Spark.
- **Direct uploads are size-capped.** The browser posts straight to the bucket with a presigned POST policy, which
  allows one key, a `content-length-range` up to the attribute's cap, and the upload window. A presigned PUT is
  never issued, because it can't limit size.
  - The bucket needs CORS for the app's origin (`POST`).
  - Set `PublicUrl` when browsers reach the server under another name than the app does.
  - Set `DirectUploads = false` to send bytes through Spark instead.
- **Path-style addressing** is the default when `ServiceUrl` is set, as most self-hosted servers need. The client
  sends only the checksums an operation requires, because servers differ in which ones they accept.
- **Sharing a bucket.** Use `Prefix = "myapp/"` when several apps share one bucket.
- **The store is outside the database,** so a save cannot commit the file and the entity together. Files copies a
  claimed upload before the commit and deletes after it. An hourly sweep removes whatever a crash in between left
  behind.

See [the file attributes guide](../../../docs/guide-file-attributes.md).
