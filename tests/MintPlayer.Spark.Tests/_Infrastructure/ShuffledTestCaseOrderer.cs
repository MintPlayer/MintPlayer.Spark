using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCaseOrderer(
    "MintPlayer.Spark.Tests._Infrastructure.ShuffledTestCaseOrderer",
    "MintPlayer.Spark.Tests")]

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Opt-in reordering of the cases WITHIN a test class, to prove a class that shares one database
/// (<c>SparkSharedDatabase</c>) does not depend on the order its tests run in.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs a class's cases sequentially but promises no order; its default is a stable hash of
/// each case's id. A class whose tests share a database and pass only in that one order carries a
/// latent contamination bug: an assertion satisfied by a sibling's leftover. Running the class
/// shuffled with a few seeds, and reversed, is how a migrated class earns its place (M8 item 5).
/// </para>
/// <para>
/// <b>Off unless asked for.</b> With <c>SPARK_TEST_ORDER</c> unset this delegates to xUnit's own
/// <see cref="DefaultTestCaseOrderer"/>, so CI and an ordinary local run order exactly as before.
/// Values: <c>reverse</c> (the default order, reversed) or an integer seed (a seeded shuffle, so a
/// failing order can be replayed).
/// </para>
/// </remarks>
public sealed class ShuffledTestCaseOrderer(IMessageSink diagnosticMessageSink) : ITestCaseOrderer
{
    public const string EnvironmentVariable = "SPARK_TEST_ORDER";

    private readonly DefaultTestCaseOrderer inner = new(diagnosticMessageSink);

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        var ordered = inner.OrderTestCases(testCases).ToList();
        var mode = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();

        if (string.IsNullOrEmpty(mode))
            return ordered;

        if (string.Equals(mode, "reverse", StringComparison.OrdinalIgnoreCase))
        {
            ordered.Reverse();
            return ordered;
        }

        if (!int.TryParse(mode, out var seed))
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be 'reverse' or an integer seed, not '{mode}'.");

        var random = new Random(seed);
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        return ordered;
    }
}
