namespace MintPlayer.Spark.SubscriptionWorker;

/// <summary>
/// Configuration surface for <c>AddSparkSubscriptions</c>. Currently carries no options.
/// <para>
/// It previously declared <c>WaitForNonStaleIndexes</c> and <c>NonStaleIndexTimeout</c>, which
/// nothing ever read — the workers never waited on indexes, so setting them changed no behaviour
/// while reading as a configured guarantee. They were also a third competing "default index
/// timeout" alongside the two in the test library. Removed rather than implemented: a worker that
/// needs non-stale indexes should wait for the specific query it depends on, not gate startup on
/// every index in the database.
/// </para>
/// </summary>
public class SparkSubscriptionOptions;
