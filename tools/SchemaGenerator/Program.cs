using MintPlayer.Spark.SchemaGenerator;

// Usage: MintPlayer.Spark.SchemaGenerator <output directory>
// The build runs it into schemas/ at the repository root; the publish workflow runs it too, to
// compare the set with the latest schemas/v{n} release.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: MintPlayer.Spark.SchemaGenerator <output directory>");
    return 2;
}

var directory = Path.GetFullPath(args[0]);
SparkSchemaGenerator.WriteTo(directory);
Console.WriteLine($"Spark schemas written to {directory}");
return 0;
