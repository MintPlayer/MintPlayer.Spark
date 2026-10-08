using System.Text.Json;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// The message the external-login popup page posts to its opener, read back out of the page.
/// The page embeds it as a JSON object (<c>var msg = {...};</c>, #490 M1), so tests parse it and
/// assert on its fields instead of string-matching one particular serialization.
/// </summary>
internal sealed record ExternalLoginPopupPayload(string? Type, bool Success, string? Error, string? Nonce)
{
    private static readonly Regex MessageLiteral = new(@"var msg = (\{.*?\});", RegexOptions.Singleline);

    /// <summary>Parses the payload out of the popup HTML; throws when the page carries none.</summary>
    public static ExternalLoginPopupPayload Parse(string popupHtml)
    {
        var match = MessageLiteral.Match(popupHtml);
        if (!match.Success)
            throw new InvalidOperationException($"The popup page carries no 'var msg = {{...}};' payload: {popupHtml}");

        using var json = JsonDocument.Parse(match.Groups[1].Value);
        var root = json.RootElement;
        return new ExternalLoginPopupPayload(
            StringOrNull(root, "type"),
            root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True,
            StringOrNull(root, "error"),
            StringOrNull(root, "nonce"));
    }

    /// <summary>The payload's error code, or <c>"&lt;none&gt;"</c> when it reports none.</summary>
    public static string ErrorFrom(string popupHtml) => Parse(popupHtml).Error ?? "<none>";

    private static string? StringOrNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
