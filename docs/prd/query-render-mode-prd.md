# Query RenderMode, Virtual Scrolling & Multi-Column Sorting - PRD

## 1. Problem Statement

Currently, all Spark queries are rendered using a `<bs-datatable>` with client-side pagination. The component loads **all** results from the server into memory, then slices pages client-side. This has three limitations:

1. **No virtual scrolling option** - For large datasets, users may prefer a continuous scrolling experience over clicking through pages. Virtual scrolling renders only visible rows and fetches data on demand, providing a smoother UX for large lists.

2. **Single-column sorting only** - The datatable's `columnHeaderClicked()` method replaces the sort column on every click. There is no way to sort by multiple columns (e.g., sort by Department, then by LastName within each department).

3. **Client-side search/filter/sort** - All filtering and sorting happens in the browser on the full result set. This must move to server-side for both render modes.

---

## 2. Current Architecture

### 2.1 Backend

- **`SparkQuery` model** (`MintPlayer.Spark.Abstractions/SparkQuery.cs`):
  - `SortBy` (string?) — single sort property
  - `SortDirection` (string, default "asc") — single direction
  - No render mode property

- **`ExecuteQuery` endpoint** (`MintPlayer.Spark/Endpoints/Queries/Execute.cs`):
  - Reads `sortBy` and `sortDirection` from query string (single values)
  - Returns **all** results as `IEnumerable<PersistentObject>` — no skip/take

- **`QueryExecutor.ApplySorting()`** (`MintPlayer.Spark/Services/QueryExecutor.cs`):
  - Applies a single `OrderBy`/`OrderByDescending` via reflection
  - No `ThenBy` chaining for secondary sorts

### 2.2 Frontend

- **`SparkQuery` TS model** (`node_packages/ng-spark/src/lib/models/spark-query.ts`):
  - `sortBy?` (string), `sortDirection` (string) — single sort

- **`SparkQueryListComponent`** (`node_packages/ng-spark/src/lib/components/query-list/`):
  - Calls `sparkService.executeQuery()` which returns `PersistentObject[]` (full array)
  - Builds `PaginationResponse<T>` client-side from the full array
  - Client-side search filtering and pagination
  - On sort change: re-fetches all data from server, then re-slices client-side
  - **Layout**: Uses `<bs-container>` + `<div class="container-fluid">` with `d-flex justify-content-between` for title/actions row. No flexbox column layout for the overall page — the datatable is a natural flow element.

- **`SparkSubQueryComponent`** (`node_packages/ng-spark/src/lib/components/sub-query/`):
  - Renders in a `<bs-card>` with `<bs-table>` — no pagination, no sorting, no search
  - Loads all items via `executeQuery()` and renders them directly

- **`SparkService.executeQuery()`** (`node_packages/ng-spark/src/lib/services/spark.service.ts`):
  - Sends `sortBy`, `sortDirection` as query params — single values
  - Returns `PersistentObject[]` (not paginated)

### 2.3 `@mintplayer/ng-bootstrap` Datatable & Virtual Datatable

- **`BsDatatableComponent`** (`@mintplayer/ng-bootstrap/datatable`):
  - Extends `DatatableSortBase` (shared multi-column sort logic)
  - `DatatableSettings`: has `sortColumns: SortColumn[]` (multi-column sort — **already migrated** from single-column)
  - Footer: per-page selector (`bs-pagination`) + page number navigation (`bs-pagination`) using `float-start`/`float-end`
  - Data fed via `BsRowTemplateDirective` with `PaginationResponse<T>`

- **`BsVirtualDatatableComponent`** (`@mintplayer/ng-bootstrap/virtual-datatable`) — **already built** in commit `fdcf9d5`:
  - Extends same `DatatableSortBase` — shared sorting logic
  - Separate header table (`<bs-table>` with `<thead>` only) + CDK `<cdk-virtual-scroll-viewport>` body
  - `dataSource` input: `VirtualDatatableDataSource<T>` (CDK `DataSource<T>` with page caching)
  - `itemSize` input: pixel height per row (default 48)
  - Row template via `BsVirtualRowTemplateDirective` (`*bsVirtualRowTemplate`)
  - **Flexbox layout**: container is `display: flex; flex-direction: column; height: 100%`, header `flex-shrink: 0`, viewport `flex: 1 1 auto; min-height: 0`
  - `table-layout: fixed` on both header and body tables to sync column widths

