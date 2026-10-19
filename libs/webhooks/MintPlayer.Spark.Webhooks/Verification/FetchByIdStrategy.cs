using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace MintPlayer.Spark.Webhooks;

/// <summary>Where <see cref="FetchByIdStrategy"/> finds the resource id, and what it accepts.</summary>
public sealed class FetchByIdOptions
{
    /// <summary>
    /// The field that carries the id: a form field for <c>application/x-www-form-urlencoded</c>
    /// bodies (Mollie posts <c>id=tr_…</c>), a top-level string property for JSON bodies. Default <c>id</c>.
    /// </summary>
    public string Field { get; set; } = "id";

    /// <summary>
    /// The shape an id must have. A delivery whose id does not match is refused, so an arbitrary
    /// string never reaches the provider's API as a path segment. Default: 1–128 ASCII letters,
    /// digits, <c>_</c> and <c>-</c>.
    /// </summary>
    public Regex Pattern { get; set; } = DefaultPattern();

    private static Regex DefaultPattern() => new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

/// <summary>
/// For providers that sign nothing and send only an id (Mollie): the delivery is accepted as a
/// <b>hint</b>, never as data. <see cref="WebhookVerification.PayloadVerified"/> is
/// <see langword="false"/> and <see cref="WebhookVerification.ResourceId"/> holds the id; the
/// handler must fetch the resource from the provider, with the right credentials, and act only on
/// what the provider returns.
/// </summary>
/// <remarks>
/// <para>
/// That is the whole security model, and it is sound: anyone may POST an id, but all a forged
/// delivery can do is make the handler look up the current state of a real resource, which is what
/// a genuine delivery does too. Handlers must therefore be idempotent and act on the fetched state.
/// </para>
/// <para>
/// Do not deduplicate a fetch-by-id source on the resource id. Mollie sends the same payment id for
/// every status change, so a key on the id would drop every update after the first. Leave
/// <see cref="WebhookSourceBuilder.DeliveryId"/> unset: each delivery is enqueued, and the fetch
/// makes a repeat harmless.
/// </para>
/// </remarks>
public sealed class FetchByIdStrategy : IWebhookSignatureStrategy
{
    private readonly FetchByIdOptions options;

    /// <summary>A strategy with the given options, or the defaults (<c>id</c>, the default pattern).</summary>
    public FetchByIdStrategy(FetchByIdOptions? options = null)
    {
        this.options = options ?? new FetchByIdOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(this.options.Field);
        ArgumentNullException.ThrowIfNull(this.options.Pattern);
    }

    /// <inheritdoc />
    public WebhookVerification Verify(WebhookRequest request)
    {
        var id = IsJson(request.ContentType) ? FromJson(request.Body) : FromForm(request.Body);

        if (string.IsNullOrEmpty(id))
            return WebhookVerification.Rejected(WebhookRejectionReason.MissingResourceId);

        bool matches;
        try
        {
            matches = options.Pattern.IsMatch(id);
        }
        catch (RegexMatchTimeoutException)
        {
            matches = false;
        }

        return matches
            ? WebhookVerification.FetchRequired(id)
            : WebhookVerification.Rejected(WebhookRejectionReason.MissingResourceId);
    }

    private static bool IsJson(string? contentType)
        => contentType is not null
            && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private string? FromForm(byte[] body)
    {
        var fields = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(body));
        return fields.TryGetValue(options.Field, out var value) && value.Count == 1 ? value[0] : null;
    }

    private string? FromJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(options.Field, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
