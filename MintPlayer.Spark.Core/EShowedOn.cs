using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EShowedOn
{
    Query = 1,
    PersistentObject = 2,
}
