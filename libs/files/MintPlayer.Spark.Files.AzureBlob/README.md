# MintPlayer.Spark.Files.AzureBlob

Keeps the bytes of Spark's file and image attributes in an Azure Blob Storage container instead of RavenDB
attachments.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddFiles(files => files.UseAzureBlob(builder.Configuration.GetConnectionString("Files")!, "files"));
});
```

- **Keep the container private.** Spark serves files only through its download endpoint, and that endpoint runs the
  read path first.
- **Direct downloads.** After that check, Spark redirects to a read-only SAS on the one blob. It is valid for 60 s
  (`Spark:Files:DownloadUrlLifetime`), or 5 min for images embedded in read responses (`EmbeddedUrlLifetime`), and it
  overrides the response's type, disposition and `Cache-Control: private, no-store`. Revoking a right takes effect only
  once the URLs already issued expire. Set `o => o.DirectDownloads = false` to proxy the bytes through Spark.
- **Direct uploads.** With a shared-key connection string, the browser uploads straight to the container. It gets a
  SAS URL that can only create and write that one blob, and only until the upload window ends.
  - The storage account needs CORS for the app's origin: `PUT`, plus the header `x-ms-blob-type`.
  - Set `o => o.DirectUploads = false`, or use a client that can't sign, to send bytes through Spark instead.
- **Size caps.** A SAS can't limit a blob's size, so Spark enforces the cap when it reads the bytes back at commit.
  A larger file is refused and deleted.
- **Sharing a container.** Use `o => o.Prefix = "myapp/"` when several apps share one container.
- **The store is outside the database,** so a save cannot commit the file and the entity together. Files copies a
  claimed upload before the commit and deletes after it. An hourly sweep removes whatever a crash in between left
  behind.

See [the file attributes guide](../../../docs/guide-file-attributes.md).
