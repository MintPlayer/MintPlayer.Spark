namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Maps the <c>request_id</c> carried through the consent hop to the id of the
/// <see cref="Models.OidcAuthorizationRequest"/> holding the validated request. See
/// <see cref="OpaqueHandle"/> for why the handle is hashed into the id rather than stored in
/// a field.
/// <para>
/// The handle travels in a URL and therefore lands in browser history, referrer headers and
/// access logs. Hashing it into the id means none of those copies is the stored value, and a
/// consumed request is a strongly-consistent point-load rather than an index hit that may
/// still read back as pending.
/// </para>
/// </summary>
public static class OidcRequestReference
{
    private const string CollectionPrefix = "OidcAuthorizationRequests/";

    /// <summary>The document id recording <paramref name="value"/>.</summary>
    public static string DocumentId(string value) => OpaqueHandle.DocumentId(CollectionPrefix, value);

    /// <summary>A new <c>request_id</c>, handed to the browser once and never persisted.</summary>
    public static string GenerateValue() => OpaqueHandle.Generate();
}
