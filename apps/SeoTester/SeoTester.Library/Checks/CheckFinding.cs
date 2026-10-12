using System.Text.Json;
using SeoTester.Scoring;

namespace SeoTester.Checks;

/// <summary>
/// One check's result on one page, or once for the site (<c>docs/seo_analyzer_PRD.md</c> §4.2, §7). Texts are never
/// stored: <see cref="Detail"/> holds the catalogue summary's placeholders (<c>url</c>, <c>n</c>, <c>k</c>,
/// <c>value</c>, <c>target</c>, <c>status</c>, <c>days</c>), from which the report renders the sentence in either
/// language.
/// </summary>
public sealed record CheckFinding(string CheckId, CheckStatus Status, IReadOnlyDictionary<string, string>? Detail = null)
{
    /// <summary>Longest value kept per placeholder: findings are stored per page and must stay small (§4.2).</summary>
    public const int MaxValueLength = 200;

    /// <summary>
    /// The elements behind a failing or warning finding (<c>docs/spark_mcp_PRD.md</c> §4.7 gap 1), as CSS selectors, where
    /// the check judges elements; null for a check of headers, the response or the whole page. See <see cref="Html.ElementEvidence"/>.
    /// </summary>
    public Html.ElementEvidence? Evidence { get; init; }

    /// <summary><see cref="Detail"/> as a short JSON object, or null.</summary>
    public string? DetailJson => Detail is { Count: > 0 } ? JsonSerializer.Serialize(Detail) : null;

    public static IReadOnlyDictionary<string, string>? ParseDetail(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Builds findings tersely, cutting every value to <see cref="CheckFinding.MaxValueLength"/>.</summary>
public static class Finding
{
    public static CheckFinding Pass(string id, params (string Key, object? Value)[] detail) => Make(id, CheckStatus.Pass, detail);
    public static CheckFinding Warn(string id, params (string Key, object? Value)[] detail) => Make(id, CheckStatus.Warn, detail);
    public static CheckFinding Fail(string id, params (string Key, object? Value)[] detail) => Make(id, CheckStatus.Fail, detail);
    public static CheckFinding NotApplicable(string id) => new(id, CheckStatus.NotApplicable);
    public static CheckFinding Error(string id, string? why = null) => Make(id, CheckStatus.Error, why is null ? [] : [("value", why)]);

    /// <summary>Pass when <paramref name="ok"/>, else <paramref name="otherwise"/>.</summary>
    public static CheckFinding When(bool ok, string id, CheckStatus otherwise, params (string Key, object? Value)[] detail)
        => Make(id, ok ? CheckStatus.Pass : otherwise, detail);

    /// <summary>
    /// <paramref name="finding"/> with the evidence of <paramref name="elements"/> when it fails or warns (a pass needs
    /// none, and stored evidence costs space on every page: spike S7).
    /// </summary>
    public static CheckFinding With(this CheckFinding finding, IEnumerable<AngleSharp.Dom.IElement> elements)
        => finding.Status is CheckStatus.Fail or CheckStatus.Warn && Html.ElementEvidence.Of(elements) is { } evidence
            ? finding with { Evidence = evidence }
            : finding;

    /// <summary><paramref name="finding"/> with <paramref name="evidence"/> when it fails or warns.</summary>
    public static CheckFinding With(this CheckFinding finding, Html.ElementEvidence? evidence)
        => finding.Status is CheckStatus.Fail or CheckStatus.Warn && evidence is not null
            ? finding with { Evidence = evidence }
            : finding;

    public static CheckFinding Make(string id, CheckStatus status, (string Key, object? Value)[] detail)
    {
        Dictionary<string, string>? values = null;
        foreach (var (key, value) in detail)
        {
            if (value is null)
                continue;
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            (values ??= new(StringComparer.Ordinal))[key] = text.Length > CheckFinding.MaxValueLength ? text[..CheckFinding.MaxValueLength] : text;
        }
        return new CheckFinding(id, status, values);
    }
}
