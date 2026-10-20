using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;

// The wire of /spark/ticketing/venues/* (ng-spark `ticketing/venues`). The plan types are in Models; both namespaces
// are claimed by the assembly's SparkClientModule.

/// <summary><c>POST /layouts/create</c>: a new private layout of a venue, with an empty draft.</summary>
[SparkContract]
public sealed record VenueLayoutCreateRequest(string VenueId, string Name);

/// <summary><c>POST /layouts/copy</c>: copy a published layout (or one's own draft) to edit (D8).</summary>
[SparkContract]
public sealed record VenueLayoutCopyRequest(string LayoutId, string Name);

/// <summary>A layout id with the draft revision the next save must name.</summary>
[SparkContract]
public sealed record VenueLayoutRef(string LayoutId, int Revision);

/// <summary><c>POST /layouts/draft</c>: replace the draft. <see cref="Revision"/> is the revision it was edited from; a stale one is refused (409).</summary>
[SparkContract]
public sealed record VenueLayoutSaveDraftRequest(string LayoutId, int Revision, LayoutPlan Plan);

/// <summary><c>POST /layouts/csv/import</c>: merge a CSV of seats into the draft (D32).</summary>
[SparkContract]
public sealed record VenueLayoutCsvImportRequest(string LayoutId, int Revision, string Csv);

/// <summary>A problem of a plan or a CSV file: where, and the translation key of what.</summary>
[SparkContract]
public sealed record VenueLayoutProblem(string Path, string Code);

/// <summary>What a draft save or a CSV import answers: the new revision, or the problems (nothing was saved).</summary>
[SparkContract]
public sealed record VenueLayoutSaveResponse(int Revision, IReadOnlyList<VenueLayoutProblem> Problems);

[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<VenueLayoutStatus>))]
public enum VenueLayoutStatus
{
    [JsonStringEnumMemberName("draft")] Draft,
    [JsonStringEnumMemberName("pendingApproval")] PendingApproval,
    [JsonStringEnumMemberName("published")] Published,
}

/// <summary><c>GET /layouts/plan?layoutId=…&amp;version=draft|proposal|published|{n}</c>: a plan for the editor or the approver.</summary>
/// <param name="CanEdit">The caller owns the layout (the editor saves its draft).</param>
/// <param name="HasBackground">The plan's background image can be fetched from <c>/layouts/background</c>.</param>
[SparkContract]
public sealed record VenueLayoutPlanResponse(
    string LayoutId,
    string VenueId,
    string Name,
    VenueLayoutStatus Status,
    int Revision,
    int PublishedVersion,
    int Version,
    bool CanEdit,
    bool HasBackground,
    string? LastRejection,
    LayoutPlan Plan);

/// <summary><c>POST /layouts/publish</c>: publish the draft as the next version.</summary>
[SparkContract]
public sealed record VenueLayoutPublishRequest(string LayoutId, int Revision);

[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<VenueLayoutPublishOutcome>))]
public enum VenueLayoutPublishOutcome
{
    /// <summary>Published at once (a first version, with creations not needing approval).</summary>
    [JsonStringEnumMemberName("published")] Published,

    /// <summary>Waiting for a platform approver (D8: modifications always, creations when configured).</summary>
    [JsonStringEnumMemberName("pending")] Pending,
}

[SparkContract]
public sealed record VenueLayoutPublishResponse(VenueLayoutPublishOutcome Outcome, int PublishedVersion, string? RequestId);

/// <summary>The custom visual diff of a publication (§4.13). Lists are capped at 5,000 keys; the counts are exact.</summary>
[SparkContract]
public sealed record LayoutDiffResult(
    int AddedCount,
    int RemovedCount,
    int MovedCount,
    int ChangedCount,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> ChangedBlocks,
    IReadOnlyList<string> ChangedChains);

/// <summary>
/// A snapshot's seat map, packed for 50k seats (S7): seats as parallel arrays in map order (block by block, row by
/// row), which is also the order of <see cref="SeatStateFrame.States"/>. <c>GET /seatmap?snapshotId=…</c>, anonymous.
/// </summary>
[SparkContract]
public sealed record SeatMapPayload(
    string SnapshotId,
    string VenueName,
    double Width,
    double Height,
    LayoutBackground? Background,
    IReadOnlyList<LayoutCategory> Categories,
    IReadOnlyList<SeatMapBlock> Blocks,
    IReadOnlyList<LayoutDecor> Decor,
    IReadOnlyList<SeatMapChain> Chains,
    SeatMapRows Rows,
    SeatMapSeats Seats);

/// <summary>A block of the map. Numbered blocks own the seats <c>[SeatStart, SeatStart + SeatCount)</c> and the rows <c>[RowStart, RowStart + RowCount)</c>.</summary>
[SparkContract]
public sealed record SeatMapBlock(
    string Key,
    string Name,
    BlockKind Kind,
    IReadOnlyList<double> Polygon,
    int Capacity,
    int Category,
    int SeatStart,
    int SeatCount,
    int RowStart,
    int RowCount);

/// <summary>A chain the buyer's list alternative offers ("best available in …", D25).</summary>
[SparkContract]
public sealed record SeatMapChain(string Key, string Name, IReadOnlyList<string> Blocks);

/// <summary>The rows, as parallel arrays.</summary>
[SparkContract]
public sealed record SeatMapRows(IReadOnlyList<string> Labels, IReadOnlyList<int> Block, IReadOnlyList<int> SeatStart, IReadOnlyList<int> SeatCount);

/// <summary>The seats, as parallel arrays (index = seat index). <see cref="Category"/> is an index into the categories, -1 for none.</summary>
[SparkContract]
public sealed record SeatMapSeats(
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Labels,
    IReadOnlyList<double> X,
    IReadOnlyList<double> Y,
    IReadOnlyList<int> Row,
    IReadOnlyList<int> Tags,
    IReadOnlyList<int> Category);

/// <summary>
/// The live state of every seat of a snapshot, on the public topic <c>ticketing-venues:{snapshotId}</c> and from
/// <c>GET /seatmap/state?snapshotId=…</c>. <see cref="States"/> is base64 of 2 bits per seat in map order (seat
/// <c>i</c> in byte <c>i / 4</c>, bits <c>2 × (i % 4)</c>): 0 free, 1 held, 2 sold. Who holds a seat is never sent.
/// </summary>
[SparkContract]
public sealed record SeatStateFrame(string SnapshotId, int SeatCount, string States, IReadOnlyList<SeatMapBlockAvailability> Blocks);

/// <summary>What is left in a block: free seats of a numbered block, places of a standing or unnumbered one.</summary>
[SparkContract]
public sealed record SeatMapBlockAvailability(string Key, int Available);
