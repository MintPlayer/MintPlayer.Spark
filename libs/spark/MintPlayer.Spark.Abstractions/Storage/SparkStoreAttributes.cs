namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Marks a provider's registration method (<c>UseRavenDb</c>, <c>UseInMemory</c>) with the store it registers, so
/// SPARK060 can compare it at build time with the features configured next to it (#551 D13). It must agree with the
/// store's <see cref="ISparkStore.ProviderName"/> and <see cref="ISparkStore.Capabilities"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SparkStoreProviderAttribute(string name, SparkStoreCapabilities capabilities) : Attribute
{
    public string Name { get; } = name;
    public SparkStoreCapabilities Capabilities { get; } = capabilities;
}

/// <summary>
/// Marks a feature library's registration method with what it needs from the store, for SPARK060 (#551 D13). The
/// method also calls <see cref="SparkStoreRequirementExtensions.RequireStore"/> with the same values, which
/// <c>UseSpark()</c> checks at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SparkRequiresStoreAttribute(string feature, SparkStoreCapabilities capabilities = SparkStoreCapabilities.None) : Attribute
{
    public string Feature { get; } = feature;
    public SparkStoreCapabilities Capabilities { get; } = capabilities;

    /// <summary>The one provider the feature runs on, or <see langword="null"/> for any capable one.</summary>
    public string? Provider { get; set; }
}
