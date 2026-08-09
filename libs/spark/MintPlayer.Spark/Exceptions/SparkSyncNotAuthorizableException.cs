namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// A cross-module sync action targeted a collection whose entity type is not registered, so no
/// authorization decision can be made about it.
/// <para>
/// Before M11 this path wrote the document anyway, through a CLR-reflection fallback that never
/// consulted <c>security.json</c> — an authenticated module could insert, update or delete anything
/// in any collection. Refusing here is the fail-closed reading the rest of the framework applies:
/// unevaluable is not permitted.
/// </para>
/// </summary>
public sealed class SparkSyncNotAuthorizableException(string collection)
    : Exception(
        $"Collection '{collection}' has no registered entity type, so a sync action against it "
        + "cannot be authorized. Register the entity on the application's SparkContext and run "
        + "--spark-synchronize-model, which is what gives security.json a name to grant rights on.")
{
    public string Collection { get; } = collection;
}
