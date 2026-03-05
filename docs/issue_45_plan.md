# Development Plan: Issue #45

**Issue**: #45
**Title**: Allow feedback through websocket connection
**Type**: Feature
**Priority**: Medium

## Executive Summary

Add WebSocket streaming support to MintPlayer.Spark queries. When a query is marked as a streaming query (`isStreamingQuery: true`), the frontend opens a WebSocket connection to the server instead of performing a single HTTP GET. The server continuously pushes data updates through the WebSocket. This is demonstrated via a new StockMarket demo entity that generates random price updates in real-time.

---

## Problem Statement

### Current Behavior
All Spark queries execute via `GET /spark/queries/{id}/execute`, returning a full `PersistentObject[]` array in a single HTTP response. Data is static after load — no real-time updates.

### Expected Behavior
Queries marked with `isStreamingQuery: true` in their JSON definition should:
1. Upgrade the HTTP connection to a WebSocket on the same endpoint
2. Continuously push data updates from the server to the client
3. Render live-updating datatables in the frontend
4. Demonstrated with a StockMarket demo showing random price fluctuations

### Impact
Enables real-time data scenarios (dashboards, monitoring, live feeds) within the Spark framework without requiring custom infrastructure.

---

## Technical Analysis

### Current Query Flow
```
HTTP GET /spark/queries/{id}/execute
    → ExecuteQuery.HandleAsync()
    → IQueryExecutor.ExecuteQueryAsync()
    → Returns PersistentObject[]
    → Single JSON response
```

### Proposed Streaming Flow
```
HTTP GET /spark/queries/{id}/execute (WebSocket upgrade request)
    → Detect WebSocket request + isStreamingQuery
    → Accept WebSocket connection
    → StreamingQueryExecutor resolves Actions class streaming method
    → Server pushes JSON messages continuously
    → Client updates datatable in real-time
```

### Key Files to Modify

**Backend (MintPlayer.Spark)**:
- `SparkMiddleware.cs` — Add `app.UseWebSockets()`, modify query endpoint to handle upgrade
- `Endpoints/Queries/Execute.cs` — WebSocket upgrade logic when `isStreamingQuery`
- `Services/QueryExecutor.cs` — New streaming execution path
- `Abstractions/SparkQuery.cs` — Add `IsStreamingQuery` property

**Frontend (ng-spark)**:
- `services/spark.service.ts` — Add `executeStreamingQuery()` method
- `components/query-list/spark-query-list.component.ts` — Detect streaming queries, use WebSocket
- `models/spark-query.ts` — Add `isStreamingQuery` property

**New Demo (StockMarket)**:
- `Demo/DemoApp/DemoApp.Library/Entities/Stock.cs` — Entity class
- `Demo/DemoApp/DemoApp/Actions/StockActions.cs` — Streaming data generator
- `Demo/DemoApp/App_Data/Queries/GetStocks.json` — Streaming query definition
- `Demo/DemoApp/App_Data/Model/Stock.json` — Entity model (generated via sync)

### Dependencies
- ASP.NET Core built-in WebSocket support (`Microsoft.AspNetCore.WebSockets`)
- No new NuGet packages required for raw WebSocket approach
- Angular: native `WebSocket` API (no additional npm packages)

### Architecture Considerations
- **Raw WebSocket over SignalR**: Simpler, no extra dependencies, fits the Spark minimal approach
- **Same endpoint**: The query execute endpoint detects WebSocket upgrade requests vs. regular HTTP
- **Actions class convention**: Streaming methods on Actions classes use `IAsyncEnumerable<T>` or accept a callback/channel
- **Authorization**: Reuse existing `IAccessControl`/`IPermissionService` checks before accepting WebSocket
- **Connection lifecycle**: Server sends data, client can send close/control messages
- **Backpressure**: Server respects WebSocket send buffer; drops or queues if client is slow

---

## Implementation Plan

### Phase 1: Backend — SparkQuery Model & WebSocket Infrastructure
1. Add `IsStreamingQuery` (bool) property to `SparkQuery` in Abstractions
2. Add `app.UseWebSockets()` in `SparkMiddleware.UseSpark()`
3. Modify `Execute.cs` to detect `HttpContext.WebSockets.IsWebSocketRequest` when `IsStreamingQuery` is true
4. Create `StreamingQueryHandler` that accepts WebSocket, resolves streaming method on Actions class, and pushes messages
5. Define streaming method convention on Actions classes (e.g., returns `IAsyncEnumerable<T>` or `ChannelReader<T>`)

### Phase 2: Backend — StockMarket Demo Entity
1. Create `Stock` entity class (Id, Symbol, Name, CurrentPrice, Change, ChangePercent, LastUpdated)
2. Add `Stocks` property to `DemoSparkContext`
3. Create `StockActions` class with a streaming method that generates random price updates
4. Create `GetStocks.json` query definition with `isStreamingQuery: true`
5. Synchronize model to generate `Stock.json`
6. Seed initial stock data (e.g., AAPL, MSFT, GOOG, AMZN, TSLA)

### Phase 3: Frontend — WebSocket Integration in ng-spark
1. Add `isStreamingQuery` property to TypeScript `SparkQuery` model
2. Create `SparkWebSocketService` or extend `SparkService` with streaming method
3. Modify `SparkQueryListComponent` to detect streaming queries and open WebSocket
4. Handle `snapshot` message: parse full PersistentObject[], set `allItems` signal
5. Handle `patch` message: find item by `id` in `allItems`, update only the changed attribute values (preserve all metadata from snapshot)
6. Manage connection lifecycle (open, reconnect on error, close on component destroy)
7. Show visual indicator that data is live/streaming

