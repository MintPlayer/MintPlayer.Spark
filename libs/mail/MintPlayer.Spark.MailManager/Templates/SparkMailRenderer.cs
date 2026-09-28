using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mjml.Net;
using Scriban;
using Scriban.Runtime;

namespace MintPlayer.Spark.MailManager.Templates;

/// <summary>A rendered mail.</summary>
/// <param name="Subject">From the template's <c>&lt;mj-title&gt;</c>, rendered, decoded, on one line.</param>
/// <param name="Html">The MJML output.</param>
/// <param name="Text">The <c>.txt</c> part when the template has one, otherwise text derived from the HTML.</param>
/// <param name="Template">The <c>.mjml</c> file that was used.</param>
/// <param name="Warnings">MJML validation messages (the mail is still sent; the render helper fails on them).</param>
public sealed record SparkRenderedMail(string Subject, string Html, string Text, SparkMailTemplateFile Template, IReadOnlyList<string> Warnings);

/// <summary>A template that cannot be rendered — never fixed by a retry.</summary>
public sealed class SparkMailTemplateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Scriban, then MJML (Mjml.Net). The facts it is built on were measured in spike S-M4:
/// <list type="bullet">
/// <item><c>StrictVariables</c> refuses an unknown top-level name, but a missing <em>member</em> renders
/// empty unless <c>EnableRelaxedMemberAccess</c> is off — both are set, so a typo fails the render.</item>
/// <item>Scriban escapes nothing, so every string value is HTML-escaped before the template sees it
/// (the <c>.txt</c> part gets the raw values).</item>
/// <item>A <c>JsonElement</c> is opaque to Scriban (members read as null), so data is converted to
/// <see cref="ScriptObject"/>s first.</item>
/// <item>Without a pushed culture Scriban formats invariantly, not with the thread's culture; the
/// recipient's culture is pushed.</item>
/// </list>
/// <c>mj-include</c> paths resolve relative to the including file, through the same culture chain and
/// the same Scriban context, so a partial may use the template's variables.
/// </summary>
public sealed partial class SparkMailRenderer(ISparkMailTemplateResolver resolver)
{
    private readonly ConcurrentDictionary<string, Template> parsed = new(StringComparer.Ordinal);

    private ISparkMailTemplateResolver Resolver => resolver;

    /// <summary>
    /// Renders <paramref name="template"/> for <paramref name="culture"/> with <paramref name="data"/>
    /// (a JSON object; its members are the template's variables).
    /// </summary>
    /// <exception cref="SparkMailTemplateException">No such template, a Scriban error, or no <c>&lt;mj-title&gt;</c>.</exception>
    public Task<SparkRenderedMail> RenderAsync(string template, CultureInfo culture, JsonElement? data, CancellationToken cancellationToken = default)
        => RenderAsync(template, culture, data, sources: null, cancellationToken);

    /// <summary>As <see cref="RenderAsync(string, CultureInfo, JsonElement?, CancellationToken)"/>; <paramref name="sources"/> receives the Scriban output of the main file (key "") and of every include (key = include path) — spike S-M2 feeds them to npm mjml.</summary>
    internal async Task<SparkRenderedMail> RenderAsync(string template, CultureInfo culture, JsonElement? data, IDictionary<string, string>? sources, CancellationToken cancellationToken)
    {
        var file = resolver.Resolve(template, ".mjml", culture)
            ?? throw new SparkMailTemplateException($"No mail template '{template}' (neither '{template}.mjml' nor a culture variant of it).");

        var escaped = ToScriptObject(data, escapeHtml: true);
        var mjmlSource = await RenderScribanAsync(file, escaped, culture, cancellationToken);

        var subjectMatch = TitleRegex().Match(mjmlSource);
        if (!subjectMatch.Success)
            throw new SparkMailTemplateException($"Mail template '{file.Path}' ({file.Source}) has no <mj-title>; it is the subject.");
        var subject = WhitespaceRegex().Replace(WebUtility.HtmlDecode(subjectMatch.Groups[1].Value), " ").Trim();

        var directory = file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : string.Empty;
        sources?.Add(string.Empty, mjmlSource);
        var loader = new IncludeLoader(this, directory, escaped, culture, sources);
        RenderResultLike result;
        try
        {
            var rendered = new MjmlRenderer().Render(mjmlSource, new MjmlOptions { Beautify = false, FileLoader = () => loader });
            result = new RenderResultLike(rendered.Html, [.. rendered.Errors.Select(e => $"{e.Error} (line {e.Position.LineNumber})")]);
        }
        catch (Exception ex) when (ex is not SparkMailTemplateException)
        {
            throw new SparkMailTemplateException($"Mail template '{file.Path}' ({file.Source}) is not valid MJML: {ex.Message}", ex);
        }
        if (loader.Failure is { } includeFailure)
            throw includeFailure;

        string text;
        if (resolver.Resolve(template, ".txt", culture) is { } textFile)
            text = await RenderScribanAsync(textFile, ToScriptObject(data, escapeHtml: false), culture, cancellationToken);
        else
            text = HtmlToText(result.Html);

        return new SparkRenderedMail(subject, result.Html, text.Trim(), file, result.Warnings);
    }

    private sealed record RenderResultLike(string Html, IReadOnlyList<string> Warnings);

