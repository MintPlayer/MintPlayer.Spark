namespace MintPlayer.Spark.Abstractions.Requests;

/// <summary>
/// A request body that names the entity type it is about.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by every persistent-object and action request. The value is the <b>request parameter</b>
/// — the caller saying which type it wants to operate on — and it is what the server resolves and then
/// authorizes. It replaces the <c>{objectTypeId}</c> route segment that carried the same meaning before
/// the route table became fully literal.
/// </para>
/// <para>
/// ⚠️ It is <b>not</b> the same thing as <c>persistentObject.objectTypeId</c>, even though both are now
/// fields in the same JSON document. That one is part of the submitted <i>document</i> — the object
/// declaring what it is — and is overwritten with the resolved type on the way in. Authorizing against
/// it instead would let the written object nominate the permissions it is written under.
/// </para>
/// <para>
/// The distinction used to be impossible to miss, because one was a URL segment and the other was
/// inside the body. Now it is one word. <c>SparkRequestType.Resolve</c> is the only thing that reads
/// this property, and two tests hold the line:
/// <c>TypeConflationTests</c> (the behaviour) and <c>SparkRequestTypeSingleSourceTests</c> (the shape).
/// </para>
/// </remarks>
public interface ISparkTypedRequest
{
    /// <summary>The entity type id or alias this request is about.</summary>
    string? ObjectTypeId { get; }
}