- **`VirtualDatatableDataSource<T>`** (`@mintplayer/ng-bootstrap/virtual-datatable`):
  - Constructor: `fetchFn: (skip, take) => Promise<{ data: T[]; totalRecords: number }>`, `pageSize = 50`
  - Page-based caching (`Map<number, T[]>`)
  - Observes CDK viewport `viewChange` events, maps to page indices, fetches uncached pages in parallel
  - Fills unloaded pages with empty slots to maintain virtual scroll positioning
  - `reset()` method clears cache (call when sort/search changes)

- **`@mintplayer/pagination`** package:
  - `PaginationRequest`: `perPage`, `page`, `sortColumns: SortColumn[]`
  - `PaginationResponse<T>`: `data[]`, `totalRecords`, `totalPages`, `perPage`, `page`
  - `SortColumn`: `{ property: string, direction: 'ascending' | 'descending' }`

- **Demo page layout** (`apps/ng-bootstrap-demo/.../datatables.component.scss`):
  ```scss
  :host {
    display: flex;
    flex-direction: column;
    height: 100%;
  }
  ```
  Title/controls get `flex-shrink-0`, datatable/virtual-datatable gets `flex-grow-1`. For virtual datatable, also `overflow-hidden` to contain the CDK viewport.

---

## 3. Design Decisions

| Decision | Rationale |
|----------|-----------|
| **No backward compatibility** for `SortBy`/`SortDirection` — replace entirely with `SortColumns` | `SortColumns` is a strict superset. No migration shim needed since this is preview-mode software. |
| **No backward compatibility** for response format — always return paginated envelope | Simplifies the API. One response shape regardless of parameters. |
| **Server-side search, sort, and filter** in both render modes | Client-side filtering doesn't scale. Server-side is consistent for both Pagination and VirtualScrolling. |
| **CSS-based row height** for virtual scrolling | No per-query configuration needed. Consumer controls row height via CSS on the datatable/viewport. |
| **Sub-queries always respect the query's `RenderMode`** | Consistent behavior. No special-casing for sub-queries. |
| **Virtual scrolling in separate entry point** (`@mintplayer/ng-bootstrap/virtual-datatable`) | Tree-shakeable. Apps that don't use virtual scrolling don't pull in CDK ScrollingModule. Already built. |
| Compact sort param format: `Property:direction,...` | Simple to construct in frontend, easy to parse on backend. |
| **Flexbox column layout** for `SparkQueryListComponent` | Mirrors the ng-bootstrap demo page layout pattern. Title/search/actions are `flex-shrink-0`, the datatable is `flex-grow-1`. Required for virtual scrolling viewport to fill available height. |

---

## 4. Proposed Changes

### 4.1 RenderMode on SparkQuery

Add a `RenderMode` property to `SparkQuery` that controls how query results are displayed.

#### C# Model

```csharp
public enum SparkQueryRenderMode
{
    Pagination,
    VirtualScrolling
}

public sealed class SparkQuery
{
    // ... existing properties ...

    /// <summary>
    /// Controls how query results are rendered in the UI.
    /// </summary>
    public SparkQueryRenderMode RenderMode { get; set; } = SparkQueryRenderMode.Pagination;
}
```

#### TypeScript Model (`spark-query.ts`)

```typescript
export type SparkQueryRenderMode = 'Pagination' | 'VirtualScrolling';

export interface SparkQuery {
  // ... existing properties ...
  renderMode?: SparkQueryRenderMode;  // default: 'Pagination'
}
```

### 4.2 Multi-Column Sorting

#### 4.2a Backend — `SparkQuery` Model

Remove `SortBy`/`SortDirection` entirely. Replace with `SortColumns`:

```csharp
public sealed class SparkQuery
{
    // ... existing properties (Id, Name, Description, Source, Alias, IndexName, UseProjection, EntityType) ...

    /// <summary>
    /// Multi-column sort specification.
    /// Each entry specifies a property name and direction ("asc"/"desc").
    /// Applied in order: first entry = primary sort, subsequent = tiebreakers.
    /// </summary>
    public SortColumn[] SortColumns { get; set; } = [];

    public SparkQueryRenderMode RenderMode { get; set; } = SparkQueryRenderMode.Pagination;
}

public sealed class SortColumn
{
    public required string Property { get; set; }
    public string Direction { get; set; } = "asc";
}
```

#### 4.2b Backend — Execute Endpoint

Accept multi-column sort from query string using compact format:

```
GET /spark/queries/{id}/execute?sortColumns=Department:asc,LastName:desc&skip=0&take=50&search=smith
```

