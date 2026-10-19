using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

/// <summary>
/// A venue (D8): a shared catalogue entry with a name and an address from reference data (§4.18). Anyone signed in
/// may propose one; creations wait for a platform approver when <c>Spark:Commerce:Ticketing:Venues:ApproveCreations</c>
/// is on, modifications always do (Approvals document proposals, X7).
/// </summary>
public sealed class Venue
{
    public string? Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The address (reference-data <see cref="ReferenceData.Address"/>, free text allowed).</summary>
    public Address? Address { get; set; }

    /// <summary>The IANA zone of the venue, copied from the address on save (D33, K10: never converted here).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Who proposed the venue.</summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Where a layout stands: a private draft, waiting for an approver, or published (usable by every seller).</summary>
public enum LayoutState
{
    Draft,
    PendingApproval,
    Published,
}

/// <summary>
/// A seating layout of a venue (D8): it belongs to its creator, who edits a private draft and publishes versions.
/// Others use a published layout as is, or copy it to edit (<see cref="CopiedFromId"/>). The plans live in
/// <see cref="VenueLayoutPlan"/> documents beside it (a 50k-seat plan is megabytes, never a form attribute).
/// </summary>
public sealed class VenueLayout
{
    public string? Id { get; set; }

    [Reference(typeof(Venue))]
    public string VenueId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The creator: the only one who edits the draft and publishes.</summary>
    public string OwnerId { get; set; } = "";

    /// <summary>The layout this one was copied from, if any.</summary>
    public string? CopiedFromId { get; set; }

    /// <summary>Draft until first published; <see cref="LayoutState.PendingApproval"/> while a publication waits.</summary>
    public LayoutState State { get; set; }

    /// <summary>The latest published version (0: never published). Snapshots copy a version; they never follow it.</summary>
    public int PublishedVersion { get; set; }

    /// <summary>Bumped by every draft save; a save names the revision it was made on (optimistic concurrency).</summary>
    public int DraftRevision { get; set; }

    /// <summary>The pending publication request (Approvals), if any.</summary>
    public string? PendingRequestId { get; set; }

    /// <summary>Why the last publication was rejected (the approver's reason), until the next one.</summary>
    public string? LastRejection { get; set; }

    /// <summary>Numbered seats of the published version (or of the draft while never published).</summary>
    public int SeatCount { get; set; }

    /// <summary>Standing and unnumbered capacity of the same version.</summary>
    public int UnnumberedCapacity { get; set; }

    public int BlockCount { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One plan of a layout: the draft (<c>VenueLayoutPlans/{layoutId}/draft</c>), a submitted proposal
/// (<c>…/proposal</c>, frozen while it waits so later draft edits do not change what is approved) or
/// a published version (<c>…/v{n}</c>, never changed again). The background image is an attachment
/// of the draft, copied along.
/// </summary>
public sealed class VenueLayoutPlan
{
    public string? Id { get; set; }

    public string LayoutId { get; set; } = "";

    /// <summary>0 for the draft and the proposal; the version number for a published plan.</summary>
    public int Version { get; set; }

    /// <summary>The draft revision this plan was saved as (or submitted from).</summary>
    public int Revision { get; set; }

    public LayoutPlan Plan { get; set; } = new();

    public DateTime SavedAtUtc { get; set; }

    public static string DraftId(string layoutId) => $"VenueLayoutPlans/{layoutId}/draft";

    public static string ProposalId(string layoutId) => $"VenueLayoutPlans/{layoutId}/proposal";

    public static string VersionId(string layoutId, int version) => $"VenueLayoutPlans/{layoutId}/v{version}";
}

/// <summary>
/// An immutable copy of a published layout version, made when a date goes on sale (§4.11: "a published date freezes a
/// layout snapshot"). It is created once and never written again: republishing the layout changes nothing for a date
/// already on sale. Its seats are the inventory units <c>{snapshotId}/{seatKey}</c>.
/// </summary>
public sealed class VenueLayoutSnapshot
{
    public string? Id { get; set; }

    /// <summary>What the snapshot was made for: the caller's key (Ticketing.Events passes the date id). One snapshot per scope.</summary>
    public string Scope { get; set; } = "";

    public string LayoutId { get; set; } = "";

    public int LayoutVersion { get; set; }

    public string VenueId { get; set; } = "";

    public string VenueName { get; set; } = "";

    /// <summary>The venue's address lines at the time (the legal snapshot a ticket prints).</summary>
    public List<string> AddressLines { get; set; } = [];

    /// <summary>The venue's IANA zone at the time (D33).</summary>
    public string? TimeZone { get; set; }

    public LayoutPlan Plan { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// An uploaded background image (PNG, JPEG or WebP), as the attachment <see cref="AttachmentName"/>. Never changed:
/// a new upload is a new document, so every plan version that names it keeps its picture.
/// </summary>
public sealed class VenueLayoutBackground
{
    public const string AttachmentName = "image";

    public string? Id { get; set; }

    /// <summary>The layout it was uploaded for; only its owner (and approvers) and the plans naming it read it.</summary>
    public string LayoutId { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Length { get; set; }

    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>
/// Whether a snapshot's seat states are published live (<c>ticketing-venues:{snapshotId}</c>) and until when. Kept
/// apart from the snapshot, which is never written again.
/// </summary>
public sealed class VenueSnapshotLive
{
    public string? Id { get; set; }

    public string SnapshotId { get; set; } = "";

    public DateTime LiveUntilUtc { get; set; }

    /// <summary>The inventory chains and quotas were defined (idempotent, but the flag spares the round trips).</summary>
    public bool Provisioned { get; set; }

    public static string For(string snapshotId) => $"VenueSnapshotLives/{snapshotId}";
}
