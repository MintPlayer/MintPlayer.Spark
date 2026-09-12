using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Builds the request bodies the literal route table takes.
/// </summary>
/// <remarks>
/// <para>
/// Every Spark endpoint names its target in the body now — <c>POST /spark/po/create</c> with
/// <c>objectTypeId</c> in the JSON, rather than <c>POST /spark/po/{objectTypeId}</c>. Tests used to
/// interpolate the type into a URL and pass a body that said nothing about it; they now pass the type
/// through here instead.
/// </para>
/// <para>
/// ⚠️ <c>objectTypeId</c> is added at the <b>top level</b> and never inside <c>persistentObject</c>.
/// Those are two different things that now live one line apart in the same document: the first is the
/// request parameter the server authorizes, the second is part of the submitted object and is
/// overwritten on the way in. <c>TypeConflationTests</c> is the fact that says so; this helper is what
/// keeps every other test from blurring it by accident.
/// </para>
/// </remarks>
public static class Wire
{
    /// <summary>
    /// The given body with <c>objectTypeId</c> (and optionally <c>id</c>) added as request parameters.
    /// A null body becomes an object carrying only those.
    /// </summary>
    public static JsonNode Typed(object objectTypeId, object? body = null, string? id = null)
    {
        var node = body is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(body, body.GetType(), WebOptions)?.AsObject() ?? new JsonObject();

        node["objectTypeId"] = objectTypeId.ToString();
        if (id is not null)
            node["id"] = id;

        return node;
    }

    /// <summary>A <c>queries/get</c> or <c>queries/execute</c> body naming its query.</summary>
    public static JsonNode Query(object queryId, object? body = null)
    {
        var node = body is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(body, body.GetType(), WebOptions)?.AsObject() ?? new JsonObject();

        node["queryId"] = queryId.ToString();
        return node;
    }

    /// <summary>An <c>actions/execute</c> body naming its type and action.</summary>
    public static JsonNode Action(object objectTypeId, string actionName, object? body = null)
    {
        var node = Typed(objectTypeId, body).AsObject();
        node["actionName"] = actionName;
        return node;
    }

    // camelCase, because that is what the endpoints deserialize and what a hand-written anonymous
    // body in a test already looks like. Serializing with the default policy would send "AsDetailAttribute"
    // and the endpoint would bind null — silently, since every field on these requests is optional.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
}
