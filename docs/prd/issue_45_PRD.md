# Product Requirements Document: Streaming Queries via WebSocket

**Issue**: #45
**Title**: Allow feedback through websocket connection
**Status**: Draft
**Created**: 2026-03-05
**Last Updated**: 2026-03-05

---

## Overview

Extend MintPlayer.Spark queries to support real-time data streaming via WebSocket connections. A query marked as `isStreamingQuery: true` opens a WebSocket instead of a one-shot HTTP request, enabling live-updating datatables. Demonstrated with a StockMarket entity generating random price updates.

---

## Goals & Objectives

### Primary Goals
- Enable real-time data streaming through the existing Spark query system
- Maintain backward compatibility — non-streaming queries are unaffected
- Demonstrate capability with a StockMarket demo in DemoApp

### Success Metrics
- Streaming query renders a datatable that updates in real-time without page reload
- WebSocket connection established and maintained for the duration of the page view
- No regressions on existing (non-streaming) query functionality

---

## Functional Requirements

### Must Have (P0)
- [ ] **FR-1**: `SparkQuery` model supports `isStreamingQuery` boolean property (backend + frontend)
- [ ] **FR-2**: Backend detects WebSocket upgrade on `/spark/queries/{id}/execute` for streaming queries
- [ ] **FR-3**: Server pushes data via WebSocket using snapshot + incremental update protocol
- [ ] **FR-4**: Actions classes support streaming methods (convention: `IAsyncEnumerable<T>` return type)
- [ ] **FR-5**: Frontend `SparkQueryListComponent` opens WebSocket for streaming queries
- [ ] **FR-6**: Datatable updates in real-time as messages arrive
- [ ] **FR-7**: WebSocket closed cleanly on component destroy / navigation
- [ ] **FR-8**: Authorization checked before accepting WebSocket connection
- [ ] **FR-9**: `Stock` entity + `StockActions` streaming demo in DemoApp
- [ ] **FR-10**: Non-streaming queries continue to work via HTTP GET (no regression)

### Should Have (P1)
- [ ] **FR-11**: Visual indicator (badge/icon) showing live data status
- [ ] **FR-12**: Graceful error handling when WebSocket connection drops
- [ ] **FR-13**: Auto-reconnect on connection loss with backoff

### Could Have (P2)
- [ ] **FR-14**: Client-side pause/resume of streaming updates
- [ ] **FR-15**: Configurable update interval per query (JSON property)

---

## Timeline & Milestones

### Milestone 1: Backend WebSocket Infrastructure
- [ ] Add `IsStreamingQuery` to `SparkQuery` model
- [ ] Add `app.UseWebSockets()` to middleware pipeline
- [ ] Implement WebSocket upgrade detection in `Execute.cs`
- [ ] Create `StreamingQueryHandler` for WebSocket message loop
- [ ] Define streaming method convention on Actions classes

### Milestone 2: StockMarket Demo Entity
- [ ] Create `Stock` entity class
- [ ] Add to `DemoSparkContext`
- [ ] Implement `StockActions` with streaming price generator
- [ ] Create `GetStocks.json` with `isStreamingQuery: true`
- [ ] Synchronize model and seed data

### Milestone 3: Frontend WebSocket Support
- [ ] Add `isStreamingQuery` to TypeScript `SparkQuery` model
- [ ] Add WebSocket streaming method to `SparkService`
- [ ] Modify `SparkQueryListComponent` for streaming detection
- [ ] Handle `snapshot` message: set full `allItems` signal with complete PersistentObject data
- [ ] Handle `patch` message: find item by `id`, update only changed attribute values (preserve metadata from snapshot)
- [ ] Connection lifecycle management (open, close, error)

### Milestone 4: Polish & Testing
- [ ] Live data visual indicator
- [ ] Reconnection logic
- [ ] End-to-end verification with DemoApp
- [ ] Verify no regressions on existing queries

---

## WebSocket Message Protocol

The protocol is designed to minimize bandwidth. The first message sends full PersistentObject JSON (including metadata, validation rules, attribute definitions, etc.). Subsequent messages send **only changed attribute values** as lightweight patches — no metadata, no rules, no descriptions.

### Server → Client

