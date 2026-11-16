# Guide: stored logs

`MintPlayer.Spark.Logging` keeps what your code logs during one piece of work (a command, a job, a webhook delivery)
as a `Log` document that users can open later. It works on any Spark store: it writes through `ISparkStore`, never
RavenDB directly. Plan and decisions: `docs/code-coverage/command-reactions-PRD.md` (#660, G6–G16).

## 1. Opting in

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<AppContext>();
    spark.AddLogs();
});
```

```json
// appsettings.json: the provider is filtered like any other, under its alias "Document".
"Logging": { "Document": { "LogLevel": { "Default": "Information" } } }
```

Nothing else: the `Log` model, its `Logs` query (`Custom.Logs`) and translations ship with the library.

## 2. Writing a log

A log scope is an ordinary `ILogger` scope. Every `ILogger` that logs inside it, anywhere in the same async flow, is
captured, and the entries are saved in one write when the scope closes. `scope.Logger` is the DI logger, so the same
call still reaches the console, Seq and any other sink.

```csharp
[Inject] private readonly ILoggerScopeProvider<MyHandler> logScopes;

await using (var scope = logScopes.CreateScope())           // id "Logs/{guid}"
{
    scope.Logger.LogInformation("Started {Item}", item);
    await worker.RunAsync();                               // its own ILogger<Worker> lands here too
}
```

- **Only entries inside a scope are stored.** Outside one, the `Document` provider ignores them.
- **Nested scopes:** an entry goes to the innermost open log scope only.
- **Exceptions:** pass them as the exception argument, `LogError(ex, "…")`. They are stored in `Entries[].Exception`,
  apart from the text, so an application can hide them (section 4). An exception formatted into the message is visible
  to every reader.
- **Work still running after the scope closed** (a fire-and-forget task) is dropped, not filed under an outer log.
- **A failed save** is logged as an error and swallowed: a log never fails the work it describes.
- ⚠️ **Open and close the scope in the same method**, as `using` does. Like every `BeginScope`, the scope lives in an
  `AsyncLocal`, and one opened inside a helper `async` method is gone when that method returns.

### A log with a fixed id

Work that runs in several steps (a retry, a final step later) reaches the same log through a key:

```csharp
await using (var scope = logScopes.CreateScope(
    "commands/github/123/456",                             // stored as "Logs/commands/github/123/456"
    relatedPersistentObjectId: invocation.Id,              // who may read it: see section 3
    expiresAtUtc: DateTime.UtcNow.AddDays(90)))
    scope.Logger.LogInformation("Received");

// A one-liner for a single entry:
await logScopes.LogAsync("commands/github/123/456", LogLevel.Information, "Done");
```

- A key is a feature prefix and at least one more segment, `[A-Za-z0-9_.:-]` separated by `/`, at most 200 characters.
  Anything else throws `ArgumentException` (`LogIds.ForKey`).
- Reopening appends. The related record and expiry are kept unless a later scope sets them. Concurrent writers to one
  key all land: the save uses the stored version and retries on a conflict.
- `expiresAtUtc` sets the store's expiration (`IExpiring`), so the log is deleted with no job of yours.

## 3. Who reads a log

The shipped `security.json` grants **nobody** anything: a log can hold exception text. The application opts in:

```json
{ "key": "…", "resource": "Read/Log", "groupId": "<everyone or a narrower group>" }
```

The shipped `LogActions` then gates every log by its record:

| The log | Readable by |
|---|---|
| has a `RelatedPersistentObjectId` | whoever may read that record, through its type right and its row rules; nobody if the record is gone |
| has none | whoever holds `Read/Log` |

⚠️ `Read` implies `Query` in Spark, so `Read/Log` also opens the `Logs` list. Its rows pass the same gate, so the list
only ever shows logs the caller could open one by one.

## 4. Narrowing it in your application

Derive from the library's class under the same name (see
[Overriding a library's actions class](guide-library-layers.md#overriding-a-librarys-actions-class)), and call `base`:

```csharp
public partial class LogActions : MintPlayer.Spark.Logging.Actions.LogActions
{
    [Inject] private readonly IHttpContextAccessor http;

    // The list for administrators only; detail loads keep the record-based rule.
    public override async Task<bool> IsAllowedAsync(string action, Log entity)
        => await base.IsAllowedAsync(action, entity)
            && (action != "Query" || http.HttpContext!.User.IsInRole("Administrators"));

    // Exceptions only for callers who may change the record.
    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Log entity)
        => …; // ["Entries.Exception"] for the others: an AsDetail column is named by its dotted path
}
```