### Phase 4: Frontend — StockMarket Program Unit
1. Add StockMarket program unit configuration in DemoApp
2. Verify the streaming query renders correctly in the datatable
3. Test connection lifecycle (open, data flow, disconnect, reconnect)

---

## WebSocket Message Protocol

The first message sends full PersistentObject JSON (all metadata, validation rules, attribute definitions). Subsequent messages are lightweight **patches** containing only the item ID and changed attribute values — no metadata, no rules, no descriptions.

### Server → Client Messages

**1. `snapshot` — Initial full dataset (sent once on connect):**
```json
{
  "type": "snapshot",
  "data": [
    {
      "id": "stocks/AAPL",
      "objectType": "Stock",
      "attributes": [
        { "name": "Symbol", "value": "AAPL", "label": { "en": "Symbol" }, "dataType": "String", "rules": { ... }, "isRequired": true, "isVisible": true },
        { "name": "CurrentPrice", "value": 189.50, "label": { "en": "Current Price" }, "dataType": "Decimal", ... },
        { "name": "Change", "value": 2.30, ... }
      ]
    },
    ...
  ]
}
```

**2. `patch` — Incremental update (only changed values):**
```json
{
  "type": "patch",
  "id": "stocks/AAPL",
  "attributes": {
    "CurrentPrice": 191.20,
    "Change": 4.00,
    "ChangePercent": 2.15,
    "LastUpdated": "2026-03-05T10:55:00Z"
  }
}
```

The frontend finds the item by `id`, then patches each key in `attributes` onto the matching attribute's `value`. All metadata (label, dataType, rules, isRequired, isVisible, etc.) is preserved from the snapshot.

**3. `error`:**
```json
{
  "type": "error",
  "message": "Description of error"
}
```

### Client → Server Messages

**Close**:
```json
{
  "type": "close"
}
```

### Protocol Summary
| Message | When | Size | Contains |
|---------|------|------|----------|
| `snapshot` | Once on connect | Large | Full PersistentObject[] with all metadata |
| `patch` | Each update | Small | Item ID + changed attribute values only |
| `error` | On failure | Minimal | Error message |

---

## Test Scenarios

### Scenario 1: WebSocket Connection Established
- **Given**: A query with `isStreamingQuery: true` exists
- **When**: The frontend navigates to the query page
- **Then**: A WebSocket connection is opened to `/spark/queries/{id}/execute`

### Scenario 2: Initial Data Snapshot
- **Given**: WebSocket connection is established
- **When**: The server starts the streaming method
- **Then**: An initial snapshot of all items is sent and rendered in the datatable

### Scenario 3: Live Updates
- **Given**: Initial snapshot has been received
- **When**: The server generates a price update
- **Then**: The updated item is pushed via WebSocket and the datatable row updates in real-time

### Scenario 4: Connection Cleanup
- **Given**: WebSocket connection is active
- **When**: User navigates away from the query page
- **Then**: WebSocket connection is closed cleanly

### Scenario 5: Non-streaming Query Unchanged
- **Given**: A query with `isStreamingQuery: false` (or unset)
- **When**: The frontend navigates to the query page
- **Then**: Standard HTTP GET is used (existing behavior unchanged)

### Scenario 6: Authorization Check
- **Given**: A streaming query exists and user lacks Read permission
- **When**: User attempts to open the query
- **Then**: WebSocket connection is rejected with 403

---

## Acceptance Criteria

- [ ] `SparkQuery` model has `IsStreamingQuery` boolean property
- [ ] Backend detects WebSocket upgrade requests on the query execute endpoint
- [ ] Streaming queries push data via WebSocket using defined message protocol
- [ ] Non-streaming queries continue to work via HTTP GET (no regression)
- [ ] `Stock` entity exists in DemoApp with realistic market data fields
- [ ] `StockActions` generates random price updates via streaming method
- [ ] `GetStocks.json` query uses `isStreamingQuery: true`
- [ ] Frontend `SparkQueryListComponent` detects streaming queries and opens WebSocket
- [ ] Datatable updates in real-time as new data arrives
- [ ] WebSocket connection is cleaned up on component destroy
- [ ] Authorization is checked before accepting WebSocket connections
- [ ] Visual indicator shows that data is live/streaming

---

## Build & Test Commands

```bash
# Backend
dotnet build MintPlayer.Spark.sln -c Debug

# Frontend (ng-spark library)
cd node_packages/ng-spark && npm run build

# Demo app
cd Demo/DemoApp/DemoApp && dotnet run

# Model synchronization
cd Demo/DemoApp/DemoApp && dotnet run --spark-synchronize-model
```

---

## Related Files

### Backend
- `MintPlayer.Spark/SparkMiddleware.cs` — Endpoint registration, middleware pipeline
- `MintPlayer.Spark/Endpoints/Queries/Execute.cs` — Query execution handler
- `MintPlayer.Spark/Services/QueryExecutor.cs` — Query execution engine
- `MintPlayer.Spark.Abstractions/SparkQuery.cs` — Query model
- `MintPlayer.Spark/Queries/CustomQueryArgs.cs` — Custom query context
- `MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs` — Base actions class

### Frontend
- `node_packages/ng-spark/src/lib/services/spark.service.ts` — HTTP service
- `node_packages/ng-spark/src/lib/components/query-list/spark-query-list.component.ts` — Query list component
- `node_packages/ng-spark/src/lib/models/spark-query.ts` — Query TypeScript model

### Demo
- `Demo/DemoApp/DemoApp/DemoSparkContext.cs` — SparkContext
- `Demo/DemoApp/DemoApp/Actions/PersonActions.cs` — Example actions class
- `Demo/DemoApp/App_Data/Queries/` — Query JSON definitions