#### 1. `snapshot` — Initial full dataset (sent once on connect)
Full PersistentObject JSON including all metadata:
```json
{
  "type": "snapshot",
  "data": [
    {
      "id": "stocks/AAPL",
      "objectType": "Stock",
      "attributes": [
        { "name": "Symbol", "value": "AAPL", "label": { "en": "Symbol" }, "dataType": "String", "rules": { ... }, "isRequired": true, "isVisible": true },
        { "name": "CurrentPrice", "value": 189.50, "label": { "en": "Current Price" }, "dataType": "Decimal", "rules": { ... } },
        { "name": "Change", "value": 2.30, ... },
        { "name": "ChangePercent", "value": 1.23, ... }
      ]
    },
    ...
  ]
}
```

#### 2. `patch` — Incremental update (sent on each change)
Only the item ID and changed attribute name/value pairs. The frontend patches these onto the existing local PersistentObject:
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

The frontend finds the item by `id`, then for each key in `attributes`, updates the matching attribute's `value` property. All other metadata (label, dataType, rules, isRequired, isVisible, etc.) is preserved from the snapshot.

#### 3. `error` — Error notification
```json
{
  "type": "error",
  "message": "Description of error"
}
```

### Client → Server
| Type | Purpose |
|------|---------|
| `close` | Request connection close |

### Protocol Summary
| Message | When | Size | Contains |
|---------|------|------|----------|
| `snapshot` | Once on connect | Large | Full PersistentObject[] with all metadata |
| `patch` | Each update | Small | Item ID + changed attribute values only |
| `error` | On failure | Minimal | Error message |

---

## Open Questions

- [ ] Should streaming queries support sorting/filtering client-side only, or should sort changes trigger a new snapshot?
- [ ] Should the update interval be configurable per-query in the JSON definition, or fixed in the Actions method?
- [ ] Should `SparkSubQueryComponent` (child queries on detail pages) also support streaming, or only top-level queries?
- [ ] What is the preferred approach for the streaming method signature: `IAsyncEnumerable<T>`, `ChannelReader<T>`, or callback-based?

---

## Technical Notes (Issue-Specific)

### Streaming Method Convention
Actions classes define streaming methods similarly to custom queries, but with a different return type:
```csharp
// Non-streaming custom query (existing)
public IRavenQueryable<VStock> GetStocks(CustomQueryArgs args) { ... }

// Streaming query (new)
public async IAsyncEnumerable<Stock> StreamStocks(StreamingQueryArgs args) { ... }
```

### WebSocket Endpoint Reuse
The same `/spark/queries/{id}/execute` endpoint handles both HTTP and WebSocket. The server checks:
1. Is the query `isStreamingQuery: true`?
2. Is the request a WebSocket upgrade (`HttpContext.WebSockets.IsWebSocketRequest`)?
3. If both: accept WebSocket. Otherwise: standard HTTP response.

### Angular WebSocket Integration
Use native `WebSocket` API (no RxJS WebSocketSubject needed). The component:
1. Checks `query.isStreamingQuery` after loading query metadata
2. Opens `new WebSocket('ws://host/spark/queries/{id}/execute')`
3. `onmessage` for `snapshot`: set `allItems` signal with full PersistentObject array
4. `onmessage` for `patch`: find item by `id` in current `allItems`, loop over `attributes` keys, update matching attribute's `value` — all other metadata (label, dataType, rules, isRequired, isVisible, etc.) stays intact from the snapshot
5. `ngOnDestroy`: close connection

### Patch Strategy
The `patch` approach is critical for performance. A full PersistentObject includes validation rules, descriptions, attribute metadata (required, visible, labels, etc.) which can be large. On updates, only the changed values are sent:
- Backend compares current vs. previous state per item, emits only changed attribute name/value pairs
- Frontend applies patches via a simple `item.attributes.find(a => a.name === key).value = newValue` loop
- This keeps WebSocket frame sizes small even with complex entity definitions

### No New Dependencies
- Backend: ASP.NET Core built-in WebSocket support (already available)
- Frontend: Native browser `WebSocket` API
- No SignalR, no additional npm packages

---

## Related
- Issue #45
- See MEMORY.md for: Custom Queries architecture, ng-spark library structure
- `docs/issue_37_plan.md` — Custom query implementation (foundation for this feature)
