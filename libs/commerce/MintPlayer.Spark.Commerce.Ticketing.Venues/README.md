# MintPlayer.Spark.Commerce.Ticketing.Venues

Venues and seating layouts for the MintPlayer.Spark ticketing suite (`docs/commerce_PRD.md` §4.11, D7, D8, D25, D32,
default 3). The guide is `docs/guide-ticketing-venues.md`.

```csharp
spark.AddAuthentication<SparkUser>();
spark.AddTicketingVenues();   // Spark:Commerce:Ticketing:Venues; adds Inventory and Approvals
```

- **Venues** (`Venue`): a shared catalogue with a reference-data `Address` (its time zone comes along, D33). Every save is
  an Approvals document proposal: modifications always, creations when `ApproveCreations` is on (D8).
- **Layouts** (`VenueLayout`): owned by their creator, who edits a private draft in the layout editor (ng-spark
  `ticketing/venues`) and publishes versions; others use a published layout or copy it to edit. A new version of a
  published layout always waits for an approver (`ApproveProposal/VenueLayout`); a first version only with `ApproveCreations`.
- **Snapshots**: `ISparkVenues.SnapshotAsync(layoutId, scope)` freezes a published version for a date, once per scope,
  never changed again. `ProvisionAsync` defines its fill chains and block quotas in the inventory engine; seats are the
  units `{snapshotId}/{seatKey}`. Accessible and companion seats are left out of chains (default 3).
- **Seat map**: `GET /spark/ticketing/venues/seatmap?snapshotId=…` (packed for 50k seats) and the live topic
  `ticketing-venues:{snapshotId}` (2 bits per seat) after `SetLiveAsync`.
- **CSV**: `block,row,seat,x,y,category,tags,companion_of,key`, import and export, in the browser and on the server.

The application grants the approvers in its `security.json`:

```json
{ "resource": "ApproveProposal/Venue", "groupId": "PlatformAdmins" },
{ "resource": "ApproveProposal/VenueLayout", "groupId": "PlatformAdmins" }
```