Parsing logic:
```csharp
var sortColumnsParam = httpContext.Request.Query["sortColumns"].FirstOrDefault();
SortColumn[]? sortOverrides = null;
if (!string.IsNullOrEmpty(sortColumnsParam))
{
    sortOverrides = sortColumnsParam.Split(',')
        .Select(part =>
        {
            var segments = part.Split(':');
            return new SortColumn
            {
                Property = segments[0],
                Direction = segments.Length > 1 ? segments[1] : "asc"
            };
        })
        .ToArray();
}
```

#### 4.2c Backend — `QueryExecutor.ApplySorting()`

Replace the current single-sort method with chained `OrderBy` → `ThenBy`:

```csharp
private object ApplySorting(object queryable, Type entityType, SortColumn[] sortColumns)
{
    for (int i = 0; i < sortColumns.Length; i++)
    {
        var col = sortColumns[i];
        var propertyInfo = entityType.GetProperty(col.Property, BindingFlags.Public | BindingFlags.Instance);
        if (propertyInfo == null) continue;

        var isDescending = string.Equals(col.Direction, "desc", StringComparison.OrdinalIgnoreCase);
        var methodName = i == 0
            ? (isDescending ? "OrderByDescending" : "OrderBy")
            : (isDescending ? "ThenByDescending" : "ThenBy");

        var parameter = Expression.Parameter(entityType, "x");
        var propertyAccess = Expression.Property(parameter, propertyInfo);
        var lambda = Expression.Lambda(propertyAccess, parameter);

        var orderMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == methodName && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType, propertyInfo.PropertyType);

        queryable = orderMethod.Invoke(null, [queryable, lambda])!;
    }
    return queryable;
}
```

#### 4.2d Frontend — `DatatableSettings` (already migrated)

The `@mintplayer/ng-bootstrap` datatable already uses `SortColumn[]` on `DatatableSettings` (migrated in the `sortColumns` refactor). The `SparkQueryListComponent` currently still reads `sortProperty`/`sortDirection` from settings — this must be updated to use `sortColumns`.

#### 4.2e Frontend — `SparkService`

Update `executeQuery()` to send multi-column sort + pagination + search:

```typescript
async executeQuery(
  queryId: string,
  options?: {
    sortColumns?: SortColumn[];
    parentId?: string;
    parentType?: string;
    skip?: number;
    take?: number;
    search?: string;
  }
): Promise<QueryResult> {
  let params = new HttpParams();
  if (options?.sortColumns?.length) {
    params = params.set('sortColumns',
      options.sortColumns.map(c => `${c.property}:${c.direction === 'descending' ? 'desc' : 'asc'}`).join(',')
    );
  }
  if (options?.parentId) params = params.set('parentId', options.parentId);
  if (options?.parentType) params = params.set('parentType', options.parentType);
  if (options?.skip != null) params = params.set('skip', options.skip);
  if (options?.take != null) params = params.set('take', options.take);
  if (options?.search) params = params.set('search', options.search);

  return firstValueFrom(this.http.get<QueryResult>(
    `${this.baseUrl}/queries/${encodeURIComponent(queryId)}/execute`,
    { params }
  ));
}
```

### 4.3 Server-Side Pagination & Search

All data fetching is now server-side. Both Pagination and VirtualScrolling modes use the same API — they differ only in how the frontend requests data.

#### 4.3a Backend — Execute Endpoint

The endpoint always returns a paginated envelope:

```
GET /spark/queries/{id}/execute?skip=0&take=50&sortColumns=LastName:asc&search=smith
```

Response:
```json
{
  "data": [ { "id": "...", "name": "...", "attributes": [...] } ],
  "totalRecords": 1234,
  "skip": 0,
  "take": 50
}
```

New query parameters:
- `skip` (int, default 0) — records to skip
- `take` (int, default 50) — records to return
- `search` (string, optional) — server-side text search across visible attributes
- `sortColumns` (string, optional) — multi-column sort override

#### 4.3b Backend — `QueryExecutor`

Add skip/take/search to the query pipeline:

```csharp
public sealed class QueryResult
{
    public required IEnumerable<PersistentObject> Data { get; set; }
    public required int TotalRecords { get; set; }
    public required int Skip { get; set; }
    public required int Take { get; set; }
}

public interface IQueryExecutor
{
    Task<QueryResult> ExecuteQueryAsync(
        SparkQuery query,
        PersistentObject? parent = null,
        int skip = 0,
        int take = 50,
        string? search = null);
}
```

