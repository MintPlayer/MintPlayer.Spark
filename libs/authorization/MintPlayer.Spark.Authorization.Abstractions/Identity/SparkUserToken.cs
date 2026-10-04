namespace MintPlayer.Spark.Authorization.Identity;

public class SparkUserToken
{
    public string LoginProvider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
}
