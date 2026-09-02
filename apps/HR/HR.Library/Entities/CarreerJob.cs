using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class CarreerJob
{
    /// <summary>The profession held during this career step.</summary>
    [Reference(typeof(Profession))]
    public string? ProfessionId { get; set; }
    /// <summary>Date on which the contract for this job started.</summary>
    public DateOnly ContractStart { get; set; }
    /// <summary>Date on which the contract ended; leave empty while the job is ongoing.</summary>
    public DateOnly? ContractEnd { get; set; }
}