Implementation approach for search:
- For **Database queries**: Apply RavenDB full-text `Search()` on string properties before skip/take
- For **Custom queries**: Apply in-memory LINQ `Where()` filtering on materialized `PersistentObject` attributes (search runs against attribute values/breadcrumbs)

For total count: use RavenDB `Statistics()` which provides count alongside query execution without an extra round-trip.

#### 4.3c Frontend — `QueryResult` Model

```typescript
export interface QueryResult {
  data: PersistentObject[];
  totalRecords: number;
  skip: number;
  take: number;
}
```

### 4.4 Frontend Integration — Flexbox Layout & RenderMode Switching

#### 4.4a `SparkQueryListComponent` Flexbox Layout

Adopt the same flexbox column pattern used by the ng-bootstrap demo page (`apps/ng-bootstrap-demo/.../datatables.component.scss`). The component uses `display: flex; flex-direction: column` so that the title/actions bar and search box are fixed-size (`flex-shrink: 0`) while the datatable fills the remaining vertical space (`flex-grow: 1`).

**SCSS** (`spark-query-list.component.scss`):
```scss
:host {
  display: flex;
  flex-direction: column;
  height: 100%;
}

tr:hover {
  background-color: rgba(0, 0, 0, 0.05);
}

td input[type="checkbox"]:disabled {
  opacity: 1;
  pointer-events: none;
}
```

**Template structure** — the overall container uses flexbox classes to arrange the top bar, search, and datatable vertically:

```html
<div class="d-flex flex-column h-100">
  <!-- Title + Actions row — fixed height -->
  <div class="d-flex justify-content-between align-items-center mb-4 flex-shrink-0">
    <h2>{{ title }}</h2>
    <div class="d-flex gap-2">
      <!-- Extra actions slot -->
      <!-- + New button -->
    </div>
  </div>

  <!-- Search row — fixed height -->
  <div class="flex-shrink-0">
    <!-- bs-form + bs-grid search box -->
  </div>

  <!-- Datatable — fills remaining space -->
  @if (query()?.renderMode === 'VirtualScrolling') {
    <bs-virtual-datatable class="flex-grow-1 overflow-hidden" ...>
      <!-- columns + virtual row template -->
    </bs-virtual-datatable>
  } @else {
    <bs-datatable class="flex-grow-1" ...>
      <!-- columns + row template -->
    </bs-datatable>
  }
</div>
```

Key flexbox properties:
- **Container**: `d-flex flex-column h-100` — vertical stack filling parent height
- **Title/Actions row**: `flex-shrink-0` — natural height, doesn't shrink
- **Search row**: `flex-shrink-0` — natural height, doesn't shrink
- **Virtual datatable**: `flex-grow-1 overflow-hidden` — takes remaining space, contains the CDK viewport
- **Regular datatable**: `flex-grow-1` — takes remaining space (pagination footer is inside the table)

This mirrors the ng-bootstrap demo pattern where the `BsVirtualDatatableComponent` internally uses the same flexbox column layout for its header table + scroll viewport.

#### 4.4b `SparkQueryListComponent` — Pagination Mode

Requests a specific page:
```typescript
async loadItems(): Promise<void> {
  const result = await this.sparkService.executeQuery(this.query()!.id, {
    sortColumns: this.settings.sortColumns,
    skip: (this.settings.page.selected - 1) * this.settings.perPage.selected,
    take: this.settings.perPage.selected,
    search: this.searchTerm || undefined,
  });
  this.paginationData.set({
    data: result.data,
    totalRecords: result.totalRecords,
    totalPages: Math.ceil(result.totalRecords / this.settings.perPage.selected),
    perPage: this.settings.perPage.selected,
    page: this.settings.page.selected,
  });
  this.settings.page.values = Array.from(
    { length: Math.ceil(result.totalRecords / this.settings.perPage.selected) || 1 },
    (_, i) => i + 1
  );
}
```

#### 4.4c `SparkQueryListComponent` — Virtual Scrolling Mode

Sets up a `VirtualDatatableDataSource`:
```typescript
this.virtualDataSource = new VirtualDatatableDataSource<PersistentObject>(
  (skip, take) => this.sparkService.executeQuery(this.query()!.id, {
    sortColumns: this.settings.sortColumns,
    skip, take,
    search: this.searchTerm || undefined,
  }).then(r => ({ data: r.data, totalRecords: r.totalRecords }))
);
```

