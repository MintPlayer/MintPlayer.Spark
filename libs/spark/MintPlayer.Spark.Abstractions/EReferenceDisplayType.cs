using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>How a Reference attribute's value is picked on the edit page.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EReferenceDisplayType
{
    /// <summary>
    /// Renders as a dropdown/select element listing every referenced item.
    /// </summary>
    Dropdown,

    /// <summary>
    /// Renders as a readonly textbox with a "…" button that opens a searchable modal grid picker.
    /// </summary>
    Modal
}
