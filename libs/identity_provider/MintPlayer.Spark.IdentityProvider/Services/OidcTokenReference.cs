namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Maps a bearer value (authorization code, refresh token) to the id of the
/// <see cref="Models.OidcToken"/> recording it. See <see cref="OpaqueHandle"/> for why the
/// value is hashed into the id rather than stored in a field.
/// </summary>
public static class OidcTokenReference
{
    private const string CollectionPrefix = "OidcTokens/";

    /// <summary>The document id recording <paramref name="value"/>.</summary>
    public static string DocumentId(string value) => OpaqueHandle.DocumentId(CollectionPrefix, value);

    /// <summary>A new bearer value, handed to the client once and never persisted.</summary>
    public static string GenerateValue() => OpaqueHandle.Generate();
}
