# MintPlayer.Spark.Commerce.WaitingRoom

The waiting room in the MintPlayer.Spark commerce suite (alias `commerce-waiting-room`). Checkout, Promotions,
Ticketing.Events and the waiting room reference none of each other; this package references all four and connects them
through their extension points:

- the waiting room's admission tokens as Checkout's admission gate (scopes `ticketing-phase:{phaseId}` and
  `waiting-room:{gate}`), combinable with a Promotions presale code on the same item;
- Promotions' waiting list learns when a sale's queue closed (`IWaitingListQueueState`);
- every queued ticketing sale phase gets its waiting room automatically (`ISparkCommerceWaitingRooms`);
- the room reads a phase's remaining capacity from the inventory engine (`IWaitingRoomCapacity`).

```csharp
spark.AddCommerceWaitingRoom();   // Spark:Commerce:WaitingRoom
```

See [docs/guide-waiting-room.md](../../../docs/guide-waiting-room.md).
