using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Base for the discriminated union of backend-issued operations that the frontend
/// executes after an action completes. Wire discriminator is the <c>type</c> property.
/// See <c>docs/prd/PRD-ClientOperations.md</c> for the full design.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NavigateOperation),         "navigate")]
[JsonDerivedType(typeof(NotifyOperation),           "notify")]
[JsonDerivedType(typeof(RefreshAttributeOperation), "refreshAttribute")]
[JsonDerivedType(typeof(RefreshQueryOperation),     "refreshQuery")]
// `disableAction` is gone (#460, D13): a disabled action is decided by OnDisableActionsAsync and
// travels on the object's or result's DisabledActions, where the submit path enforces it too.
[JsonDerivedType(typeof(RetryOperation),            "retry")]
[JsonDerivedType(typeof(ShowSecretOperation),       "showSecret")]
public abstract class ClientOperation { }
