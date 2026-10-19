# MintPlayer.Spark.WaitingRoom

A waiting room for MintPlayer.Spark: rooms bound to a gate with a schedule; RandomizedPreQueue (committed seed), Ballot (provably fair draw, staged single-use links) and Fifo modes; one entry per identity; a public progress topic and a private admission stream; an admission worker under a lease; a presence check that never costs the place (WCAG 2.2.1); signed single-use admission tokens (`IWaitingRoomAdmissions`); truthful sold-out handling.

```csharp
spark.AddWaitingRoom();
```

See [docs/guide-waiting-room.md](../../../docs/guide-waiting-room.md).
