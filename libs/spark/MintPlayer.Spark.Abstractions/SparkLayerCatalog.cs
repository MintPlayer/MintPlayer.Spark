using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions;

/// <summary>One file a library ships as a layer (<c>[assembly: SparkLayer]</c>).</summary>
/// <param name="Kind">The kind: <c>actions</c>, <c>translations</c>, <c>model</c>, <c>security</c>, <c>programUnits</c> or <c>moderation</c>.</param>
/// <param name="Path">The path relative to the library's <c>App_Data</c>, with <c>/</c>.</param>
/// <param name="Json">The file's text, unparsed.</param>
public sealed record SparkLibraryLayer(string Kind, string Path, string Json);

/// <summary>A referenced library that ships layers.</summary>
/// <param name="Alias">Its <c>SparkLibraryAlias</c> (composition D16).</param>
/// <param name="AssemblyName">Its assembly; layers are named by it in messages.</param>
/// <param name="DependsOn">The layered assemblies it stacks above.</param>
/// <param name="Layers">Its files, by path.</param>
public sealed record SparkLibrary(string Alias, string AssemblyName, IReadOnlyList<string> DependsOn, IReadOnlyList<SparkLibraryLayer> Layers);

/// <summary>
/// The library layers in this process, in layer order: <c>MintPlayer.Spark</c> first, each library
/// above the libraries it depends on, by assembly name between unrelated ones (composition D1/D2).
/// </summary>
/// <remarks>
/// <para>
/// The application's build records the layered assemblies it referenced
/// (<c>[assembly: SparkLayerAssemblies]</c>), and exactly those are loaded: the compiler drops a
/// reference whose types the application never uses, so a reference walk alone misses a library that
/// ships only layers (S1). A host whose entry assembly records nothing (a test runner hosting the
/// application through <c>WebApplicationFactory</c>, a tool) falls back to <see cref="SparkAssemblies.SparkAware"/>
/// plus every deployed assembly whose metadata carries <c>[assembly: SparkLayer]</c>, so the fallback is not
/// subject to the same trimming.
/// </para>
/// <para>
/// Read once and cached: library layers are compiled in and never reload (D15).
/// </para>
/// </remarks>
public static class SparkLayerCatalog
{
    private static readonly Lazy<IReadOnlyList<SparkLibrary>> libraries = new(() => Discover(LayeredAssemblies(Assembly.GetEntryAssembly())));

    /// <summary>Every library in this process that ships layers, in layer order.</summary>
    /// <exception cref="InvalidOperationException">Two libraries declare the same alias.</exception>
    public static IReadOnlyList<SparkLibrary> Libraries => libraries.Value;

    /// <summary>The layers of one kind, in layer order, each with the library that ships it.</summary>
    public static IReadOnlyList<(SparkLibrary Library, SparkLibraryLayer Layer)> Of(string kind) => Of(Libraries, kind);

    /// <summary>The layers of one kind among <paramref name="libraries"/>, in their order.</summary>
    public static IReadOnlyList<(SparkLibrary Library, SparkLibraryLayer Layer)> Of(IEnumerable<SparkLibrary> libraries, string kind)
        => libraries
            .SelectMany(l => l.Layers.Where(x => string.Equals(x.Kind, kind, StringComparison.Ordinal)).Select(x => (l, x)))
            .ToList();

