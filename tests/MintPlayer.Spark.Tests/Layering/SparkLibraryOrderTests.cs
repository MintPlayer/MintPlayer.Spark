using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// The run-time build of the layer order (composition D2, grill Q3). The generators' build is held to
/// the same table in <c>MintPlayer.Spark.SourceGenerators.Tests</c> (ApplicationLayersGeneratorTests),
/// so both views of the order agree (D14).
/// </summary>
public class SparkLibraryOrderTests
{
    /// <summary>Kept word for word in <c>MintPlayer.Spark.SourceGenerators.Tests/Generators/ApplicationLayersGeneratorTests.cs</c>.</summary>
    public static readonly ((string Name, string[] DependsOn)[] Libraries, string[] Expected)[] All =
    [
        // Core first, then ordinal by name between unrelated libraries (upper case sorts first).
        ([("b.Lib", []), ("MintPlayer.Spark", []), ("A.Lib", [])], ["MintPlayer.Spark", "A.Lib", "b.Lib"]),
        // A library comes after every library it depends on, whatever its name.
        ([("A.Ext", ["Z.Lib"]), ("Z.Lib", []), ("M.Lib", [])], ["M.Lib", "Z.Lib", "A.Ext"]),
        // Chains: C above B above A.
        ([("C", ["B", "A"]), ("A", []), ("B", ["A"])], ["A", "B", "C"]),
        // A dependency outside the set is ignored.
        ([("A", ["Gone"]), ("B", [])], ["A", "B"]),
        // A cycle cannot come from assembly references; it still terminates, by name.
        ([("B", ["A"]), ("A", ["B"])], ["A", "B"]),
    ];

    [Fact]
    public void The_run_time_build_of_the_order_function_agrees_with_the_shared_table()
    {
        foreach (var (libraries, expected) in All)
            SparkLibraryOrder.Sort(libraries, l => l.Name, l => l.DependsOn).Select(l => l.Name).Should().Equal(expected);
    }
}
