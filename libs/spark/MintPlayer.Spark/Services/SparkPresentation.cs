using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The boundary net of D13a: the one serialization step every persistent object in an outbound
/// response passes — the envelope, query rows, retry prompts, client operations, custom-action
/// results, streaming snapshots — checks that the object was presented for <em>this</em> request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a JSON contract hook, and not a result filter or a response writer.</b> A filter sees an
/// <c>IResult</c> whose value is an arbitrary envelope, and would have to walk object graphs by
/// reflection to find the persistent objects in it — and miss the ones in a 449 written by the
/// middleware, or in a WebSocket frame. The serializer already visits exactly what is written, once,
/// wherever it sits: <see cref="JsonTypeInfo.OnSerializing"/> on <see cref="PersistentObject"/> fires for
/// a top-level object, an <c>object</c>-typed <c>result</c>, a row in a list and an AsDetail row alike.
/// It is installed into the HTTP JSON options (every <c>Results.Json</c> and envelope) and into the
/// streaming endpoint's own options.
/// </para>
/// <para>
/// <b>Development throws, everything else prunes.</b> An object built with <c>new PersistentObject</c> or
/// through <c>AsSystem()</c> and then returned is a programming error, and a loud one is found on the
/// first run. Elsewhere the object is presented here, failing closed: attributes the caller may
/// neither query nor read are removed, edit-denied ones made read-only, and a warning names the type.
/// That fallback blocks on the rights decision, which a request has normally made already (the type
/// check resolved the caller's groups), so in practice it completes synchronously.
/// </para>
/// <para>
/// What the net does not do is the per-row <c>GetProtectedAttributesAsync</c> redaction: that needs the
/// stored document, and stays with the read paths that have it.
/// </para>
/// </remarks>
internal static class SparkPresentation
{
    private static readonly object CallerKey = new();
    private static readonly HttpContextAccessor Accessor = new();

    /// <summary>
    /// The token objects presented in this request carry. One per request, kept in
    /// <see cref="HttpContext.Items"/>, so neither a pooled context nor a second scope inside the same
    /// request changes the answer.
    /// </summary>
    public static object CallerOf(HttpContext httpContext)
    {
        if (!httpContext.Items.TryGetValue(CallerKey, out var token) || token is null)
            httpContext.Items[CallerKey] = token = new object();
        return token;
    }

    /// <summary>Installs the net into <paramref name="options"/>.</summary>
    public static void Install(JsonSerializerOptions options)
        => options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(Modify);

    private static void Modify(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(PersistentObject))
            typeInfo.OnSerializing = obj => Check((PersistentObject)obj);
    }

    /// <summary>The check itself.</summary>
    internal static void Check(PersistentObject po)
    {
        // No request, no caller: a test or a job serializing for its own use is not a response.
        if (Accessor.HttpContext is not { } httpContext || ReferenceEquals(po.PresentedFor, CallerOf(httpContext)))
            return;

        var services = httpContext.RequestServices;
        if (services.GetService<IHostEnvironment>()?.IsDevelopment() == true)
        {
            throw new InvalidOperationException(
                $"The persistent object '{po.Name}' ({po.ObjectTypeId}) reached the response without being presented for the caller. " +
                "Build it with IManager/IEntityMapper.GetPersistentObjectAsync or ToPersistentObjectAsync, or load it through IDatabaseAccess; " +
                "an object made with 'new PersistentObject' or through AsSystem() is not a presentation (D13a). " +
                "Outside Development this object would have been pruned for the caller.");
        }

        services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkPresentation))
            .LogWarning("Persistent object {Type} ({ObjectTypeId}) reached the response unpresented; pruned for the caller.", po.Name, po.ObjectTypeId);

        // Fail closed: what the caller may not query goes as well as what they may not read.
        var enforcement = services.GetRequiredService<IAttributeRightsEnforcement>();
        enforcement.PresentAsync([po], Abstractions.Authorization.SparkCoreActions.Query).GetAwaiter().GetResult();
        enforcement.PresentAsync([po], Abstractions.Authorization.SparkCoreActions.Read).GetAwaiter().GetResult();
    }
}
