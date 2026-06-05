# PRD: Spark Messaging Improvements

**Version:** 1.0
**Date:** February 8, 2026
**Status:** Draft
**Prerequisite:** Implemented messaging system from PRD-Messaging.md

---

## 1. Overview

This document captures gaps identified in the initial MintPlayer.Spark.Messaging implementation. Issues are ordered by severity.

---

## 2. Bug Fix: HandleAsync Reflection Ambiguity

**Severity:** Bug -- throws `AmbiguousMatchException` at runtime
**File:** `MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:152`

**Problem:** When a recipient implements `IRecipient<A>` and `IRecipient<B>`, calling `recipientType.GetMethod("HandleAsync")` finds two overloads and throws.

**Fix:** Resolve the specific `HandleAsync` method from the correct interface, not from the class:

```csharp
// Instead of:
var handleMethod = recipientType.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance);

// Use the interface method with the specific message type:
var recipientInterfaceType = typeof(IRecipient<>).MakeGenericType(clrType);
var handleMethod = recipientInterfaceType.GetMethod("HandleAsync");
```

This resolves the exact method for the message type being processed, regardless of how many `IRecipient<T>` interfaces the class implements.

---

## 3. Throughput: Drain Queue Before Sleeping

**Severity:** Functional gap -- limits throughput to 1 message/queue/cycle
**File:** `MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:81-107`

**Problem:** After processing one message per queue, the processor returns to the wait loop. If 100 messages are queued, it takes 100 cycles (each requiring a Changes API signal or fallback poll timeout) to drain them.

**Fix:** After successfully processing a message in a queue, immediately check for the next actionable message in the same queue. Continue until the queue is drained or an error occurs, then return to the wait loop.

```
ProcessMessagesAsync:
  1. Query oldest actionable message per queue
  2. Process each queue concurrently
  3. Per queue: process message, then loop back to query next message in same queue
  4. Stop looping when no more actionable messages remain
  5. Return to wait loop
```

---

## 4. Resilience: Changes API Error Handling

**Severity:** Silent degradation -- processor falls back to polling without logging
**File:** `MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:236-245`

**Problem:** `DocumentChangeObserver.OnError` is empty. If the RavenDB Changes API connection drops, no notification is logged and no re-subscription occurs. The system silently degrades to fallback polling only.

**Fix:**
- Log the error in `OnError`
- Signal the main loop to re-subscribe
- Add reconnection logic in `ExecuteAsync` that detects a broken subscription and re-creates it

```csharp
public void OnError(Exception error)
{
    _logger.LogWarning(error, "RavenDB Changes API connection lost, falling back to polling");
    _signal.Release(); // Wake up the main loop
    _needsReconnect = true;
}
```

In `ExecuteAsync`, check `_needsReconnect` and re-establish the subscription.

---

## 5. Query Optimization: Pagination

**Severity:** Performance -- loads all actionable messages into memory
**File:** `MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:87-93`

**Problem:** `ToListAsync()` loads ALL actionable messages to group by queue and take the first per queue. With thousands of pending messages this wastes memory and RavenDB bandwidth.

**Fix options:**
- **Option A:** Use a RavenDB Map-Reduce index that computes the oldest actionable message per queue directly
- **Option B:** Query with `.Take(128)` as a reasonable cap (RavenDB defaults to 128 anyway), accepting that some queues may be missed until the next cycle
- **Option C:** Query distinct queue names first (via facets or a separate index), then query the oldest message per queue individually

Option B is simplest and sufficient for moderate volumes.

---

## 6. Consistency: WaitForNonStaleResults

**Severity:** Minor -- newly stored messages may not appear in index immediately
**File:** `MintPlayer.Spark.Messaging/Services/MessageProcessor.cs:87-93`

**Problem:** RavenDB indexes are eventually consistent. A just-stored message may not appear in the `SparkMessages_ByQueue` index query when the Changes API fires.

**Fix:** Add `Customize(x => x.WaitForNonStaleResults())` to the actionable messages query:

```csharp
var actionableMessages = await session
    .Query<SparkMessage, Indexes.SparkMessages_ByQueue>()
    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
    .Where(...)
```

The timeout prevents indefinite blocking if the index is heavily behind.

---

## 7. DI Scope: Register Scoped IAsyncDocumentSession

**Severity:** Misleading documentation -- recipient example fails at runtime
**Files:** `MintPlayer.Spark.Messaging/SparkMessagingExtensions.cs`, `MintPlayer.Spark.Messaging/README.md`

**Problem:** The README shows recipients injecting `IAsyncDocumentSession`, but Spark doesn't register sessions as scoped services. Recipients attempting this injection would get a DI resolution failure.

**Fix options:**
- **Option A (preferred):** Register a scoped `IAsyncDocumentSession` factory in `AddSparkMessaging()`:
  ```csharp
  services.AddScoped<IAsyncDocumentSession>(sp =>
      sp.GetRequiredService<IDocumentStore>().OpenAsyncSession());
  ```
- **Option B:** Update documentation to show injecting `IDocumentStore` and opening sessions manually

Option A is preferred as it enables the idiomatic scoped pattern and ensures the session is disposed with the scope.

---

## 8. Retention: Auto-Cleanup of Completed Messages

**Severity:** Design gap -- documents accumulate indefinitely
**File:** `MintPlayer.Spark.Messaging/Models/SparkMessage.cs`

**Problem:** Completed and dead-lettered `SparkMessage` documents stay in RavenDB forever, growing the collection unboundedly.

**Fix:** Use RavenDB's built-in [document expiration](https://ravendb.net/docs/article-page/6.2/csharp/server/extensions/expiration) feature. When marking a message as `Completed` or `DeadLettered`, set the `@expires` metadata:

```csharp
var metadata = session.Advanced.GetMetadataFor(msg);
metadata[Constants.Documents.Metadata.Expires] = DateTime.UtcNow.AddDays(options.RetentionDays);
```

Add `RetentionDays` to `SparkMessagingOptions` (default: 7 days). Requires the Expiration bundle to be enabled on the RavenDB database (enabled by default since RavenDB 5.x).

---

## 9. Implementation Plan

| # | Task | Priority |
|---|------|----------|
| 1 | Fix HandleAsync reflection ambiguity | Critical (bug) |
| 2 | Drain queue loop before sleeping | High |
| 3 | Changes API error logging + reconnection | High |
| 4 | Add query pagination (.Take) | Medium |
| 5 | Add WaitForNonStaleResults | Medium |
| 6 | Register scoped IAsyncDocumentSession | Medium |
| 7 | Add document expiration for completed messages | Low |
