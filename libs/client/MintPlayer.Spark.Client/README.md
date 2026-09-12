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

**This client cannot yet answer a retry prompt, and it does not cover every endpoint.** Both are being
built (see [What is not here yet](#what-is-not-here-yet)). Today it covers the CRUD, query, action,
metadata and authentication surface — which is enough for most tests, and is what 19 of this
repository's own 28 end-to-end classes already use — but a flow that hits a confirmation prompt stops
there.

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
| `ExecuteQueryAsync(query, skip, take, search, parentId, parentType, sortColumns)` | `POST /spark/queries/execute` |
| `GetQueryAsync(query)` / `ListQueriesAsync()` | `POST /spark/queries/get`, `GET /spark/queries` |
| `ExecuteActionAsync(type, name, parent, selectedItemIds, …)` | `POST /spark/actions/execute` |
| `ListEntityTypesAsync()` / `ListAliasesAsync()` | `GET /spark/types`, `GET /spark/aliases` |
| `GetPermissionsAsync(type)` | `GET /spark/permissions/{type}` |
| `SendAsync(method, url, content, requiresAntiforgery)` | anything not yet typed |

Every type and query argument accepts **either a Guid or an alias** — `"cars"` and
`"a20e8400-…"` resolve to the same thing. Ids need no escaping: they travel in a JSON body, so a Raven
id's slashes arrive intact.

⚠️ **`sortColumns` is still a string here**, in the old `"Name:asc,RegisteredAt:desc"` form, and is the
one place that encoding survives. The wire takes a typed array; this parameter is published API on a
shipped package, so changing its type is a separate decision from moving the route. The client parses
it for you — a typed overload belongs with the column-filtering work that motivated the change, where
there will be a second thing to express.

### Reading a retry prompt

`ExecuteActionAsync` returns a `SparkActionResult` rather than throwing when the server asks a question:

```csharp
var result = await client.ExecuteActionAsync(carTypeId, "DeleteCar", parent: car);

if (result.IsRetry)
{
    Console.WriteLine(result.Retry!.Title);       // "Delete car"
    Console.WriteLine(result.Retry.Options);      // ["Delete", "Cancel"]
}
```

⚠️ **You can read the prompt but not answer it yet** — see below. To drive a retry flow today, use
`SendAsync` and construct the resubmission by hand; the shape is in the
[HTTP API Specification](../../../docs/Spark-API-Specification.md#retry-action-protocol-449-status-code).

---

## What is not here yet

Tracked in [`docs/spark_client_conversation_plan.md`](../../../docs/spark_client_conversation_plan.md).
Listed because a missing feature you know about is a design constraint, and one you discover mid-suite
is a rewrite.

| Gap | Consequence today |
|---|---|
| **Cannot answer a retry** (M3) | No `ContinueAsync`. A multi-step confirmation flow needs raw `SendAsync`. |
| **Client operations are discarded** (M4) | `notify`, `navigate`, `refreshAttribute` are dropped; the envelope's `result` is all you get. |
| **Missing endpoints** (M5) | No typed `refresh`, `new`, `delete-row`, `lookupref/*`, `types/{id}`, `actions/list`, `program-units`, `culture`, `translations`. |
| **No culture or timezone headers** (M6) | `HttpClient` sends neither, and the server falls back silently — so a test asserting culture- or timezone-dependent output is asserting the **fallback**, not your setting. |

The server side is complete: every hook that can prompt does, on every endpoint, including reads.
The remaining work is all on this side of the wire.

---

## Errors

Non-success statuses throw `SparkClientException`, carrying `StatusCode` and the response body.

⚠️ **A `404` is not evidence that something does not exist.** Spark answers unknown-type,
denied-type, unreadable-body and genuinely-missing **identically**, on purpose: a distinguishable
"no such thing" is an oracle for enumerating what exists (security audit M-3). A test asserting
`404` is asserting *refusal*, and must not conclude *absence* from it.

`GetPersistentObjectAsync` and `GetQueryAsync` return `null` on `404` rather than throwing, since
"not there, or not yours" is an ordinary answer for a read.

---

## Related

- **[HTTP API Specification](../../../docs/Spark-API-Specification.md)** — the protocol this client speaks
- **[Testing Harness](../../testing/MintPlayer.Spark.Testing/README.md)** — embedded RavenDB, in-process host factory, `Wire` request builders
- **[Manager & Retry Actions](../../../docs/guide-manager-retry-actions.md)** — writing the hooks that prompt
