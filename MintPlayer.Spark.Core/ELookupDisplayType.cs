namespace MintPlayer.Spark.Abstractions;

public enum ELookupDisplayType
{
    /// <summary>
    /// Renders as a dropdown/select element
    /// </summary>
    Dropdown,

    /// <summary>
    /// Renders as a readonly textbox with a modal selector button
    /// </summary>
    Modal,

    /// <summary>
    /// Renders as a multiselect dropdown for flags/multi-value selection.
    /// The value is stored as a comma-separated string (e.g., "Query, PersistentObject").
    /// </summary>
    Multiselect
}
