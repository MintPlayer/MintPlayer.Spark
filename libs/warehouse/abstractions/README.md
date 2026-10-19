# MintPlayer.Spark.Warehouse.Abstractions

The extension points of the MintPlayer.Spark warehouse (`docs/warehouse_picking_PRD.md` §4.1), and the value types
they exchange. No persistent objects and no alias: the documents live in `MintPlayer.Spark.Warehouse` and its process
packages, which call these interfaces.

| Interface | Decides | PRD |
|---|---|---|
| `ISiteRouter` | Which site fulfils an incoming order | §4.3 Intake, WD3 |
| `IAllocationRule` | The order in which stock balances are claimed (FIFO, FEFO, closest location, custom) | §4.7 Allocation, WD4 |
| `IPutawayRule` | Where received stock goes (fixed location, same lot, nearest empty, custom) | §4.7 Receiving |
| `IPickStrategy` | How released lines become trips (discrete, batch, cluster, zone) | §4.7 Pick work, WD5 |
| `IPickRouter` | The order in which a trip visits its locations (walk sequence by default) | §4.5 Locations |
| `ICartonizer` | Which cartons a parcel needs (manual, volumetric) | §4.7 Packing |
| `IBarcodeResolutionStep` | One step of the scan pipeline (GS1 parse, own codes, registry) | §4.6, WD8 |

Quantities are `decimal` in the item's **base unit** (§4.5 Units); packaging levels convert at input and display.
Lengths are millimetres and weights grams, as the member names say.

Every interface is asynchronous (`ValueTask`, with a `CancellationToken`) and receives everything it needs as values,
so a custom rule is registered in DI and needs no other warehouse package.
