using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Base for the discriminated union of backend-issued instructions that the frontend
/// executes after an action completes. Wire discriminator is the <c>type</c> property.
/// See <c>docs/PRD-ClientInstructions.md</c> for the full design.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NavigateInstruction),         "navigate")]
[JsonDerivedType(typeof(NotifyInstruction),           "notify")]
[JsonDerivedType(typeof(RefreshAttributeInstruction), "refreshAttribute")]
[JsonDerivedType(typeof(RefreshQueryInstruction),     "refreshQuery")]
[JsonDerivedType(typeof(DisableActionInstruction),    "disableAction")]
[JsonDerivedType(typeof(RetryInstruction),            "retry")]
public abstract class ClientInstruction { }
