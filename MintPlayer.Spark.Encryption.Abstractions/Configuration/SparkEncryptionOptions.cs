namespace MintPlayer.Spark.Encryption.Abstractions.Configuration;

/// <summary>
/// Configuration options for Spark field-level encryption.
/// </summary>
public class SparkEncryptionOptions
{
    /// <summary>
    /// Base64-encoded AES-256 key (32 bytes) for encrypting this module's own data.
    /// In Development mode, a key is auto-generated if not configured.
    /// </summary>
    public string? OwnKey { get; set; }

    /// <summary>
    /// Keys from other modules, keyed by module name.
    /// Used to decrypt fields on replicated entities that carry <c>[Replicated(SourceModule = "X")]</c>.
    /// Each value is a Base64-encoded AES-256 key (32 bytes).
    /// </summary>
    public Dictionary<string, string> ModuleKeys { get; set; } = new();
}