    private async Task<string> RenderScribanAsync(SparkMailTemplateFile file, ScriptObject data, CultureInfo culture, CancellationToken cancellationToken)
    {
        var template = Parse(file);
        var context = new TemplateContext
        {
            StrictVariables = true,
            EnableRelaxedMemberAccess = false,
            CancellationToken = cancellationToken,
        };
        context.PushCulture(culture);
        context.PushGlobal(data);
        try
        {
            return await template.RenderAsync(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SparkMailTemplateException($"Mail template '{file.Path}' ({file.Source}) failed to render: {ex.Message}", ex);
        }
    }

    /// <summary>Parses (and caches) a file; throws on a syntax error. Startup validation calls it for every file.</summary>
    internal Template Parse(SparkMailTemplateFile file)
    {
        var key = file.Source + "|" + file.Path + "|" + file.Content.GetHashCode(StringComparison.Ordinal);
        return parsed.GetOrAdd(key, _ =>
        {
            var template = Template.Parse(file.Content, file.Path);
            if (template.HasErrors)
                throw new SparkMailTemplateException($"Mail template '{file.Path}' ({file.Source}) does not parse: {string.Join("; ", template.Messages)}");
            return template;
        });
    }

    /// <summary>
    /// <paramref name="json"/> (a JSON object, or null) with <c>app_name</c> added when it has none —
    /// always defined, so a template may test it under strict variables.
    /// </summary>
    public static JsonElement WithAppName(string? json, string? applicationName)
    {
        var node = (json is null ? null : System.Text.Json.Nodes.JsonNode.Parse(json)) as System.Text.Json.Nodes.JsonObject ?? [];
        if (!node.ContainsKey("app_name"))
            node["app_name"] = applicationName;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>JSON → Scriban values. String values HTML-escaped when <paramref name="escapeHtml"/>.</summary>
    internal static ScriptObject ToScriptObject(JsonElement? data, bool escapeHtml)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            return new ScriptObject();
        return (ScriptObject)Convert(element, escapeHtml)!;
    }

    private static object? Convert(JsonElement element, bool escapeHtml)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new ScriptObject();
                foreach (var property in element.EnumerateObject())
                    obj[property.Name] = Convert(property.Value, escapeHtml);
                return obj;
            case JsonValueKind.Array:
                var array = new ScriptArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(Convert(item, escapeHtml));
                return array;
            case JsonValueKind.String:
                var value = element.GetString();
                // An ISO date round-trips as a DateTime so date.to_string and the culture apply.
                if (value is not null && value.Length >= 19 && value[4] == '-' && value[10] == 'T'
                    && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                    return at.DateTime; // Scriban's date functions take DateTime; the clock time as written
                return escapeHtml ? WebUtility.HtmlEncode(value) : value;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer)) return integer;
                if (element.TryGetDecimal(out var number)) return number;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>A crude text part for a template without a <c>.txt</c> file: body text, links kept as "text (url)".</summary>
    internal static string HtmlToText(string html)
    {
        var body = HeadRegex().Replace(html, string.Empty);
        body = CommentRegex().Replace(body, string.Empty);
        body = LinkRegex().Replace(body, m =>
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            var text = WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[2].Value, string.Empty)).Trim();
            return text.Length == 0 || text == href ? $" {href} " : $" {text} ({href}) ";
        });
        body = BlockEndRegex().Replace(body, "\n");
        body = WebUtility.HtmlDecode(TagRegex().Replace(body, string.Empty));
        var lines = body.Split('\n').Select(l => WhitespaceRegex().Replace(l, " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n\n", lines);
    }

    [GeneratedRegex(@"<mj-title>(.*?)</mj-title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<head[\s\S]*?</head>|<style[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<a\s[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"</(p|div|td|tr|h[1-6]|li|table)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>Resolves <c>mj-include</c> through the resolver and renders the partial with the same data.</summary>
    private sealed class IncludeLoader(SparkMailRenderer renderer, string directory, ScriptObject data, CultureInfo culture, IDictionary<string, string>? sources) : IFileLoader
    {
        public SparkMailTemplateException? Failure { get; private set; }

        public string? LoadText(string path)
        {
            try
            {
                var relative = path.Replace('\\', '/');
                if (relative.StartsWith("./", StringComparison.Ordinal))
                    relative = relative[2..];
                var combined = directory.Length == 0 ? relative : $"{directory}/{relative}";
                var normalized = Normalize(combined);
                var extension = System.IO.Path.GetExtension(normalized);
                var name = extension.Length > 0 ? normalized[..^extension.Length] : normalized;
                var file = renderer.Resolver.Resolve(name, extension.Length > 0 ? extension : ".mjml", culture)
                    ?? throw new SparkMailTemplateException($"mj-include '{path}': no such template file.");
                // Synchronous by the IFileLoader contract; Scriban renders in memory, nothing awaits I/O.
                var text = renderer.RenderScribanAsync(file, data, culture, CancellationToken.None).GetAwaiter().GetResult();
                if (sources is not null) sources[path] = text;
                return text;
            }
            catch (SparkMailTemplateException ex)
            {
                Failure ??= ex;
                return null;
            }
        }

        private static string Normalize(string path)
        {
            var parts = new List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part is "" or ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            return string.Join('/', parts);
        }
    }
}
