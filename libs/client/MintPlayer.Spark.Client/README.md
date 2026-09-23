# MintPlayer.Spark.Client

A typed .NET client for a Spark backend. It speaks the same HTTP protocol the Angular frontend speaks,
so a test can drive a real application end-to-end without a browser.

```csharp
using var client = new SparkClient(httpClient);
await client.LoginAsync("alice@example.com", "hunter2");

var car = await client.CreatePersistentObjectAsync(new PersistentObject
{
    Name = "Car",
    Attributes = [new() { Name = "LicensePlate", Value = "1-ABC-123" }],
});

var results = await client.ExecuteQueryAsync("cars", take: 10, search: "ABC");
```

---

## ⚠️ Read this before planning a test suite around it

**It does not cover every endpoint yet** (see [What is not here yet](#what-is-not-here-yet)). Today it
covers the CRUD, query, action, metadata and authentication surface — which is enough for most tests,
and is what 19 of this repository's own 28 end-to-end classes already use. It *can* now hold a
conversation: a server that asks a question gets an answer, on every endpoint that can ask one.

**And even when it is complete, it will not replace browser tests.** That is not a scheduling
statement; it was measured. The two defects found in this repository's timezone work — a
`datetime-local` input rendering a wire timestamp as blank, and a date pipe converting in the browser —
are both **invisible** to a protocol client, because both live in rendering. A protocol client asserts
what the server sends. Only a browser asserts what a person sees.

Use it for what it is good at: everything about the server's behaviour, at a fraction of a browser
test's cost and flakiness.

---

## Setup

```csharp
// Against a running host
var http = new HttpClient { BaseAddress = new Uri("https://localhost:5001") };
using var client = new SparkClient(http);

// Against an in-process host (see MintPlayer.Spark.Testing)
await using var factory = new SparkEndpointFactory<MyContext>(store, models);
using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
```

The client manages its own cookie jar and anti-forgery token. The token is minted lazily on the first
mutating call, from `GET /spark` — you never handle `X-XSRF-TOKEN` yourself.

### Signing in

Sign-in lives in a **separate package**, `MintPlayer.Spark.Client.Authorization`, as extension methods
on `SparkClient`. That mirrors the server: authentication is optional there too, shipped as
`MintPlayer.Spark.Authorization`.

```csharp
using MintPlayer.Spark.Client.Authorization;

await client.LoginAsync(email, password);
await client.RegisterAsync(email, password);
var me = await client.GetCurrentUserAsync();
await client.LogoutAsync();
```

⚠️ **These only work if the server enabled authentication.** `/spark/auth/*` is mapped from inside
`MintPlayer.Spark.Authorization`, so an app that never called `spark.AddAuthentication<TUser>()` does
not expose those routes at all — and the client then gets a `SparkClientException` with **404**, which
looks identical to every other refusal (see [Errors](#errors)). If sign-in fails with a 404 and the
credentials are right, check the server's wiring before the credentials.

Referencing the package is not the same as the server having it. Nothing at compile time can tell you
this; the dependency runs the wrong way.

⚠️ **Both** `LoginAsync` and `LogoutAsync` invalidate the cached anti-forgery token, and login is the
one worth knowing about: the token is bound to the authenticated principal, so the anonymous one the
client already holds stops being valid for mutating calls the moment you sign in. The client re-primes
it for you. A hand-rolled client that caches a token across a sign-in gets a `400` from the
anti-forgery gate and no useful message.

---

## What it covers today

| Method | Endpoint |
|---|---|
| `GetPersistentObjectAsync(type, id)` | `POST /spark/po/load` |
| `CreatePersistentObjectAsync(obj)` | `POST /spark/po/create` |
| `UpdatePersistentObjectAsync(obj)` | `POST /spark/po/update` |
| `DeletePersistentObjectAsync(type, id)` | `POST /spark/po/delete` |
| `ExecuteQueryAsync(query, skip, take, search, parentId, parentType, sortColumns, columns)` | `POST /spark/queries/execute` |
| `GetDistinctValuesAsync(query, column, search, columns, parentId, parentType)` | `POST /spark/queries/distinct-values` |
| `GetQueryAsync(query)` / `ListQueriesAsync()` | `POST /spark/queries/get`, `GET /spark/queries` |
| `ExecuteActionAsync(type, name, parent, selectedItemIds, parentId, parentType, queryId, …)` | `POST /spark/actions/execute` |
| `ContinueAsync(result, option, persistentObject)` | the same endpoint, one answer further |
| `RefreshPersistentObjectAsync(obj, triggeredBy)` | `POST /spark/po/refresh` |
| `NewPersistentObjectAsync(type, asDetailAttribute, parentType, parentId, parameters)` | `POST /spark/po/new` |
| `DeleteRowAsync(type, asDetailAttribute, parentType, parentId, rowKey)` | `POST /spark/po/delete-row` |
| `ListEntityTypesAsync()` / `GetEntityTypeAsync(type)` | `GET /spark/types`, `GET /spark/types/{id}` |
| `ListAliasesAsync()` | `GET /spark/aliases` |
| `ListCustomActionsAsync(type)` | `POST /spark/actions/list` |
| `ListLookupReferencesAsync()` / `GetLookupReferenceAsync(name)` | `GET /spark/lookupref/`, `GET /spark/lookupref/{name}` |
| `AddLookupReferenceValueAsync` / `UpdateLookupReferenceValueAsync` / `DeleteLookupReferenceValueAsync` | `POST /spark/lookupref/{name}`, `PUT /spark/lookupref/{name}/{key}`, `DELETE /spark/lookupref/{name}/{key}` |
| `GetProgramUnitsAsync()` / `GetCultureAsync()` / `GetTranslationsAsync()` | `GET /spark/program-units`, `GET /spark/culture`, `GET /spark/translations` |
| `GetPermissionsAsync(type)` | `GET /spark/permissions/{entityTypeId}` |
| `SendAsync(method, url, content, requiresAntiforgery)` | anything not yet typed |

⚠️ **Only `create`, `update`, `delete`, `refresh`, `new`, `delete-row` and `actions/execute` are
enveloped, and only those can ask a question.** The rest return bare JSON, which is why they take no
`onRetry` — offering one would advertise a conversation the server cannot start.

⚠️ **`lookupref/{name}`, `lookupref/{name}/{key}` and `types/{id}` still carry route variables.** The
literal route table covers `/po`, `/queries` and `/actions`; these three were never part of it. The
client escapes every segment, which matters because a lookup-reference key is user data.

⚠️ **`ListCustomActionsAsync` answers with an empty list for an unknown type *and* for one you may not
see.** The endpoint refuses to tell them apart, because the difference is an existence oracle — so do
not read empty as "no such type".

### Who the viewer is

```csharp
client.TimeZoneId = "Europe/Brussels";      // → X-Spark-Timezone
client.AcceptLanguage = "nl-BE,nl;q=0.9";   // → Accept-Language
```

Both are unset by default, and **the server falls back silently** — to UTC and to the application's
default language, with no error and no log. So a test asserting timezone- or culture-dependent output
through this client is asserting the fallback until you set these, and it passes either way.

Every type and query argument accepts **either a Guid or an alias** — `"cars"` and
`"a20e8400-…"` resolve to the same thing. Ids need no escaping: they travel in a JSON body, so a Raven
id's slashes arrive intact.

⚠️ **Pass `queryId` when the action is running over a grid selection.** The server re-runs that query
narrowed to `selectedItemIds` and hands the action the rows the grid rendered; name no query and it
falls back to loading each id — a different code path, with different row filtering. The Angular grid
always sends it, so a test that omits it is asserting the path the grid never takes.

⚠️ **`sortColumns` is still a string here**, in the old `"Name:asc,RegisteredAt:desc"` form, and is the
one place that encoding survives. The wire takes a typed array; this parameter is published API on a
shipped package, so changing its type is a separate decision from moving the route. The client parses
it for you — a typed overload belongs with the column-filtering work that motivated the change, where
there will be a second thing to express.

---

## Answering a question

A hook can stop mid-request and ask something — "are you sure?", "type the plate to confirm". On the
wire that is a `449` carrying the question. **Nine endpoints can ask**, including reads: a load, a
query, a save, a delete, an action.

There are two ways to answer, and they differ only in who writes the loop.

### Hand it a handler, and the whole conversation happens inside one call

```csharp
var saved = await client.UpdatePersistentObjectAsync(car, onRetry: (prompt, ct) =>
{
    Console.WriteLine($"{prompt.Title}: {string.Join(" / ", prompt.Options)}");
    return Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Confirm"));
});
```

Set `client.RetryHandler` instead to answer every call the same way. This is what the Angular client
does — the conversation is invisible to the caller, and `saved` is the object after the questions were
settled.

Returning `null` declines: the call then fails with `SparkRetryRequiredException`, which carries the
question it would not answer.

### Or drive it yourself, one question at a time

`ExecuteActionAsync` returns a result rather than throwing, so you can look at the question before
answering it. ⚠️ This is the behaviour **when no handler applies** — if `RetryHandler` is set or
`onRetry:` is passed, the handler answers and the call returns the finished result, so `IsRetry` is
never true.

```csharp
var result = await client.ExecuteActionAsync(carTypeId, "DeleteCar", parent: car);

while (result.IsRetry)
{
    var form = result.Retry!.PersistentObject;          // the delete confirmation's own form
    form?["Confirmation"].SetValue(car.LicensePlate);   // …filled in

    result = await client.ContinueAsync(result, "Delete", form);
}
```

`ContinueAsync` **appends** the answer and sends the original request again in full. That is not a
workaround for a suspended call — it is the protocol. The server replays the hook from the top on
every attempt and feeds it the answers it has, which is why nothing has to be held open in between,
and why two conversations through one client cannot interfere: the answers live on the result, never
on the client.

### What the server says on the way — client operations

A response carries more than its result. Alongside it the server can ask the client to show a
message, repaint an attribute, re-run a query or navigate — the same operations the Angular frontend
acts on. Hand the call an `onOperation` and you see them, in emission order:

```csharp
var notices = new List<string>();

var result = await client.ExecuteActionAsync(
    "car", "SyncServiceHistory",
    selectedItemIds: ["cars/1"],
    queryId: "cars",
    onRetry: async (prompt, ct) => RetryAnswer.Choose("Overwrite"),
    onOperation: op =>
    {
        if (op is SparkNotifyOperation notify) notices.Add(notify.Message);
    });

// Or read them off the result instead of streaming them:
foreach (var refresh in result.Operations.OfType<SparkRefreshAttributeOperation>())
    SparkClientOperations.Apply(car, [refresh]);
```

⚠️ **Non-retry operations arrive *before* the prompt is answered.** A hook that says "saved 3 of 4
rows" and then asks about the fourth is emitting both in one envelope, and the notify is what explains
the question — delivering it afterwards would show the dialog first and the reason second.

⚠️ **Nothing is applied for you.** `refreshAttribute` is the one operation a headless client can act
on, and `SparkClientOperations.Apply` is explicit because this SDK keeps no registry of open objects
the way a UI does. Only the caller knows which object a patch is for. `navigate`, `refreshQuery` and
`disableAction` are surfaced and nothing more — the last is a no-op in the browser too.

⚠️ **An operation type this client has never heard of is ignored, not thrown on.** It arrives as
`SparkUnknownOperation` with its payload intact. That is the point of the contract: a newer server
must be readable by an older client, so the envelope is never bound to a closed set of types.

### Three things that bite

⚠️ **`step` comes from the server.** Never count answers locally. A hook may skip a step — asking the
second question only when the first was answered a particular way — and a local counter agrees with
the server right up until that happens, then silently answers a different question.
`ContinueAsync` and the handler loop both echo the server's.

⚠️ **An option that was not offered is refused before it is sent.** The server cannot tell an option
that never existed from one a hook stopped offering; both simply fail to match, and the hook runs its
else-branch as though you had chosen something. A typo would assert the wrong path and pass.

⚠️ **A prompt raised from `OnLoadAsync` fires on every read of that type**, and one from
`OnQueryAsync` on every execution — including the ones a grid issues while paging. A handler that
answers unconditionally is answering far more often than it looks.

`MaxRetryDepth` (default 16) bounds the conversation, so a hook that re-raises regardless of the
answer fails with a message naming the step instead of spinning.

---

## What is not here yet

Tracked in [`docs/spark_client_conversation_plan.md`](../../../docs/spark_client_conversation_plan.md).
Listed because a missing feature you know about is a design constraint, and one you discover mid-suite
is a rewrite.

| Gap | Consequence today |
|---|---|
| **No WebSocket streaming** | `/spark/queries/{id}/stream` has no typed client. Deliberate: nothing needs it, and unused public API is a liability. |
| **No external login** | Browser-dependent by construction — a redirect to a third party and back is not something a request/response client can drive. |
| **No scripting DSL** | C# API only, by decision. |

The server side is complete, and so is this one: every hook that can prompt does, on every endpoint
including reads; every endpoint has a typed method; operations are surfaced; and the viewer headers
are yours to set.

---

## CORS does not apply to you

Spark's endpoints answer `Access-Control-Allow-Origin: *` ([why](../../../docs/guide-cors.md)), which is
about **browsers** and has no bearing on this client. `HttpClient` is not subject to CORS: it sends what
you tell it and reads what comes back, cross-origin or not.

⚠️ Worth stating because the inverse trips people up: a test passing through `SparkClient` proves
nothing about whether a *browser* could make the same call. If you need that, it is a browser test.

---

## Errors

Non-success statuses throw `SparkClientException`, carrying `StatusCode` and the response body.

⚠️ **A `404` is not evidence that something does not exist.** Spark answers unknown-type,
denied-type, unreadable-body and genuinely-missing **identically**, on purpose: a distinguishable
"no such thing" is an oracle for enumerating what exists (security audit M-3). A test asserting
`404` is asserting *refusal*, and must not conclude *absence* from it.

`GetPersistentObjectAsync` and `GetQueryAsync` return `null` on `404` rather than throwing, since
"not there, or not yours" is an ordinary answer for a read.

⚠️ **`SparkRetryRequiredException` is not a failure.** It derives from `SparkClientException`, so a
`catch (SparkClientException)` still catches it — which is the right default, because a question
nobody answered is a request that did not complete. Catch the narrower type first when you want to
tell them apart; its `Prompt` is the question, and answering it means calling the method again with a
handler.

---

## Related

- **[HTTP API Specification](../../../docs/Spark-API-Specification.md)** — the protocol this client speaks
- **[Testing Harness](../../testing/MintPlayer.Spark.Testing/README.md)** — embedded RavenDB, in-process host factory, `Wire` request builders
- **[Manager & Retry Actions](../../../docs/guide-manager-retry-actions.md)** — writing the hooks that prompt
