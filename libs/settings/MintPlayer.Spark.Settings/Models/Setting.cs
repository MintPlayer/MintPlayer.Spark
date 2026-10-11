namespace MintPlayer.Spark.Settings.Models;

/// <summary>
/// One declared setting as an administrator sees and edits it: the <c>Settings</c> collection, id
/// <c>Settings/{Key}</c> (<c>docs/seo_analyzer_PRD.md</c> X9). The startup seeder writes one per declared
/// <see cref="SparkSetting{T}"/> and keeps <see cref="Type"/>, <see cref="Default"/> and
/// <see cref="Description"/> in step with the code; only <see cref="Value"/> is the administrator's.
/// </summary>
public class Setting
{
    /// <summary>The document id, <c>Settings/{Key}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The configuration key the setting overrides, e.g. <c>Seo:Limits:MaxPages</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The stored override, written as configuration would be (<c>25</c>, <c>true</c>, <c>00:05:00</c>). Empty: not overridden, so configuration or the default applies.</summary>
    public string? Value { get; set; }

    /// <summary>The value type declared in code, e.g. <c>Int32</c> or <c>TimeSpan</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The value declared in code, used when neither a stored value nor configuration has one.</summary>
    public string? Default { get; set; }

    /// <summary>What the setting does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The document id of the setting <paramref name="key"/>.</summary>
    public static string IdFor(string key) => "Settings/" + key;
}
