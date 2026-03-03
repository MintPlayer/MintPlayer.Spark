# PO and Query Aliases

Spark supports URL-friendly aliases for entity types, queries, and program units. Instead of navigating by GUID (`/po/550e8400-e29b-41d4-a716-446655440000/Cars-123`), users see clean URLs like `/po/car/Cars-123`. Aliases are declared in the model JSON files and resolved automatically by both the backend API and the Angular frontend.

## Overview

Aliases work on three levels:

| Level | Source | URL pattern |
|---|---|---|
| Entity type | Auto-generated from `Name` (or optional `alias` in model JSON) | `/po/{alias}/{id}` instead of `/po/{guid}/{id}` |
| Query | Auto-generated from query name (strips `Get` prefix, lowercases) | `/query/{alias}` instead of `/query/{guid}` |
| Program unit | Explicit `alias` in `App_Data/programUnits.json` | Navigation sidebar uses alias in router links |

## Step 1: Understanding Alias Auto-Generation

### Entity Type Aliases

Entity type aliases are **auto-generated** at model load time. The framework lowercases the entity type's `Name`:

- `Car` becomes `car`
- `Company` becomes `company`
- `Person` becomes `person`

You can optionally override the auto-generated alias by adding an `alias` property to the model JSON file (e.g., `App_Data/Model/Car.json`):

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Car",
  "alias": "vehicle",
  "attributes": [...]
}
```

If no `alias` is set in the JSON, the auto-generated value is used. The demo apps rely on auto-generation and do not set explicit aliases in model JSON files.

### Query Aliases

Query aliases are also **auto-generated** by stripping a `Get` prefix and lowercasing the name:

- `GetCars` becomes `cars`
- `GetPeople` becomes `people`
- `GetCompanies` becomes `companies`

Queries are stored as individual JSON files in `App_Data/Queries/` (e.g., `GetCars.json`). You can optionally add an `alias` property to override the auto-generated value:

```json
{
  "id": "880e8400-e29b-41d4-a716-446655440000",
  "name": "GetPeople",
  "alias": "staff",
  "contextProperty": "People"
}
```

### Program Unit Alias

In `App_Data/programUnits.json`, add an `alias` to each program unit. This alias is used by the frontend's `routerLink` pipe for sidebar navigation:

```json
{
  "programUnitGroups": [
    {
      "id": "990e8400-e29b-41d4-a716-446655440000",
      "name": {"en": "Master Data"},
      "programUnits": [
        {
          "id": "990e8400-e29b-41d4-a716-446655440001",
          "name": {"en": "People"},
          "icon": "bi-people",
          "type": "query",
          "queryId": "880e8400-e29b-41d4-a716-446655440000",
          "alias": "people",
          "order": 1
        },
        {
          "id": "990e8400-e29b-41d4-a716-446655440002",
          "name": {"en": "Cars"},
          "icon": "bi-car-front-fill",
          "type": "query",
          "queryId": "bc696815-2abb-4e7c-98a1-ac86b4352105",
          "alias": "cars",
          "order": 2
        }
      ]
    }
  ]
}
```

## Step 2: Backend Resolution

The backend resolves aliases transparently. The `GET /spark/aliases` endpoint returns a map of all configured aliases:

```
GET /spark/aliases
```

**Response:**

```json
{
  "entityTypes": {
    "550e8400-e29b-41d4-a716-446655440000": "car",
    "660e8400-e29b-41d4-a716-446655440001": "company"
  },
  "queries": {
    "880e8400-e29b-41d4-a716-446655440000": "people",
    "880e8400-e29b-41d4-a716-446655440001": "companies"
  }
}
```

Existing endpoints accept both GUIDs and aliases as identifiers. For example, `GET /spark/po/car` and `GET /spark/po/550e8400-e29b-41d4-a716-446655440000` resolve to the same entity type.

## Step 3: Angular Routing

The `@mintplayer/ng-spark` library provides `sparkRoutes()` which includes alias-aware route definitions:

```typescript
import { sparkRoutes } from '@mintplayer/ng-spark';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      ...sparkRoutes()
    ]
  }
];
```

The `sparkRoutes()` function registers:

| Route | Component | Description |
|---|---|---|
| `query/:queryId` | Query list | Accepts both GUID and alias |
| `po/:type/new` | Create form | `:type` can be GUID or alias |
| `po/:type/:id/edit` | Edit form | `:type` can be GUID or alias |
| `po/:type/:id` | Detail page | `:type` can be GUID or alias |
| `po/:type` | Query list | Resolves entity type by GUID or alias, finds matching query |

### How the Frontend Resolves Aliases

The query list and detail components resolve the `:type` parameter by checking both ID and alias:

```typescript
const entityType = entityTypes.find(t => t.id === typeParam || t.alias === typeParam);
```

This means both `/po/car/Cars-123` and `/po/550e8400-.../Cars-123` route to the same detail page.

### Navigation with Aliases

The `routerLink` pipe in the sidebar automatically uses the alias when available:

```typescript
// For a query-type program unit:
['/query', unit.alias || unit.queryId]

// For a PO-type program unit:
['/po', unit.alias || unit.persistentObjectId]
```

When navigating from a query list to a detail page, the alias is preferred:

```typescript
this.router.navigate(['/po', entityType.alias || entityType.id, item.id]);
```

Reference link pipes also use aliases:

```typescript
// reference-link-route.pipe.ts
return ['/po', targetType.alias || targetType.id, referenceId];
```

## C# Model Properties

The alias is available on the corresponding C# model classes:

```csharp
// EntityTypeDefinition
public string? Alias { get; set; }

// SparkQuery
public string? Alias { get; set; }

// ProgramUnit
public string? Alias { get; set; }
```

These properties are populated at model load time. If not explicitly set in the JSON files, they are auto-generated (see Step 1).

## Complete Example

See the DemoApp for a working example:

- `Demo/DemoApp/DemoApp/App_Data/programUnits.json` -- program units with aliases (`people`, `companies`, `cars`)
- `Demo/DemoApp/DemoApp/ClientApp/src/app/app.routes.ts` -- route configuration using `sparkRoutes()`
- `MintPlayer.Spark/Endpoints/Aliases/GetAliases.cs` -- the `/spark/aliases` endpoint
- `node_packages/ng-spark/src/lib/pipes/router-link.pipe.ts` -- alias-aware navigation pipe
- `node_packages/ng-spark/src/lib/routes/spark-routes.ts` -- route definitions
