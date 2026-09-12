# MintPlayer.Spark HTTP API Specification

Reference for every HTTP endpoint the Spark framework exposes. All endpoints live under the `/spark/*` top-level prefix.

## Two things to know before reading the table

**Every persistent-object, query and action path is literal, and every such call is a `POST`.** There are no route variables: the entity type, the object id, paging, sorting and filters all travel in a JSON body. A Raven id contains slashes and nothing validates the character set of a type name or alias, so a scheme that kept either in the path was collision-free only by convention. The one exception is the WebSocket streaming route, whose handshake has no body to move an id into.

**A top-level `objectTypeId` is not the same field as `persistentObject.objectTypeId`.** The first is the request parameter: the server resolves it, authorizes against it, and it is read in exactly one place. The second is part of the submitted document — the object declaring what it is — and is overwritten with the resolved type on the way in. A payload naming a different type changes nothing and buys no access. They now sit one line apart in the same document, which is why the distinction is stated here rather than left to be inferred.

**Anti-forgery:** the mutating endpoints require the `X-XSRF-TOKEN` header. The four reads (`po/load`, `queries/get`, `queries/execute`, `actions/list`) do **not**, despite being POSTs — the verb changed, what they do did not, and an anti-forgery token protects against a cross-site request causing a *change*.

**Response envelope:** mutating endpoints answer `{ result, operations }`, where `operations` is an array of typed side-effects (`navigate`, `notify`, `refreshQuery`, `retry`, …). See [`docs/prd/PRD-ClientOperations.md`](./prd/PRD-ClientOperations.md). The reads answer a bare object; only their `449` is enveloped, because a retry has nowhere else to live.

## Route Prefix Mapping

All endpoints are hierarchically organized under a single top-level prefix and group-based sub-prefixes:

| Group | Base Path | Full Route Prefix |
|-------|-----------|-------------------|
| **SparkGroup** (root) | `/spark` | `/spark` |
| **PersistentObjectGroup** | `/po` | `/spark/po` |
| **QueriesGroup** | `/queries` | `/spark/queries` |
| **ActionsGroup** | `/actions` | `/spark/actions` |
| **EntityTypesGroup** | `/types` | `/spark/types` |
| **LookupReferencesGroup** | `/lookupref` | `/spark/lookupref` |

Routes that declare `IMemberOf<SparkGroup>` directly append their Path to `/spark` without a group prefix (e.g., `GetAliases` at `/spark/aliases`).

---

## Endpoint Reference

### Health Check

**HealthCheck** — `MintPlayer.Spark/Endpoints/HealthCheck.cs`

- **Method**: `GET`
- **Route**: `/spark/`
- **Request**: none
- **Response**: `200 OK`, plain text `"Spark Middleware is active!"`
- **Auth**: anonymous

---

### PersistentObject Endpoints

#### Create PersistentObject

**`POST /spark/po/create`** — `Endpoints/PersistentObject/Create.cs`

