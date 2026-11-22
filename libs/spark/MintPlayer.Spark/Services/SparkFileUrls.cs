using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using System.Text.Json.Serialization.Metadata;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The <c>url</c> of each stored file in a read response (#700 D15, spike SP1), signed beside the boundary net.
/// </summary>
/// <remarks>
/// <para>
/// When a persistent object is serialized for the current request, and after <see cref="SparkPresentation.Check"/> made
/// sure it was presented for the caller, <see cref="Sign"/> walks the attributes it still carries and asks the
/// <see cref="ISparkFileUrlSigner"/> for each file and variant. The answers are kept per request, by instance, and the
/// <c>url</c> property added to <see cref="SparkFile"/> and <see cref="SparkImageVariant"/> reads them back as the value is
/// written. A file never written as part of a presented object (a custom DTO, a job) has no entry and gets no URL.
/// </para>
/// <para>
/// The URL is never stored: it is not a member of the type, so the RavenDB serializer never sees it, and the HTTP
/// serializer only writes it.
/// </para>
/// </remarks>
internal static class SparkFileUrls
{
    private static readonly object TableKey = new();

    /// <summary>Signs the files <paramref name="po"/> carries, for the request in <paramref name="httpContext"/>.</summary>
    public static void Sign(PersistentObject po, HttpContext? httpContext)
    {
        if (httpContext is null || string.IsNullOrEmpty(po.Id)
            || httpContext.RequestServices.GetService<ISparkFileUrlSigner>() is not { } signer)
            return;

        var table = TableOf(httpContext, create: true)!;
        if (!httpContext.Items.ContainsKey(RootKey))
            httpContext.Items[RootKey] = po;
        Walk(po, po.ObjectTypeId, po.Id, nested: false, signer, table);
    }

    /// <summary>
    /// Signs the files of one query cell (<see cref="QueryResultItemValue.FileOwner"/> set by the projector, after row
    /// security and redaction), under the column's name as the attribute.
    /// </summary>
    public static void SignCell(QueryResultItemValue cell, HttpContext? httpContext)
    {
        if (httpContext is null || cell.FileOwner is not { } owner || string.IsNullOrEmpty(owner.EntityId)
            || httpContext.RequestServices.GetService<ISparkFileUrlSigner>() is not { } signer)
            return;

        var table = TableOf(httpContext, create: true)!;
        if (!httpContext.Items.ContainsKey(RootKey))
            httpContext.Items[RootKey] = cell;
        switch (cell.Value)
        {
            case SparkFile file:
                Add(file, owner.ObjectTypeId, owner.EntityId, cell.Key, signer, table);
                break;
            case IEnumerable<SparkFile> files:
                foreach (var file in files)
                    Add(file, owner.ObjectTypeId, owner.EntityId, cell.Key, signer, table);
                break;
        }
    }

    /// <summary>
    /// Forgets the URLs once the outermost object (a persistent object or a query cell) is written: a live socket keeps one
    /// request for its whole life, and its table must not grow with every frame.
    /// </summary>
    public static void Release(object written, HttpContext? httpContext)
    {
        if (httpContext is null || !httpContext.Items.TryGetValue(RootKey, out var root) || !ReferenceEquals(root, written))
            return;
        httpContext.Items.Remove(RootKey);
        TableOf(httpContext, create: false)?.Clear();
    }

    private static readonly object RootKey = new();

    private static void Walk(PersistentObject po, Guid objectTypeId, string entityId, bool nested, ISparkFileUrlSigner signer, Dictionary<object, string?> table)
    {
        foreach (var attribute in po.Attributes)
        {
            if (attribute is PersistentObjectAttributeAsDetail asDetail)
            {
                // Files inside an AsDetail row live on the parent's document. The parent is written first, so its walk
                // claims these values before the row's own serialization would.
                if (asDetail.Object is { } row)
                    Walk(row, objectTypeId, entityId, nested: true, signer, table);
                foreach (var item in asDetail.Objects ?? [])
                    Walk(item, objectTypeId, entityId, nested: true, signer, table);
                continue;
            }

            var name = nested ? null : attribute.Name;
            switch (attribute.Value)
            {
                case SparkFile file:
                    Add(file, objectTypeId, entityId, name, signer, table);
                    break;
                case IEnumerable<SparkFile> files:
                    foreach (var file in files)
                        Add(file, objectTypeId, entityId, name, signer, table);
                    break;
            }
        }
    }

    private static void Add(SparkFile? file, Guid objectTypeId, string entityId, string? attribute, ISparkFileUrlSigner signer, Dictionary<object, string?> table)
    {
        if (file is null || table.ContainsKey(file))
            return;
        table[file] = signer.Sign(new SparkFileLocation(objectTypeId, entityId, attribute, file, null));
        if (file is SparkImage image)
        {
            foreach (var (variantName, variant) in image.Variants)
            {
                if (variant is not null && !table.ContainsKey(variant))
                    table[variant] = signer.Sign(new SparkFileLocation(objectTypeId, entityId, attribute, file, variantName));
            }
        }
    }

    /// <summary>Adds the write-only <c>url</c> property to a file or variant contract.</summary>
    public static void AddUrlProperty(JsonTypeInfo typeInfo, IHttpContextAccessor accessor)
    {
        if (typeInfo.Properties.Any(p => p.Name == "url"))
            return;
        var url = typeInfo.CreateJsonPropertyInfo(typeof(string), "url");
        url.Get = value => accessor.HttpContext is { } httpContext && TableOf(httpContext, create: false) is { } table
            && table.TryGetValue(value, out var signed) ? signed : null;
        url.ShouldSerialize = (_, value) => value is not null;
        typeInfo.Properties.Add(url);
    }

    private static Dictionary<object, string?>? TableOf(HttpContext httpContext, bool create)
    {
        if (httpContext.Items.TryGetValue(TableKey, out var existing) && existing is Dictionary<object, string?> table)
            return table;
        if (!create)
            return null;
        table = new Dictionary<object, string?>(ReferenceEqualityComparer.Instance);
        httpContext.Items[TableKey] = table;
        return table;
    }
}
