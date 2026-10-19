using MintPlayer.Spark.SchemaGenerator;

// Usage:
//   MintPlayer.Spark.SchemaGenerator <output directory>
//     The JSON schemas. The build runs it into schemas/ at the repository root; the publish workflow runs it
//     too, to compare the set with the latest schemas/v{n} release.
//   MintPlayer.Spark.SchemaGenerator contracts [--verify]
//     The libraries' TypeScript contracts (commerce X20 item 2), written into ng-spark's entry points, or with
//     --verify only checked (exit 3 when a committed file is out of date). Run from inside the repository.
if (args.Length >= 1 && args[0] == "contracts")
{
    var options = args.Skip(1).ToList();
    var verify = options.Remove("--verify");
    if (options.Count > 0)
    {
        Console.Error.WriteLine("Usage: MintPlayer.Spark.SchemaGenerator contracts [--verify]");
        return 2;
    }
    return SparkLibraryContracts.Run(SparkLibraryContracts.FindRepositoryRoot(Directory.GetCurrentDirectory()), verify, Console.Out, Console.Error);
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: MintPlayer.Spark.SchemaGenerator <output directory> | contracts [--verify]");
    return 2;
}

var directory = Path.GetFullPath(args[0]);
SparkSchemaGenerator.WriteTo(directory);
Console.WriteLine($"Spark schemas written to {directory}");
return 0;
