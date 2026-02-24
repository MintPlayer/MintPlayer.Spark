using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class CarreerJob
{
    [Reference(typeof(Profession))]
    public string? ProfessionId { get; set; }
    public DateOnly ContractStart { get; set; }
    public DateOnly? ContractEnd { get; set; }
}
