# MintPlayer.Spark HTTP API Specification

Reference for every HTTP endpoint the Spark framework exposes. All endpoints live under the `/spark/*` top-level prefix. Mutating endpoints (POST/PUT/DELETE) require the anti-forgery token; read endpoints are anonymous unless noted.

> **Forward-compatibility note:** the response envelope is evolving. See [`docs/PRD-ClientOperations.md`](./PRD-ClientOperations.md) — once that PRD's endpoint-wiring milestone lands, every action-endpoint response will be wrapped in `{ result, operations }` (operations being an array of typed side-effects like `navigate`, `notify`, `refreshQuery`, `retry`, etc.). This document describes the current shape; the wrapping will be transparent to consumers that use the typed `MintPlayer.Spark.Client` SDK.

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

**`POST /spark/po/{objectTypeId}`** — `Endpoints/PersistentObject/Create.cs`

- **Route params**: `{objectTypeId}` (entity type id or alias)
- **Request body**: `PersistentObjectRequest` — see DTOs below
- **Response shapes**:
  - `201 Created` — body: the created `PersistentObject` (server-assigned `id`, `etag`)
  - `400 Bad Request` — `{ "errors": [...] }` on validation failure
  - `404 Not Found` — `{ "error": "Entity type '{objectTypeId}' not found" }`
  - `449` (retry) — see [Retry Action Protocol](#retry-action-protocol-449-status-code)
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check via `IPermissionService`
- **Notes**:
  - `objectTypeId` on the PO body is forced to the route parameter.
  - Retry resubmissions carry `retryResults[]` in the body.

#### Get PersistentObject

**`GET /spark/po/{objectTypeId}/{**id}`** — `Endpoints/PersistentObject/Get.cs`

- **Route params**: `{objectTypeId}`, `{**id}` (catch-all; supports hierarchical IDs; URI-decoded)
- **Request body**: none
- **Response shapes**:
  - `200 OK` — body: `PersistentObject`
  - `404 Not Found` — `{ "error": "Object with ID ... not found" }` or entity-type-not-found
  - `401` / `403` on auth failure
- **Auth**: permission check on read access

#### List PersistentObjects

**`GET /spark/po/{objectTypeId}`** — `Endpoints/PersistentObject/List.cs`

- **Route params**: `{objectTypeId}`
- **Response shapes**:
  - `200 OK` — body: `PersistentObject[]` (row-level authorization applies; denied rows silently omitted)
  - `404 Not Found` on unknown entity type
  - `401` / `403` on auth failure
- **Auth**: permission check on read access; no pagination (use Query endpoints for paged/sorted listings)

#### Update PersistentObject

**`PUT /spark/po/{objectTypeId}/{**id}`** — `Endpoints/PersistentObject/Update.cs`

- **Route params**: `{objectTypeId}`, `{**id}`
- **Request body**: `PersistentObjectRequest`
- **Response shapes**:
  - `200 OK` — body: updated `PersistentObject` (new `etag`)
  - `400 Bad Request` — `{ "errors": [...] }` on validation failure
  - `404 Not Found` — object or entity type not found
  - `409 Conflict` — `{ "error": "..." }` on etag mismatch (optimistic-concurrency)
  - `449` on retry
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check on edit access
- **Notes**:
  - `id` and `objectTypeId` forced to URL parameter values.
  - `etag` on the PO body is the optimistic-concurrency token; omit to skip the check.

#### Delete PersistentObject

**`DELETE /spark/po/{objectTypeId}/{**id}`** — `Endpoints/PersistentObject/Delete.cs`

- **Route params**: `{objectTypeId}`, `{**id}`
- **Request body**: optional (present only on retry resubmissions); when present, `PersistentObjectRequest` with just the `retryResults[]` populated
- **Response shapes**:
  - `204 No Content` — empty body
  - `404 Not Found` — object or entity type not found
  - `449` on retry
  - `401` / `403` on auth failure
- **Auth**: XSRF-TOKEN required; permission check on delete access
- **Notes**: body-presence is detected via `Content-Type: application/json`, not `Content-Length` — this handles chunked transfer-encoding correctly when retry resubmissions need to carry state on DELETE.

---

### Query Endpoints

#### List Queries

**`GET /spark/queries/`** — `Endpoints/Queries/List.cs`

- **Response**: `200 OK` — body: `SparkQuery[]` (filtered to queries the caller has `Query` permission on)
- **Auth**: filters by permission; queries without an `EntityType` are hidden.

#### Get Query

**`GET /spark/queries/{id}`** — `Endpoints/Queries/Get.cs`

- **Route params**: `{id}` — Guid or alias
- **Response shapes**:
  - `200 OK` — body: `SparkQuery`
  - `404 Not Found` — `{ "error": "Query '{id}' not found" }` (also returned when caller is unauthorized, to avoid leaking existence)

#### Execute Query

**`GET /spark/queries/{id}/execute`** — `Endpoints/Queries/Execute.cs`

- **Route params**: `{id}` — Guid or alias
- **Query params**:
  - `sortColumns` — comma-separated `property:direction` pairs (e.g. `name:asc,age:desc`). Direction defaults to `asc`. Server allowlists sort properties against the query's declared `SortColumns` plus the entity's attribute set.
  - `skip` — default `0`
  - `take` — default `50`
  - `search` — passed to the query's search handler if declared
  - `parentId` + `parentType` — scoped-query context (requires both; 404 if parent not resolvable/authorized)
- **Response shapes**:
  - `200 OK` — query result (shape depends on query; typically `{ items, totalCount, ... }`)
  - `400 Bad Request` — unknown sort columns
  - `404 Not Found` — query or parent not found
  - `401` / `403` on auth failure
- **Notes**:
  - Sort-column allow-listing prevents reflection-based side-channel info leaks.
  - Parent-not-found returns `404`, not silent empty results, to prevent data leakage.

#### Stream Query (WebSocket)

**`GET /spark/queries/{id}/stream`** — `Endpoints/Queries/StreamExecuteQuery.cs`

- **Route params**: `{id}` — Guid or alias
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

**`GET /spark/actions/{objectTypeId}`** — `Endpoints/Actions/ListCustomActions.cs`

- **Route params**: `{objectTypeId}`
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

**`POST /spark/actions/{objectTypeId}/{actionName}`** — `Endpoints/Actions/ExecuteCustomAction.cs`

- **Route params**: `{objectTypeId}`, `{actionName}`
- **Request body**: `CustomActionRequest` — `{ parent?, selectedItems?, retryResults? }`
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

#### Get Entity Type

**`GET /spark/types/{id}`** — `Endpoints/EntityTypes/Get.cs`

- **Route params**: `{id}` (entity type id)
- **Response shapes**:
  - `200 OK` — body: `EntityTypeDefinition`
  - `404 Not Found` — either not registered, or caller is unauthorized (existence hidden)

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

- **Response**: `200 OK` — body: `ProgramUnitsConfiguration` (navigation tree of Program Unit Groups and their Program Units, filtered by permission).

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
  "useProjection": bool,
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

### Retry round-trip on DELETE

**Request (initial)**:
```http
DELETE /spark/po/ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a/persons/12345 HTTP/1.1
X-XSRF-TOKEN: AbCd1234...
```

**Response (`449`)**:
```json
{
  "type": "retry-action",
  "step": 0,
  "title": "Delete Person",
  "message": "This person has 5 related orders. Delete them as well?",
  "options": ["Cancel", "Delete All"],
  "defaultOption": "Cancel",
  "persistentObject": null
}
```

**Request (resubmission)**:
```http
DELETE /spark/po/ca18ba09-6e1e-4d00-8ed3-0a0011de2f3a/persons/12345 HTTP/1.1
X-XSRF-TOKEN: AbCd1234...
Content-Type: application/json

{ "retryResults": [ { "option": "Delete All", "step": 0, "persistentObject": null } ] }
```

**Response**: `204 No Content` (empty body)
