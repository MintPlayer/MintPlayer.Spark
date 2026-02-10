using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Encryption.Abstractions;
using MintPlayer.Spark.Encryption.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions;

namespace MintPlayer.Spark.Encryption.Services;

/// <summary>
/// Resolves <see cref="EncryptedAttribute"/>-decorated properties for a given entity type
/// and determines which encryption key to use based on the presence of <see cref="ReplicatedAttribute"/>.
/// Uses a per-type reflection cache for performance.
/// </summary>
internal sealed class EncryptedFieldResolver
{
    private readonly SparkEncryptionOptions _options;
    private readonly ConcurrentDictionary<Type, PropertyInfo[]> _cache = new();

    public EncryptedFieldResolver(IOptions<SparkEncryptionOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Gets all string properties decorated with <see cref="EncryptedAttribute"/> for the given type.
    /// </summary>
    public PropertyInfo[] GetEncryptedProperties(Type entityType)
    {
        return _cache.GetOrAdd(entityType, static type =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.GetCustomAttribute<EncryptedAttribute>() != null)
                .ToArray());
    }

    /// <summary>
    /// Returns the appropriate Base64-encoded AES-256 key for encrypting/decrypting the entity type.
    /// If the type carries <c>[Replicated(SourceModule = "X")]</c>, returns <c>ModuleKeys["X"]</c>;
    /// otherwise returns <c>OwnKey</c>.
    /// </summary>
    public string? GetKeyForEntity(Type entityType)
    {
        var replicated = entityType.GetCustomAttribute<ReplicatedAttribute>();
        if (replicated != null)
        {
            return _options.ModuleKeys.TryGetValue(replicated.SourceModule, out var moduleKey)
                ? moduleKey
                : null;
        }

        return _options.OwnKey;
    }
}
