namespace MintPlayer.Spark.Configuration;

public class SparkOptions
{
    public RavenDbOptions RavenDb { get; set; } = new();
}

public class RavenDbOptions
{
    public string[] Urls { get; set; } = ["http://localhost:8080"];
    public string Database { get; set; } = "Spark";
}
