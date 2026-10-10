namespace MintPlayer.Spark.Abstractions.Sharing;

/// <summary>
/// A type whose objects can be shared through a link (<c>docs/seo_analyzer_PRD.md</c> X6): a capability token,
/// of which only the SHA-256 is stored here. Anyone holding the link reads exactly that object (the attributes
/// declared with <c>spark.AddShareLinks</c>) and its declared child queries, anonymously, until it is revoked.
/// </summary>
/// <remarks>
/// <see cref="ShareTokenHash"/> is framework-protected: a persistent-object create or edit keeps the stored value
/// whatever the client posts. Only the <c>ShareLink</c> / <c>RevokeShareLink</c> actions, <c>ISparkShareLinks</c>
/// and raw server code change it. Never make it a model attribute.
/// </remarks>
public interface ISparkShareable
{
    /// <summary>The SHA-256 (hex) of the current link's token; null when the object is not shared.</summary>
    string? ShareTokenHash { get; set; }
}