    /// <summary>The libraries <paramref name="assemblies"/> hold, in layer order.</summary>
    /// <exception cref="InvalidOperationException">Two of them declare the same alias.</exception>
    public static IReadOnlyList<SparkLibrary> Discover(IEnumerable<Assembly> assemblies)
    {
        var found = new List<SparkLibrary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            if (assembly.GetName().Name is not { } name || !seen.Add(name)) continue;

            SparkLayerAttribute[] layers;
            SparkLayerDependenciesAttribute? dependencies;
            try
            {
                layers = assembly.GetCustomAttributes<SparkLayerAttribute>().ToArray();
                dependencies = assembly.GetCustomAttribute<SparkLayerDependenciesAttribute>();
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException) { continue; }

            if (layers.Length == 0) continue;
            found.Add(new SparkLibrary(
                layers[0].Alias,
                name,
                dependencies?.AssemblyNames ?? [],
                layers.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => new SparkLibraryLayer(l.Kind, l.Path, l.Json)).ToList()));
        }

        var duplicate = found.GroupBy(l => l.Alias, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            var names = duplicate.Select(l => l.AssemblyName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            throw new InvalidOperationException(
                $"Libraries '{names[0]}' and '{names[1]}' both declare the SparkLibraryAlias '{duplicate.Key}' (SPARK043). " +
                "An alias names a library's rights and ids, so it must be unique; remove one of the references.");
        }

        return SparkLibraryOrder.Sort(found, l => l.AssemblyName, l => l.DependsOn);
    }

    /// <summary>
    /// The assemblies <paramref name="entry"/> recorded as layered at build time, loaded by name; the
    /// reference walk when it recorded nothing.
    /// </summary>
    internal static IEnumerable<Assembly> LayeredAssemblies(Assembly? entry)
    {
        var recorded = entry?.GetCustomAttribute<SparkLayerAssembliesAttribute>();
        if (recorded is null)
        {
            // The reference walk alone repeats S1's pitfall: it starts from metadata references, which
            // the compiler trims, so a library whose types nothing loaded uses is missed. Measured
            // 2026-10-07 (layer-transport check, PRD §9): an application consuming the packages and
            // recording nothing lost core MintPlayer.Spark's actions and translations, because neither
            // it nor MintPlayer.Spark.Authorization references MintPlayer.Spark in metadata. The host's
            // trusted-platform list is built from deps.json, the same closure the compiler saw.
            return SparkAssemblies.SparkAware()
                .Concat(DeployedLayeredAssemblies(TrustedPlatformAssemblyPaths()))
                .ToList();
        }

        // A recorded name that does not load is a broken deployment, not something to skip quietly.
        return recorded.AssemblyNames.Select(name => Assembly.Load(new AssemblyName(name))).ToList();
    }

    /// <summary>The files the host resolved for this process from deps.json; empty for a single-file bundle.</summary>
    internal static IEnumerable<string> TrustedPlatformAssemblyPaths()
        => (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>
    /// The assemblies among <paramref name="paths"/> that carry <c>[assembly: SparkLayer]</c>, decided from
    /// their metadata so nothing else is loaded; each one is then loaded by name.
    /// </summary>
    internal static IEnumerable<Assembly> DeployedLayeredAssemblies(IEnumerable<string> paths)
    {
        var found = new List<Assembly>();
        foreach (var path in paths)
        {
            if (SparkAssemblies.IsPlatform(Path.GetFileNameWithoutExtension(path))) continue;

            AssemblyName? name;
            try { name = LayeredAssemblyName(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { continue; }
            if (name is null) continue;

            try { found.Add(Assembly.Load(name)); }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        }

        return found;
    }

    private static AssemblyName? LayeredAssemblyName(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;

        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly) return null;

        var definition = metadata.GetAssemblyDefinition();
        foreach (var handle in definition.GetCustomAttributes())
        {
            if (IsLayerAttribute(metadata, metadata.GetCustomAttribute(handle).Constructor))
                return definition.GetAssemblyName();
        }

        return null;
    }

    private static bool IsLayerAttribute(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle type;
        if (constructor.Kind == HandleKind.MemberReference)
            type = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        else if (constructor.Kind == HandleKind.MethodDefinition)
            type = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
        else
            return false;

        StringHandle ns, name;
        if (type.Kind == HandleKind.TypeReference)
        {
            var reference = metadata.GetTypeReference((TypeReferenceHandle)type);
            (ns, name) = (reference.Namespace, reference.Name);
        }
        else if (type.Kind == HandleKind.TypeDefinition)
        {
            var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)type);
            (ns, name) = (definition.Namespace, definition.Name);
        }
        else
            return false;

        return metadata.StringComparer.Equals(name, nameof(SparkLayerAttribute))
            && metadata.StringComparer.Equals(ns, typeof(SparkLayerAttribute).Namespace!);
    }
}
