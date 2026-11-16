# MintPlayer.Spark.Logging

Stored logs for MintPlayer.Spark. What your code logs through any `ILogger` while a **log scope** is open is also kept in
one `Log` document per scope, written in one save when the scope closes. It works on any Spark store.

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark.AddLogs());

await using (var scope = logScopes.CreateScope("commands/github/123/456", relatedPersistentObjectId: commandId))
{
    scope.Logger.LogInformation("Merging #{Number}", number);   // console, Seq, … and the Log document
    try { … } catch (Exception ex) { scope.Logger.LogError(ex, "Merge failed"); }
}
```

- `scope.Logger` is your application's own `ILogger`; any logger deeper in the same async flow is captured too.
- Filter what is stored under `Logging:Document:LogLevel`, like any provider.
- A log that belongs to a record (`relatedPersistentObjectId`) is readable exactly when that record is, in the `Logs`
  list and on its own page alike. Nobody may read logs until your `security.json` grants `Read/Log`.

See [docs/guide-logging.md](https://github.com/MintPlayer/MintPlayer.Spark/blob/master/docs/guide-logging.md).
