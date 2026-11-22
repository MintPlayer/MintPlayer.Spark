# MintPlayer.Spark.Files.FileSystem

Keeps the bytes of Spark's file and image attributes in a local directory instead of RavenDB attachments.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddFiles(files => files.UseFileSystem("/data/files"));
});
```

- **Use a private directory**, such as a mounted volume, never the web root. Spark serves files only through its
  download endpoint, and that endpoint runs the read path first.
- **Keys are made by the server.** Each one is checked against a strict pattern, and the resolved path must stay
  inside the root. A client's file name never becomes a path.
- **Writes are atomic.** Bytes go to a temporary file in the target directory, which is then moved into place.
- **Bytes go through Spark.** There is no direct upload, so Spark caps every upload while it streams.
- **The store is outside the database,** so a save cannot commit the file and the entity together. Files copies a
  claimed upload before the commit and deletes after it. An hourly sweep (`Spark:Files:ReconcileInterval`) removes
  whatever a crash in between left behind.

See [the file attributes guide](../../../docs/guide-file-attributes.md).
