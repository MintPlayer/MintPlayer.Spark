namespace MintPlayer.Spark.Apps;

/// <summary>
/// Declares a webhook event. The type is the event's <c>data</c>; its JSON schema is published in the catalog.
/// <code>
/// [WebhookEvent("report.completed", Permission = "reports:read", Description = "A coverage report finished processing.")]
/// public sealed record ReportCompleted(string ReportId, decimal LineRate);
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class WebhookEventAttribute(string name) : Attribute
{
    /// <summary>The event type on the wire (<c>type</c> in the envelope), e.g. <c>report.completed</c>.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// The App permission an installation needs to receive the event (D6). Direct webhooks ignore it: their owner
    /// sees their own data. Null: every installation of the owner receives it.
    /// </summary>
    public string? Permission { get; set; }

    /// <summary>Shown in the catalog and the settings page.</summary>
    public string? Description { get; set; }
}
