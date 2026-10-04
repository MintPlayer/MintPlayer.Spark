using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EShowedOn
{
    /// <summary>
    /// Drawn on no page (Vidyano's <c>Never</c>, #264). The attribute still ships on the PersistentObject, so
    /// custom client code can read it, and an action can show it for one object by setting the runtime
    /// <c>PersistentObjectAttribute.ShowedOn</c> in <c>OnLoad</c>/<c>OnNew</c>/<c>OnRefresh</c>. Layout only:
    /// ⚠️ it does not protect the value, so pair it with <c>isReadOnly</c> or a deny when it is not user input.
    /// Synchronize keeps an explicit <c>None</c>; only an absent <c>showedOn</c> is derived.
    /// </summary>
    None = 0,
    Query = 1,
    PersistentObject = 2,
}
