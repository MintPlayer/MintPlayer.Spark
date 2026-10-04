using MintPlayer.Spark.Client;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A delete as a user makes it (#467, D14): of the version they loaded. A row the caller cannot load
/// is sent with an etag that names no version, so the server answers exactly as it would a real
/// stale request — 404 for a row that is not there or not theirs.
/// </summary>
internal static class SparkClientDeleteExtensions
{
    public const string UnknownEtag = "A:0-unknown";

    public static async Task DeleteAsLoadedAsync(this SparkClient client, Guid objectTypeId, string id)
    {
        var loaded = await GetOrNullAsync(client, objectTypeId, id);
        await client.DeletePersistentObjectAsync(objectTypeId, id, loaded?.Etag ?? UnknownEtag);
    }

    private static async Task<Abstractions.PersistentObject?> GetOrNullAsync(SparkClient client, Guid objectTypeId, string id)
    {
        try { return await client.GetPersistentObjectAsync(objectTypeId, id); }
        catch (SparkClientException) { return null; }
    }
}
