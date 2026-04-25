using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Base for the discriminated union of backend-issued operations that the frontend
/// executes after an action completes. Wire discriminator is the <c>type</c> property.
/// See <c>docs/PRD-ClientOperations.md</c> for the full design.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NavigateOperation),         "navigate")]
[JsonDerivedType(typeof(NotifyOperation),           "notify")]
[JsonDerivedType(typeof(RefreshAttributeOperation), "refreshAttribute")]
[JsonDerivedType(typeof(RefreshQueryOperation),     "refreshQuery")]
[JsonDerivedType(typeof(DisableActionOperation),    "disableAction")]
[JsonDerivedType(typeof(RetryOperation),            "retry")]
public abstract class ClientOperation { }