- **Request body**: `{ objectTypeId, persistentObject, retryResults? }`
- **Response shapes**:
  - `201 Created` — the created `PersistentObject` (server-assigned `id`, `etag`)
  - `400 Bad Request` — `{ "errors": [...] }` on validation failure
  - `404 Not Found` — unknown type, denied type, or unreadable body (all identical by design — M-3)
  - `449` (retry) — see [Retry Action Protocol](#retry-action-protocol-449-status-code)
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check via `IPermissionService`
- **Notes**:
  - `persistentObject.objectTypeId` is **overwritten** with the resolved type. It is never the authority.
  - `POST` is Create and never Edit: `id` is forced to null. A client posting an existing id used to flip the operation to Edit and overwrite a foreign record under the `New` right.
  - ⚠️ The body is read **before** authorization, because the body is where the type is. A malformed body is answered exactly as an unknown type, so a parse failure cannot be used to probe which types exist.

#### Load PersistentObject

**`POST /spark/po/load`** — `Endpoints/PersistentObject/Get.cs`

- **Request body**: `{ objectTypeId, id, retryResults? }`
- **Response shapes**:
  - `200 OK` — a bare `PersistentObject` (**not** enveloped)
  - `404 Not Found` — object unknown, type unknown, or denied — indistinguishable by design
  - `449` on retry, enveloped
  - `401` on auth failure where signing in could help
- **Auth**: **no** anti-forgery token; permission check on read access
- **Notes**:
  - `OnLoadAsync` can raise a retry — this is why the endpoint has a body at all. ⚠️ It is the load *seam*, not just this endpoint's hook: delete, refresh and delete-row all run it on their way elsewhere, so a retry there fires on every read of that type.

#### Update PersistentObject

**`POST /spark/po/update`** — `Endpoints/PersistentObject/Update.cs`

- **Request body**: `{ objectTypeId, id, persistentObject, retryResults? }`
- **Response shapes**:
  - `200 OK` — updated `PersistentObject` (new `etag`)
  - `400 Bad Request` — `{ "errors": [...] }` on validation failure
  - `404 Not Found` — object or type not found, or denied
  - `409 Conflict` — `{ "error": "Concurrency conflict" }` on etag mismatch. Deliberately generic: the server-side change vector is a version side channel.
  - `449` on retry
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check on edit access
- **Notes**:
  - The top-level `id` names the target; `persistentObject.id` and `.objectTypeId` are both overwritten from what the server loaded.
  - `etag` on the PO body is the optimistic-concurrency token; omit to skip the check.

#### New PersistentObject (construct, unsaved)

**`POST /spark/po/new`** — `Endpoints/PersistentObject/New.cs`

- **Request body**: `{ objectTypeId, asDetailAttribute?, parentType?, parentId?, parameters?, retryResults? }`
- **Response shapes**:
  - `200 OK` — the constructed, unsaved `PersistentObject`, enveloped
  - `400 Bad Request` — `{ "errors": [...] }` when `OnNewAsync` refuses
  - `404 Not Found` — unknown type, or an `asDetailAttribute` the parent does not have
  - `449` on retry
- **Auth**: XSRF-TOKEN required
- **Notes**: writes nothing. For an AsDetail row the parent still owns the save. A present `parentId` is re-loaded server-side rather than trusted; an absent one means the hook is handed a null parent, which is the honest answer.

#### Refresh PersistentObject

**`POST /spark/po/refresh`** — `Endpoints/PersistentObject/Refresh.cs`

- **Request body**: `{ objectTypeId, persistentObject, triggeredBy, retryResults? }`
  - `triggeredBy` is the attribute's **name**. For a trigger inside a detail grid it is the path
    form the inline validation errors use: `Jobs[1].ProfessionId`.
- **Response shapes**:
  - `200 OK` — body: the reshaped `PersistentObject`, wrapped in the client-operation envelope
  - `404 Not Found` — entity type unknown, or a row the caller may not read
  - `449` on retry
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check is `New` when the submitted object has no `id`,
  `Read` when it has one. For an existing row the load applies row security and attribute redaction.
- **Notes**:
  - **Writes nothing.** It answers "what should this form look like now", and is called far more
    often than any other endpoint here — potentially on every field blur.
  - The server rebuilds the object's metadata from the model and accepts only `value` and
    `isValueChanged` off the wire, so a client cannot assert that an attribute is optional, visible
    or writable when the model says otherwise.
  - A `triggeredBy` naming a detail-grid column dispatches to that **row's** type — a change to
    `CarreerJob.ProfessionId` reaches `CarreerJobActions.OnRefreshAsync` — and the response is the
    reshaped row, carrying its owner as `parent`. Authorization still uses the **request's**
    `objectTypeId`: nested AsDetail types are not in `security.json`.
  - See [TriggersRefresh & OnRefreshAsync](./guide-triggers-refresh.md).

#### Delete PersistentObject

**`POST /spark/po/delete`** — `Endpoints/PersistentObject/Delete.cs`

- **Request body**: `{ objectTypeId, id, retryResults? }`
- **Response shapes**:
  - `204 No Content` — empty body
  - `400 Bad Request` — `{ "errors": [...] }` when a hook refuses for a business reason
  - `404 Not Found` — object or type not found, or denied
  - `449` on retry
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check on delete access
- **Notes**: a delete always carries a body now. It used to be a `DELETE` that attached one *only* once there were retry answers to send, with the server sniffing `Content-Type` to decide whether to read it; that conditional went with the verb.

#### Delete AsDetail Row

**`POST /spark/po/delete-row`** — `Endpoints/PersistentObject/DeleteRow.cs`

- **Request body**: `{ objectTypeId, asDetailAttribute, parentType, parentId, rowKey, parameters?, retryResults? }`
- **Response shapes**:
  - `200 OK` — `{ removed: true }`, enveloped
  - `400 Bad Request` — `{ "errors": [...] }` when `OnDeleteRowAsync` refuses ("this invoice line has already been settled")
  - `404 Not Found` — unknown parent, attribute or row key
  - `449` on retry
- **Auth**: XSRF-TOKEN required
- **Notes**: **deletes nothing.** A `200` means the caller may splice the row out of the collection it is editing; the removal is persisted with the parent. The endpoint never reads a row from the body — a client asserting the row's state is simply ignored, because the refusal exists to doubt exactly that claim.

---

### Query Endpoints

#### List Queries

**`GET /spark/queries/`** — `Endpoints/Queries/List.cs`

- **Response**: `200 OK` — body: `SparkQuery[]` (filtered to queries the caller has `Query` permission on)
- **Auth**: filters by permission; queries without an `EntityType` are hidden.

#### Get Query

**`POST /spark/queries/get`** — `Endpoints/Queries/Get.cs`

- **Request body**: `{ queryId }` — Guid or alias
- **Response shapes**:
  - `200 OK` — body: `SparkQuery` (bare, not enveloped)
  - `404 Not Found` — `{ "error": "Query '{id}' not found" }` (also returned when the caller is unauthorized, and when the body is unreadable, to avoid leaking existence)
- **Auth**: **no** anti-forgery token — this is a read.

#### Execute Query

**`POST /spark/queries/execute`** — `Endpoints/Queries/Execute.cs`

- **Request body**:
  - `queryId` — Guid or alias
  - `sortColumns` — an **array** of `{ property, direction }`, direction `"asc"` / `"desc"`. Server allow-lists each property against the query's declared `SortColumns` plus the entity's attribute set.
  - `skip` — default `0`; negatives clamp to `0`
  - `take` — default `50`; clamped to `[1, 1000]`
  - `search` — passed to the query's search handler if declared
  - `parentId` + `parentType` — scoped-query context (requires both; `404` if the parent is not resolvable or not authorized)
  - `retryResults?` — `OnQueryAsync` can prompt
- **Response shapes**:
  - `200 OK` — query result (bare): `{ columns, items, totalItems, skip, take }`
  - `400 Bad Request` — unknown sort columns
  - `404 Not Found` — query or parent not found
  - `449` on retry, enveloped
  - `401` / `403` on auth failure
- **Auth**: **no** anti-forgery token — this is a read.
- **Notes**:
  - ⚠️ `sortColumns` was `prop:asc,other:desc` in a query string — an encoding invented because a query string has no arrays. It is a real array now and the string form is gone, not kept as an alias. Column filtering (several columns, a multi-selection each) is what the shape exists to accommodate next.
  - Sort-column allow-listing prevents reflection-based side-channel info leaks: without it a caller could order by any public property on the projection type, including fields never exposed as attributes.
  - Authorization runs **before** the sort parse, or the parser answers questions on the caller's behalf — an unauthorized caller could otherwise enumerate attribute names by watching 400-vs-404.
  - Parent-not-found returns `404`, not silent empty results, to prevent data leakage.

#### Stream Query (WebSocket)

**`GET /spark/queries/{id}/stream`** — `Endpoints/Queries/StreamExecuteQuery.cs`

- **Route params**: `{id}` — Guid or alias. ⚠️ **The only route variable left in Spark**, and the only one that cannot be removed: a WebSocket handshake has no body to move an id into. It collides with nothing — its siblings are the single-segment literals `/get` and `/execute`, and this route needs the `/stream` suffix to match at all.
- **Protocol**: WebSocket upgrade (`Connection: Upgrade`)
- **WebSocket messages** (JSON, camelCase):
  - `StreamingMessage` — snapshot or diff patch with items/ops
  - `ErrorMessage { message }` — on auth failure or invalid use
- **Non-matching requests**:
  - `400 Bad Request` if the request isn't a WebSocket upgrade
  - `400 Bad Request` if the query is not marked `IsStreamingQuery`
  - `404 Not Found` if the query doesn't exist
- **Close semantics**: `NormalClosure` on stream end; `InternalServerError` on auth failure (with an error message preceding). Client disconnects are handled silently.

---

### Custom Action Endpoints

#### List Custom Actions

**`POST /spark/actions/list`** — `Endpoints/Actions/ListCustomActions.cs`

- **Request body**: `{ objectTypeId }`
- **Auth**: **no** anti-forgery token — this is a read. Answers `200` with the **empty list** for an unknown or denied type, never a refusal: the client shell asks it for every type it renders, so refusing would bounce an anonymous visitor to sign-in merely for opening a page.
- **Response**: `200 OK` — body: array of action-metadata objects:
  ```json
  [
    {
      "name": "CarCopy",
      "displayName": "Copy",
      "icon": "Copy",
      "description": "Creates a copy of the selected car",
      "showedOn": "query",
      "selectionRule": "=1",
      "refreshOnCompleted": true,
      "confirmationMessageKey": "AreYouSure",
      "offset": 0
    }
  ]
  ```
- **Filtering**: only actions whose security-json resource `{ActionName}/{EntityTypeName}` is authorized AND whose C# implementation is registered via the source generator.
- **Sort**: by `offset` (ascending).

#### Execute Custom Action

**`POST /spark/actions/execute`** — `Endpoints/Actions/ExecuteCustomAction.cs`

- **Request body**: `CustomActionRequest` — `{ objectTypeId, actionName, parent?, selectedItemIds?, parentId?, parentType?, queryId?, retryResults? }`
- **Response shapes**:
  - `200 OK` — empty (or action-specific)
  - `404 Not Found` — entity type or action not registered
  - `449` on retry (see protocol)
  - `500 Internal Server Error` — `{ "error": "..." }` from unhandled action exception (logged server-side)
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check via `IPermissionService.EnsureAuthorizedAsync({actionName}, {EntityTypeName})`

---

### Entity Type Endpoints

#### List Entity Types

**`GET /spark/types/`** — `Endpoints/EntityTypes/List.cs`

- **Response**: `200 OK` — body: `EntityTypeDefinition[]` (filtered by `Query` permission per type)
- **Note**: this is the metadata clients render *from*, including on detail pages — a type missing here renders blank, not merely unlisted. It stays list-scoped (`Query`); a `Read`-only type reaches it because **`Read` implies `Query`** (see [Authorization](./guide-authorization.md)).

#### Get Entity Type

**`GET /spark/types/{id}`** — `Endpoints/EntityTypes/Get.cs`

- **Route params**: `{id}` (entity type id)
- **Response shapes**:
  - `200 OK` — body: `EntityTypeDefinition`
  - `404 Not Found` — either not registered, or caller is unauthorized (existence hidden)
- **Auth**: `Query` permission on the type (a `Read` grant satisfies it through the implication)

---

### Lookup Reference Endpoints

#### List Lookup References

**`GET /spark/lookupref/`** — `Endpoints/LookupReferences/List.cs`

- **Response**: `200 OK` — body: array of lookup-reference objects
- **Auth**: anonymous

#### Get Lookup Reference

**`GET /spark/lookupref/{name}`** — `Endpoints/LookupReferences/Get.cs`

- **Route params**: `{name}`
- **Response shapes**:
  - `200 OK` — body: lookup reference
  - `404 Not Found` — `{ "error": "LookupReference '{name}' not found" }`
- **Auth**: anonymous

#### Add Lookup Reference Value

**`POST /spark/lookupref/{name}`** — `Endpoints/LookupReferences/AddValue.cs`

- **Request body**: `LookupReferenceValueDto` — `{ key, values: TranslatedString, isActive, extra? }`
- **Response**: `201 Created` — body: updated lookup reference
- **Auth**: XSRF-TOKEN required

#### Update Lookup Reference Value

**`PUT /spark/lookupref/{name}/{key}`** — `Endpoints/LookupReferences/UpdateValue.cs`

- **Request body**: `LookupReferenceValueDto`
- **Response**: `200 OK` — updated lookup reference
- **Auth**: XSRF-TOKEN required

#### Delete Lookup Reference Value

**`DELETE /spark/lookupref/{name}/{key}`** — `Endpoints/LookupReferences/DeleteValue.cs`

- **Response**: `204 No Content`
- **Auth**: XSRF-TOKEN required

---

### Miscellaneous Endpoints

#### Get Aliases

**`GET /spark/aliases`** — `Endpoints/Aliases/GetAliases.cs`

- **Response**: `200 OK` — body:
  ```json
  {
    "entityTypes": { "<guid>": "alias", ... },
    "queries":     { "<guid>": "alias", ... }
  }
  ```
- **Filtering**: only aliases the caller has `Query` permission on are included.

#### Get Culture

**`GET /spark/culture`** — `Endpoints/Culture/Get.cs`

- **Response**: `200 OK` — body: `CultureConfiguration`
- **Auth**: anonymous

#### Get Translations

**`GET /spark/translations`** — `Endpoints/Translations/Get.cs`

- **Response**: `200 OK` — body: all localized strings
- **Auth**: anonymous

#### Get Permissions

**`GET /spark/permissions/{entityTypeId}`** — `Endpoints/Permissions/GetPermissions.cs`

- **Route params**: `{entityTypeId}`
- **Response shapes**:
  - `200 OK` — body:
    ```json
    { "canRead": bool, "canCreate": bool, "canEdit": bool, "canDelete": bool }
    ```
  - `404 Not Found` — unknown entity type

#### Get Program Units

**`GET /spark/program-units`** — `Endpoints/ProgramUnits/Get.cs`

- **Response**: `200 OK` — body: `ProgramUnitsConfiguration` (navigation tree of Program Unit Groups and their Program Units, filtered per caller).
- **Filtering** — by the right the unit's click will demand: `query` units require the `Query` right on the target's entity type, `persistentObject` units require `Read`, `url` units are always visible. Groups whose units all filtered away are dropped. Fail-closed: a typed unit whose target can't be resolved is hidden.
- **Unit shape** — `type` is canonicalized by the loader to exactly `query` / `persistentObject` / `url`; a `persistentObject` unit may carry `objectId` (deep link to one object — for a model-only type, the composed page served by the type's name-resolved Actions class via `OnLoadAsync(id, parent)`; see `guide-program-units.md`); a `url` unit carries `url`.

---

## Global Specifications

### CSRF / Anti-Forgery

Double-submit token pattern.

- **Token generation** — every response emits an `XSRF-TOKEN` cookie (`HttpOnly=false`, `SameSite=Strict`, `Secure` when HTTPS).
- **Token validation** — on mutating requests (POST/PUT/PATCH/DELETE), the client must send the token in the `X-XSRF-TOKEN` header; the server checks that header value matches the cookie value.
- **Configuration** — `services.AddAntiforgery(opt => opt.HeaderName = "X-XSRF-TOKEN")`.
- **JSON-body support** — a custom middleware runs before ASP.NET Core's built-in anti-forgery middleware so XSRF checks work on JSON bodies (built-in only validates form-encoded bodies).
- **Enforcement** — endpoints opt in via `RequireAntiforgeryTokenAttribute(true)` on their `Configure(builder)` method.
- **Failure mode** — `400 Bad Request` when the token is missing or invalid.
- **Affected endpoints** — Create/Update/Delete on PersistentObject; Execute on CustomAction; Add/Update/Delete on LookupReference values.

### Retry Action Protocol (449 Status Code)

Action methods can prompt the user for confirmation/input mid-execution.

**Server → client (`HTTP 449`)**:
```json
{
  "type": "retry-action",
  "step": 0,
  "title": "Delete Car",
  "message": "Type the license plate to confirm deletion of ABC-123.",
  "options": ["Delete", "Cancel"],
  "defaultOption": "Cancel",
  "persistentObject": { /* optional scaffold PO for a form */ }
}
```

**Client → server (resubmission of the original request)**:
```json
{
  "persistentObject": { /* original body, possibly edited */ },
  "retryResults": [
    { "option": "Delete", "step": 0, "persistentObject": null }
  ]
}
```

Multiple sequential prompts accumulate — the client echoes back all prior answers on each resubmission. When every prompt has been answered, the endpoint returns the normal success response.

Subsumption under the Client Operations PRD: once implemented, the 449 response shape becomes one element of the unified `{ result, operations }` envelope (with `operations[0].type == "retry"`). User-visible behavior is unchanged; only the server-side JSON builder is different.

### Authorization Model

`IPermissionService` checks permissions per resource:

- **Read**, **Create** (`New`), **Edit**, **Delete** — standard CRUD permissions per entity type.
- **Query** — can the caller execute a query returning this entity type.
- **Custom actions** — permission strings formatted as `{ActionName}/{EntityTypeName}`.

**Failure modes**:

- Unauthenticated → `401 Unauthorized`
- Authenticated but unauthorized → `403 Forbidden`
- Some endpoints return `404 Not Found` instead of `403` to hide resource existence (currently: `GetEntityType`, `GetQuery`).

**List/read filtering**: authorized-but-denied items are either silently omitted (`ListEntityTypes`, `ListQueries`, `ListPersistentObjects`) or `404`-hidden (`GetEntityType`, `GetQuery`).

---

## Core DTOs

### `PersistentObject`

```csharp
{
  "id": string?,                 // null on create; server-assigned
  "name": string,                // required
  "objectTypeId": Guid,          // server forces to route param on mutations
  "breadcrumb": string?,
  "etag": string?,               // optimistic-concurrency token (RavenDB change vector)
  "attributes": PersistentObjectAttribute[]
}
```

### `PersistentObjectAttribute`

```csharp
{
  "id": string?,
  "name": string,
  "label": TranslatedString?,
  "value": object?,              // typed per dataType
  "dataType": string,            // "string" | "number" | "boolean" | "date" | "AsDetail" | ...
  "isArray": bool,
  "isRequired": bool,
  "isVisible": bool,
  "isReadOnly": bool,
  "isValueChanged": bool,        // change tracking
  "order": int,
  "query": string?,              // for Reference attributes — the query that backs selection
  "breadcrumb": string?,
  "showedOn": int,               // flags enum: 0=None | 1=Query | 2=PersistentObject | 3=Both
  "rules": ValidationRule[],
  "group": Guid?,                // attribute grouping
  "renderer": string?,           // custom frontend renderer
  "rendererOptions": object?
}
```

Detail-type attributes (`dataType == "AsDetail"`) carry nested `PersistentObject`s in `object` (single) or `objects` (array) fields.

### `PersistentObjectRequest` (mutation wrapper)

```csharp
{
  "persistentObject": PersistentObject,
  "retryResults": RetryResult[]?
}
```

### `RetryResult`

```csharp
{
  "option": string,              // button label the user clicked
  "step": int,                   // 0-based
  "persistentObject": PersistentObject?
}
```

### `CustomActionRequest`

```csharp
{
  "parent": PersistentObject?,           // context PO
  "selectedItems": PersistentObject[],   // selected rows from a query (can be empty)
  "retryResults": RetryResult[]?
}
```

### `LookupReferenceValueDto`

```csharp
{
  "key": string,
  "values": { "<culture>": "<localized text>" },   // TranslatedString
  "isActive": bool,
  "extra": { "<key>": any }?
}
```

### `SparkQuery` (metadata)

```csharp
{
  "id": Guid,
  "name": string,
  "description": TranslatedString?,
  "source": string,                       // "Database.PropertyName" | "Custom.MethodName"
  "alias": string?,
  "sortColumns": SortColumn[],
  "renderMode": string,                   // SparkQueryRenderMode enum
  "indexName": string?,
  "entityType": string?,                  // result entity type name
  "isStreamingQuery": bool
}
```

### Error response shape

Most errors follow:

```json
{ "error": "Human-readable message" }
```

Validation errors (`400`) follow:

```json
{ "errors": [ { "property": "Name", "message": "Field is required" } ] }
```

Concurrency conflicts (`409`) follow the standard error shape with a descriptive message.

---

## Notable Cross-Cutting Behaviors

1. **Hierarchical IDs** — PersistentObject IDs can contain slashes (e.g., `company/123/department/456`). The `{**id}` catch-all route supports them; the server URI-decodes before database lookup.
2. **Optimistic concurrency** — Update operations honor the `etag` field (RavenDB change vector). A stale etag returns `409 Conflict`.
3. **Sort-column allow-listing** — Query execute allow-lists sort properties against the query's declared `SortColumns` plus the entity's attribute set, preventing reflection-based side-channel enumeration.
4. **Permission leakage mitigation** — `GetQuery` and `GetEntityType` return `404` for unauthorized access rather than `403` to avoid leaking resource existence.
5. **WebSocket streaming** — Queries marked `IsStreamingQuery: true` can be opened as WebSockets at `/spark/queries/{id}/stream`. The server emits snapshot + incremental diff patches for bandwidth-efficient real-time views.
6. **Anti-forgery on JSON bodies** — Spark's custom middleware validates XSRF tokens on JSON bodies (not just form-encoded), patching a gap in ASP.NET Core's built-in middleware.
7. **Retry state** — request-scoped `RetryAccessor.AnsweredResults` holds the `retryResults[]` from the current request body, so action handlers can inspect prior prompt answers without re-parsing.
8. **Content-Type over Content-Length** — the DELETE endpoint detects body presence via `Content-Type: application/json`, which correctly handles chunked transfer-encoding (retry resubmissions on DELETE).

---

## Worked Examples

### Create a PersistentObject

**Request**:
```http
POST /spark/po/ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a HTTP/1.1
X-XSRF-TOKEN: AbCd1234...
Content-Type: application/json

{
  "persistentObject": {
    "name": "John Doe",
    "objectTypeId": "ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a",
    "attributes": [
      { "name": "email", "value": "john@example.com", "dataType": "string" }
    ]
  }
}
```

**Response (`201 Created`)**:
```json
{
  "id": "persons/12345",
  "name": "John Doe",
  "objectTypeId": "ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a",
  "etag": "A:1-abcdef123456",
  "attributes": [
    { "id": "attr/001", "name": "email", "value": "john@example.com", "dataType": "string" }
  ]
}
```

### Execute a Query with sort override

**Request**:
```http
GET /spark/queries/e5c2f1a8-9f0d-4b2e-8a1f-3c0b5d9a2e1c/execute?skip=0&take=10&sortColumns=name:asc,age:desc HTTP/1.1
```

**Response (`200 OK`)**:
```json
{
  "items": [
    { "id": "persons/1", "name": "Alice", "age": 30 },
    { "id": "persons/2", "name": "Bob", "age": 28 }
  ],
  "totalCount": 2
}
```

### Retry round-trip on a delete

**Request (initial)**:
```http
POST /spark/po/delete HTTP/1.1
X-XSRF-TOKEN: AbCd1234...
Content-Type: application/json

{ "objectTypeId": "ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a", "id": "persons/12345" }
```

**Response (`449`)** — the prompt is an *operation inside the envelope*, not the body itself:
```json
{
  "result": null,
  "operations": [
    {
      "type": "retry",
      "step": 0,
      "title": "Delete Person",
      "message": "This person has 5 related orders. Delete them as well?",
      "options": ["Cancel", "Delete All"],
      "defaultOption": "Cancel",
      "persistentObject": null
    }
  ]
}
```

**Request (resubmission)** — same endpoint, same target, plus the answer:
```http
POST /spark/po/delete HTTP/1.1
X-XSRF-TOKEN: AbCd1234...
Content-Type: application/json

{
  "objectTypeId": "ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a",
  "id": "persons/12345",
  "retryResults": [ { "option": "Delete All", "step": 0, "persistentObject": null } ]
}
```

**Response**: `204 No Content` (empty body)

⚠️ **Answers accumulate.** A second prompt is resubmitted with **both** entries in `retryResults`; the
server re-runs the hook from the top and keys on `step`. A hook must therefore check whether its own
step has already been answered before prompting again, or it prompts forever — the Angular client
resubmits with no depth limit.
