namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One file of a library's <c>App_Data</c>, compiled into the library as a layer that applications
/// compose their own file on top of (composition D1). Emitted by the Spark source generator; never
/// written by hand.
/// </summary>
/// <remarks>
/// The text is passed through unparsed: the run time composes and validates it. Read at run time
/// through <c>SparkLayerCatalog</c> and at build time from the referenced assembly's attributes.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class SparkLayerAttribute : Attribute
{
    /// <param name="alias">The library's <c>SparkLibraryAlias</c> (composition D16).</param>
    /// <param name="kind">The kind, decided by the path: <c>actions</c>, <c>translations</c>, <c>model</c>, <c>security</c>, <c>programUnits</c> or <c>moderation</c>.</param>
    /// <param name="path">The file's path relative to the library's <c>App_Data</c>, with <c>/</c>, e.g. <c>Model/SparkUser.json</c>.</param>
    /// <param name="json">The file's text.</param>
    public SparkLayerAttribute(string alias, string kind, string path, string json)
    {
        Alias = alias;
        Kind = kind;
        Path = path;
        Json = json;
    }

    public string Alias { get; }
    public string Kind { get; }
    public string Path { get; }
    public string Json { get; }
}

/// <summary>
/// The layered assemblies a library stacks above: every assembly it was compiled against that carries
/// <see cref="SparkLayerAttribute"/>, transitively (composition D2, grill Q3). Emitted beside the layers
/// by the Spark source generator.
/// </summary>
/// <remarks>
/// Recorded rather than read from the assembly's references, because the compiler drops a reference
/// whose types the library never uses, and a library may override another's layer without using its code.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SparkLayerDependenciesAttribute : Attribute
{
    public SparkLayerDependenciesAttribute(params string[] assemblyNames)
    {
        AssemblyNames = assemblyNames;
    }

    public IReadOnlyList<string> AssemblyNames { get; }
}

/// <summary>
/// Emitted into an application by the Spark source generator: the referenced assemblies that carry
/// <see cref="SparkLayerAttribute"/>, in layer order. The run time loads exactly these, so it sees the
/// layers the build saw even when the compiler dropped a reference whose types the application never
/// uses (composition D1, amended by S1).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SparkLayerAssembliesAttribute : Attribute
{
    public SparkLayerAssembliesAttribute(params string[] assemblyNames)
    {
        AssemblyNames = assemblyNames;
    }

    public IReadOnlyList<string> AssemblyNames { get; }
}