When sort or search changes, call `this.virtualDataSource.reset()` to clear the page cache and re-fetch.

#### 4.4d Template — RenderMode Branching

```html
@if (query()?.renderMode === 'VirtualScrolling') {
  <bs-virtual-datatable class="flex-grow-1 overflow-hidden"
      [(settings)]="settings"
      [dataSource]="virtualDataSource"
      (settingsChange)="onSettingsChange()">
    @for (attr of visibleAttributes(); track attr.id) {
      <div *bsDatatableColumn="attr.name; sortable: true">
        {{ (attr.label | resolveTranslation) || attr.name }}
      </div>
    }

    <ng-template bsVirtualRowTemplate let-item>
      @for (attr of visibleAttributes(); track attr.id; let first = $first) {
        <td>
          @if (first && canRead() && item) {
            <a [routerLink]="['/po', entityType()!.alias || entityType()!.id, item.id]"
               (click)="rowClicked.emit(item)">
              <ng-container *ngTemplateOutlet="cellContent; context: { $implicit: item, attr: attr }"></ng-container>
            </a>
          } @else if (item) {
            <ng-container *ngTemplateOutlet="cellContent; context: { $implicit: item, attr: attr }"></ng-container>
          } @else {
            &nbsp;
          }
        </td>
      }
    </ng-template>
  </bs-virtual-datatable>
} @else {
  <bs-datatable class="flex-grow-1" [(settings)]="settings" (settingsChange)="onSettingsChange()">
    @for (attr of visibleAttributes(); track attr.id) {
      <div *bsDatatableColumn="attr.name; sortable: true">
        {{ (attr.label | resolveTranslation) || attr.name }}
      </div>
    }

    <tr *bsRowTemplate="let item of paginationData()">
      @for (attr of visibleAttributes(); track attr.id; let first = $first) {
        <td>
          @if (first && canRead()) {
            <a [routerLink]="['/po', entityType()!.alias || entityType()!.id, item.id]"
               (click)="rowClicked.emit(item)">
              <ng-container *ngTemplateOutlet="cellContent; context: { $implicit: item, attr: attr }"></ng-container>
            </a>
          } @else {
            <ng-container *ngTemplateOutlet="cellContent; context: { $implicit: item, attr: attr }"></ng-container>
          }
        </td>
      }
    </tr>
  </bs-datatable>
}
```

Note: The virtual row template uses `bsVirtualRowTemplate` (not `bsRowTemplate`) and handles `null` items (placeholder slots for unloaded pages) by rendering `&nbsp;` cells.

### 4.5 `SparkSubQueryComponent`

Sub-queries respect the query's `RenderMode`. The component branches identically:
- **Pagination**: uses `<bs-datatable>` with server-side pagination (replaces current `<bs-table>` with no pagination)
- **VirtualScrolling**: uses `<bs-virtual-datatable>` with the same data source pattern

The sub-query card layout also adopts flexbox for the card body when using virtual scrolling, so the viewport fills the card's available height.

---

## 5. Implementation Plan

### Phase 1: Server-Side Pagination, Search & Multi-Column Sorting

This phase changes the data flow from client-side to server-side for both existing pagination mode and prepares for virtual scrolling.

1. **Backend `SortColumn` model** — Add `SortColumn` class to `MintPlayer.Spark.Abstractions`
2. **Backend `SparkQuery`** — Replace `SortBy`/`SortDirection` with `SortColumns[]`, add `RenderMode`
3. **Backend `QueryResult`** — New response envelope class with `Data`, `TotalRecords`, `Skip`, `Take`
4. **Backend `QueryExecutor`** — Rewrite `ApplySorting()` for multi-column, add `ApplySkip()`/`ApplyTake()`, add search filtering, return `QueryResult`
5. **Backend `ExecuteQuery` endpoint** — Parse `sortColumns`, `skip`, `take`, `search` params; always return `QueryResult` envelope
6. **Update existing query JSON files** — Migrate `sortBy`/`sortDirection` → `sortColumns` in all `App_Data/Model/*.json` inline queries
7. **Frontend `QueryResult` model** — Add `QueryResult` interface to ng-spark models
8. **Frontend `SparkQuery` model** — Replace `sortBy`/`sortDirection` with `sortColumns`, add `renderMode`
9. **Frontend `SparkService.executeQuery()`** — New signature with options object, sends `sortColumns`/`skip`/`take`/`search` params, returns `QueryResult`
10. **Frontend `SparkQueryListComponent`** — Remove client-side `allItems`/`applyFilter()`, use server-side pagination and search, adopt flexbox column layout
11. **Frontend `SparkSubQueryComponent`** — Add pagination via `<bs-datatable>` (replacing current plain `<bs-table>`)

