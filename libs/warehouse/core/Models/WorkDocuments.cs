namespace MintPlayer.Spark.Warehouse.Models;

/// <summary>The kinds of directed work (PRD §4.5, WD5): one collection for every kind keeps one index.</summary>
public enum WarehouseTaskKind
{
    Pick,
    PutBack,
    Putaway,
    Replenish,
    Count,
    Inspect,
}

public enum WarehouseTaskState
{
    Open,
    Assigned,
    InProgress,
    Done,
    Cancelled,
}

/// <summary>
/// One unit of directed work (PRD §4.5, WD5). A pick task is one location visit, in walk-sequence order; the kind-specific
/// fields live in <see cref="Payload"/>. A task is held by a user through a lease (commerce X12): an expired lease frees it
/// for someone else, so a handheld that dies does not strand work. Every confirmation carries an op id; a replayed op id
/// is a no-op (<see cref="ConfirmedOpIds"/>, WI4).
/// </summary>
public sealed class WarehouseTask
{
    public const string IdPrefix = "warehouse-tasks/";

    public string? Id { get; set; }

    public WarehouseTaskKind Kind { get; set; }

    public WarehouseTaskState State { get; set; }

    public string SiteId { get; set; } = "";

    public string? ZoneId { get; set; }

    public string? LocationId { get; set; }

    /// <summary>The location's walk sequence, copied so a queue sorts without loading locations.</summary>
    public long WalkSequence { get; set; }

    /// <summary>For moves: where the stock goes.</summary>
    public string? TargetLocationId { get; set; }

    public string? ItemId { get; set; }

    public string? LotCode { get; set; }

    /// <summary>Base units to handle; for a count, null (blind).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Base units confirmed so far.</summary>
    public decimal Confirmed { get; set; }

    /// <summary>The owners whose goods the task touches (WI5: an owner member sees only tasks of their owners).</summary>
    public List<string> OrganizationIds { get; set; } = [];

    /// <summary>The wave, batch or count run, or null.</summary>
    public string? GroupId { get; set; }

    /// <summary>What the task serves: an order line, a receipt line, a replenishment trigger.</summary>
    public string? Reference { get; set; }

    public int Priority { get; set; }

    public string? AssignedTo { get; set; }

    /// <summary>Until when <see cref="AssignedTo"/> holds the task; past it the task can be taken by someone else.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public Dictionary<string, string> Payload { get; set; } = [];

    /// <summary>The last op ids applied to this task (bounded), so a replayed confirmation is a no-op.</summary>
    public List<string> ConfirmedOpIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public enum WarehouseTaskGroupKind
{
    Wave,
    PickBatch,
    CountRun,
}

/// <summary>
/// A group of tasks (PRD §4.5): a wave, a pick batch with its picker and lease (X12) and totes, or a count run. Totes are
/// unique while in use (an X12 claim per container id).
/// </summary>
public sealed class WarehouseTaskGroup
{
    public const string IdPrefix = "warehouse-task-groups/";

    public string? Id { get; set; }

    public WarehouseTaskGroupKind Kind { get; set; }

    public string SiteId { get; set; } = "";

    public WarehouseTaskState State { get; set; }

    public string? AssignedTo { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public List<string> ToteIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A productivity event (Q-W19): recorded per user because the metrics need it; per-user views for supervisors only,
/// aggregates for everyone else, a host retention period, and part of the GDPR export and deletion hooks.
/// </summary>
public sealed class ProductivityEvent
{
    public const string IdPrefix = "warehouse-productivity/";

    public string? Id { get; set; }

    public WarehouseTaskKind Kind { get; set; }

    public string UserId { get; set; } = "";

    public string SiteId { get; set; } = "";

    public string? TaskId { get; set; }

    public decimal Quantity { get; set; }

    public int Lines { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndedAt { get; set; }
}

/// <summary>An audited run of the recall trace (round 5, commerce GA7): who traced which lot, when, and how much it found.</summary>
public sealed class RecallTraceRun
{
    public const string IdPrefix = "warehouse-recall-traces/";

    public string? Id { get; set; }

    public string? OrganizationId { get; set; }

    public string ItemId { get; set; } = "";

    public string? LotCode { get; set; }

    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public string? UserId { get; set; }

    public DateTimeOffset RanAt { get; set; }

    public int ShipmentLines { get; set; }

    public decimal OnHand { get; set; }
}
