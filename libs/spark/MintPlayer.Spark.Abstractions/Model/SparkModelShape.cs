using MintPlayer.Spark.Abstractions.Reflection;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// One entity in the model shape: its CLR type plus the index/projection facts that change what
/// gets generated for it.
/// </summary>
public readonly record struct SparkModelType(Type Type, string? QueryTypeName, string? IndexName);

/// <summary>
/// Renders the model's <em>shape</em> — the part derived from the CLR entity classes — as canonical
/// text, and hashes it.
///
/// <para>
/// This exists so an application can verify at startup that its <c>App_Data/Model/*.json</c> still
/// describes its entity classes, <b>without</b> the model generator being present. The hash covers
/// the generator's <i>inputs</i>, never its output, which is what makes generator-free verification
/// possible.
/// </para>
///
/// <para>
/// Consequently the hash deliberately ignores everything a human may author in the model JSON —
/// labels and translations, renderer and renderer options, group, edit mode, column span,
/// visibility, order, rules, attribute ids, hand-added attributes with no CLR property, and inline
/// queries. Those are all supported edits; reacting to them would mean a translated label could stop
/// an application from starting.
/// </para>
///
/// <para>
/// <b>Determinism is a safety property here, not a nicety.</b> A hash that varies between runs of
/// the same code does not cause a merge conflict — it stops a healthy application from starting, on
/// some machines and not others. Reflection does not guarantee member order (measured: reordering
/// the files of a <c>partial</c> class changes <see cref="Type.GetProperties()"/> order), so
/// everything here is ordinal-sorted, written with an explicit UTF-8 encoding and a hardcoded
/// <c>\n</c>, and never touches the current culture. Do not "simplify" any of that away.
/// </para>
/// </summary>
public static class SparkModelShape
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Canonical text for one entity. This text <em>is</em> the hash input, and it is kept readable
    /// on purpose: when the startup check fires and someone disputes it, being able to dump and diff
    /// this is the difference between a five-minute fix and an outage.
    /// </summary>
    public static string Describe(SparkModelType modelType)
    {
        var builder = new StringBuilder();
        var type = modelType.Type;

        builder.Append("type\t").Append(type.FullName ?? type.Name).Append('\n');

        if (!string.IsNullOrEmpty(modelType.QueryTypeName))
            builder.Append("  querytype\t").Append(modelType.QueryTypeName).Append('\n');

        if (!string.IsNullOrEmpty(modelType.IndexName))
            builder.Append("  index\t").Append(modelType.IndexName).Append('\n');

        // The [Breadcrumb] attribute is authoritative — the synchronizer overwrites the JSON with it —
        // so it is part of the shape. A breadcrumb authored only in JSON is not.
        var breadcrumb = type.GetCustomAttribute<BreadcrumbAttribute>(inherit: true);
        if (breadcrumb != null)
            builder.Append("  breadcrumb\t").Append(NormalizeNewlines(breadcrumb.Template)).Append('\n');

        foreach (var property in type.GetSparkModelProperties().OrderBy(p => p.Name, StringComparer.Ordinal))
            AppendProperty(builder, property);

        return builder.ToString();
    }

    private static void AppendProperty(StringBuilder builder, PropertyInfo property)
    {
        var elementType = GetCollectionElementType(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType);
        var reference = property.GetCachedCustomAttribute<ReferenceAttribute>();

        // The DERIVED data type is hashed, not the CLR type name. int and long both generate
        // "number", and List<string> and string[] generate identical JSON — so neither may move the
        // hash. Under a refuse-to-start policy, a false alarm for a change that alters nothing is
        // the fastest way to teach operators to reach for the override.
        var dataType = reference != null ? "Reference" : GetDataType(property.PropertyType);

        builder.Append("  prop\t").Append(property.Name)
               .Append('\t').Append(dataType)
               .Append('\t').Append(elementType != null ? "array" : "single")
               .Append('\t').Append(property.CanWrite ? "rw" : "ro")
               .Append('\t').Append(IsNullable(property.PropertyType) ? "nullable" : "required");

        if (dataType == "AsDetail" && (elementType ?? property.PropertyType) is { } detailType)
            builder.Append("\tdetail=").Append(detailType.FullName ?? detailType.Name);

        if (reference != null)
        {
            builder.Append("\tref=").Append(reference.TargetType.FullName ?? reference.TargetType.Name);
            if (!string.IsNullOrEmpty(reference.Query))
                builder.Append("\trefquery=").Append(reference.Query);
        }

        if (property.GetCachedCustomAttribute<LookupReferenceAttribute>() is { } lookup)
            builder.Append("\tlookup=").Append(lookup.LookupType.Name);

        if (property.GetCachedCustomAttribute<SortableAttribute>() != null)
            builder.Append("\tsortable");

        builder.Append('\n');
    }

    /// <summary>Hash of one entity's shape.</summary>
    public static string ComputeEntityHash(SparkModelType modelType) => Sha256Hex(Describe(modelType));

    /// <summary>
    /// Per-entity hashes, keyed by the entity's simple type name — the same name the model file uses.
    /// <para>
    /// Sharded rather than one value so a drift message can name the entity that moved, and so two
    /// pull requests touching different entities land on different lines of a sorted map and merge
    /// without conflict.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComputePerEntityHashes(IEnumerable<SparkModelType> types)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var modelType in types)
            result[modelType.Type.Name] = ComputeEntityHash(modelType);
        return result;
    }

    /// <summary>
    /// Hash of the set of queryable roots. Catches a <em>removed</em> root, which per-entity hashes
    /// cannot see on their own: the orphaned model file and its CLR class both still exist and still
    /// agree with each other.
    /// </summary>
    public static string ComputeContextRootsHash(IEnumerable<string> rootEntityNames)
        => Sha256Hex(string.Join("\n", rootEntityNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)));

    /// <summary>
    /// Roll-up over the per-entity hashes, the context roots and the on-disk model files, so the
    /// common case is a single comparison.
    /// <para>
    /// The file hash is folded in deliberately: the entity hashes only describe what the CLR classes
    /// say the model should contain, so on their own they would not notice a file planted in the
    /// model directory — and the loader reads whatever is in that directory. Including it means an
    /// added, removed or altered model file invalidates the roll-up and the application refuses to
    /// start.
    /// </para>
    /// </summary>
    public static string ComputeModelHash(
        IReadOnlyDictionary<string, string> perEntityHashes,
        string contextRootsHash,
        string modelFilesHash)
    {
        var builder = new StringBuilder();
        foreach (var entry in perEntityHashes.OrderBy(e => e.Key, StringComparer.Ordinal))
            builder.Append(entry.Key).Append(':').Append(entry.Value).Append('\n');
        builder.Append("roots:").Append(contextRootsHash).Append('\n');
        builder.Append("files:").Append(modelFilesHash).Append('\n');
        return Sha256Hex(builder.ToString());
    }

    private static string Sha256Hex(string canonicalText)
        => Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(canonicalText)));

    /// <summary>
    /// Collapses CRLF and lone CR to LF. Author-supplied text such as a <c>[Breadcrumb]</c> template
    /// is the only way a newline reaches this hash, but the cost of an unnormalised one is a
    /// deployment that refuses to start on Linux after the hash was written on Windows.
    /// </summary>
    private static string? NormalizeNewlines(string? value)
        => value?.Replace("\r\n", "\n").Replace("\r", "\n");

    // --- type classification (single definition, shared with the model generator) ---------------

    /// <summary>
    /// Maps a CLR property type onto the model's data-type vocabulary. Collections are classified by
    /// their ELEMENT type, never the wrapper: a <c>List&lt;&gt;</c> is itself a class with public
    /// properties, so classifying the wrapper would mis-tag <c>List&lt;string&gt;</c> as AsDetail and
    /// the mapper would drop its values.
    /// </summary>
    public static string GetDataType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        var elementType = GetCollectionElementType(underlying);
        if (elementType != null)
            return IsComplexType(elementType) ? "AsDetail" : GetDataType(elementType);

        return underlying switch
        {
            _ when underlying == typeof(string) => "string",
            _ when underlying == typeof(int) || underlying == typeof(long) => "number",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "decimal",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "datetime",
            _ when underlying == typeof(DateOnly) => "date",
            _ when underlying == typeof(Guid) => "guid",
            _ when underlying == typeof(System.Drawing.Color) => "color",
            _ when IsComplexType(underlying) => "AsDetail",
            _ => "string"
        };
    }

    /// <summary>
    /// Element type of an array or generic collection; <see langword="null"/> when the type is not a
    /// collection.
    /// </summary>
    public static Type? GetCollectionElementType(Type type)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("SparkModelShape.CollectionElement", type),
            static k => ResolveCollectionElementType(k.Type));

    private static Type? ResolveCollectionElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elementType = iface.GetGenericArguments()[0];
                // string implements IEnumerable<char>; it is not a collection for our purposes.
                if (elementType != typeof(char))
                    return elementType;
            }
        }

        return null;
    }

    /// <summary>A class (other than string) that has properties of its own.</summary>
    public static bool IsComplexType(Type type)
    {
        if (type == typeof(string) || type.IsValueType || type.IsEnum || type.IsPrimitive)
            return false;

        return type.GetCachedProperties().Length > 0;
    }

    /// <summary>Whether a value of this type may be absent.</summary>
    public static bool IsNullable(Type type)
        => Nullable.GetUnderlyingType(type) != null || !type.IsValueType;
}