### Phase 2: Virtual Scrolling

Depends on `@mintplayer/ng-bootstrap/virtual-datatable` which is **already published** in v21.10.0.

1. **Frontend `SparkQueryListComponent`** — Branch on `renderMode`, use `<bs-virtual-datatable>` for VirtualScrolling mode with `VirtualDatatableDataSource`, add `overflow-hidden` class
2. **Frontend `SparkSubQueryComponent`** — Branch on `renderMode`, use `<bs-virtual-datatable>` for VirtualScrolling mode
3. **Add `@angular/cdk` dependency** to `ng-spark` package (needed for `VirtualDatatableDataSource` import)

---

## 6. Data Model Changes Summary

### `SparkQuery` (C#) — Final Shape

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `Id` | `Guid` | required | Existing |
| `Name` | `string` | required | Existing |
| `Description` | `TranslatedString?` | `null` | Existing |
| `Source` | `string` | required | Existing |
| `Alias` | `string?` | `null` | Existing |
| `IndexName` | `string?` | `null` | Existing |
| `UseProjection` | `bool` | `false` | Existing |
| `EntityType` | `string?` | `null` | Existing |
| `SortColumns` | `SortColumn[]` | `[]` | **New.** Replaces `SortBy`/`SortDirection` |
| `RenderMode` | `SparkQueryRenderMode` | `Pagination` | **New.** Enum: `Pagination`, `VirtualScrolling` |

**Removed:** `SortBy`, `SortDirection`

### `SortColumn` (new class, in `MintPlayer.Spark.Abstractions`)

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `Property` | `string` | required | Column/property name |
| `Direction` | `string` | `"asc"` | `"asc"` or `"desc"` |

### `QueryResult` (new class, in `MintPlayer.Spark.Abstractions`)

| Property | Type | Notes |
|----------|------|-------|
| `Data` | `IEnumerable<PersistentObject>` | Page of results |
| `TotalRecords` | `int` | Total matching records |
| `Skip` | `int` | Records skipped |
| `Take` | `int` | Page size |

### `DatatableSettings` (TypeScript — already migrated in ng-bootstrap)

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `sortColumns` | `SortColumn[]` | `[]` | **Already present** in ng-bootstrap. Spark component must update to use it. |
| `perPage` | `{ values: number[], selected: number }` | `{ values: [10, 20, 50], selected: 20 }` | Existing |
| `page` | `{ values: number[], selected: number }` | `{ values: [1], selected: 1 }` | Existing |

**Already removed in ng-bootstrap:** `sortProperty`, `sortDirection`

### Execute Query API — Final Shape

```
GET /spark/queries/{id}/execute?sortColumns=Dept:asc,Name:desc&skip=0&take=50&search=smith
```

| Parameter | Type | Default | Notes |
|-----------|------|---------|-------|
| `sortColumns` | `string` | (from query definition) | Format: `Property:direction,...` |
| `skip` | `int` | `0` | Records to skip |
| `take` | `int` | `50` | Records to return |
| `search` | `string?` | `null` | Server-side text search |
| `parentId` | `string?` | `null` | Existing. Parent context for custom queries |
| `parentType` | `string?` | `null` | Existing. Parent entity type |

Response (always this shape):
```json
{
  "data": [ { "id": "...", "name": "...", "attributes": [...] } ],
  "totalRecords": 1234,
  "skip": 0,
  "take": 50
}
```

---

## 7. Migration Checklist

Since there is no backward compatibility, these items must all land together:

- [ ] All `App_Data/Model/*.json` inline queries: `sortBy`/`sortDirection` → `sortColumns` array format
- [ ] All consumers of `DatatableSettings`: update from `sortProperty`/`sortDirection` to `sortColumns` (ng-bootstrap already done, Spark component needs update)
- [ ] All consumers of `SparkService.executeQuery()`: update to new options-object signature
- [ ] All frontend code handling flat `PersistentObject[]` responses: update to `QueryResult` envelope
- [ ] Demo apps (DemoApp, HR, Fleet): update query definitions and any direct datatable usage
- [ ] `@mintplayer/ng-bootstrap` version: `21.10.0` (already includes virtual-datatable + sortColumns migration)
