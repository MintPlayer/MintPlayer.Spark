namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// A library's <c>App_Data/actions.json</c>, compiled into the assembly as raw JSON (#467, D7/S12).
/// Emitted by the Spark source generator; never written by hand.
/// </summary>
/// <remarks>
/// The text is passed through unparsed: the generator's JSON reader handles strings only, and an
/// action has numbers (<c>offset</c>) and booleans (<c>refreshOnCompleted</c>). Read at run time by
/// reflection, core first and then by assembly name, and at build time by the analyzers.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class SparkActionsAttribute : Attribute
{
    public SparkActionsAttribute(string json)
    {
        Json = json;
    }

    public string Json { get; }
}
