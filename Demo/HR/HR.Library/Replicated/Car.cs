using MintPlayer.Spark.Replication.Abstractions;

namespace HR.Replicated;

/// <summary>
/// A read-only copy of Cars from the Fleet module.
/// The ETL script defines which fields are replicated.
/// </summary>
[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            '@metadata': {
                '@collection': 'Cars'
            }
        });
    """)]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
}
