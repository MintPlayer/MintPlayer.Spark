using System.Globalization;
using System.Net;
using System.Text.Json;
using Mjml.Net;
using Newtonsoft.Json.Linq;
using Scriban;
using Scriban.Runtime;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 spike S-M4: Scriban (7.5) strict variables, what a Newtonsoft <c>JObject</c> / an STJ
/// <c>JsonElement</c> look like to a template, escaping (Scriban escapes nothing), and culture-aware
/// number and date formatting — the facts the MailManager renderer is built on.
/// </summary>
public class MailSpikeM4Tests(ITestOutputHelper output)
{
    private async Task<(string? Output, Exception? Error)> RenderAsync(string text, Action<ScriptObject> globals, bool strict, CultureInfo? culture = null, bool relaxedMembers = true)
    {
        var template = Template.Parse(text);
        template.HasErrors.Should().BeFalse(string.Join("; ", template.Messages));
        var context = new TemplateContext { StrictVariables = strict, EnableRelaxedMemberAccess = relaxedMembers };
        if (culture is not null)
            context.PushCulture(culture);
        var root = new ScriptObject();
        globals(root);
        context.PushGlobal(root);
        try { return (await template.RenderAsync(context), null); }
        catch (Exception ex) { return (null, ex); }
    }

    /// <summary>The conversion the spike proposes: JSON → ScriptObject, strings HTML-escaped.</summary>
    private static object? ToScript(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Aggregate(new ScriptObject(), (o, p) => { o[p.Name] = ToScript(p.Value); return o; }),
        JsonValueKind.Array => new ScriptArray(element.EnumerateArray().Select(ToScript)),
        JsonValueKind.String => WebUtility.HtmlEncode(element.GetString()),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    [Fact]
    public async Task S_M4_strict_variables_refuse_an_unknown_name()
    {
        var strict = await RenderAsync("Hi {{ name }} {{ missing }}", g => g["name"] = "Ann", strict: true);
        var relaxed = await RenderAsync("Hi {{ name }} {{ missing }}.", g => g["name"] = "Ann", strict: false);
        var strictMember = await RenderAsync("{{ user.missing }}", g => g["user"] = new ScriptObject { ["name"] = "Ann" }, strict: true);

        output.WriteLine($"strict: {strict.Error?.GetType().Name}: {strict.Error?.Message}");
        output.WriteLine($"relaxed: '{relaxed.Output}'");
        var strictMemberUnrelaxed = await RenderAsync("{{ user.missing }}", g => g["user"] = new ScriptObject { ["name"] = "Ann" }, strict: true, relaxedMembers: false);
        var nullParentUnrelaxed = await RenderAsync("{{ user.missing.deeper }}", g => g["user"] = new ScriptObject { ["name"] = "Ann" }, strict: true, relaxedMembers: false);
        output.WriteLine($"strict member: {strictMember.Error?.GetType().Name}: {strictMember.Error?.Message} / '{strictMember.Output}'");
        output.WriteLine($"strict member, relaxed member access off: {strictMemberUnrelaxed.Error?.GetType().Name}: {strictMemberUnrelaxed.Error?.Message} / '{strictMemberUnrelaxed.Output}'");
        output.WriteLine($"member of a missing member, relaxed off: {nullParentUnrelaxed.Error?.GetType().Name}: {nullParentUnrelaxed.Error?.Message}");
        strict.Error.Should().BeOfType<Scriban.Syntax.ScriptRuntimeException>();
        relaxed.Output.Should().Be("Hi Ann .");
        strictMember.Error.Should().BeNull("StrictVariables covers top-level names only: a missing member renders empty");
        strictMember.Output.Should().Be(string.Empty);
    }

    [Fact]
    public async Task S_M4_what_JSON_objects_look_like_to_a_template()
    {
        const string json = """{"name":"Ann","items":[{"n":1},{"n":2}]}""";
        var jobject = await RenderAsync("{{ data.name }}|{{ data.items.size }}", g => g["data"] = JObject.Parse(json), strict: false);
        var jobjectStrict = await RenderAsync("{{ data.name }}", g => g["data"] = JObject.Parse(json), strict: true);
        var element = await RenderAsync("{{ data.name }}|{{ data.items.size }}", g => g["data"] = JsonDocument.Parse(json).RootElement, strict: false);
        var converted = await RenderAsync("{{ data.name }}|{{ data.items.size }}|{{ for i in data.items }}{{ i.n }}{{ end }}", g => g["data"] = ToScript(JsonDocument.Parse(json).RootElement), strict: true);

        output.WriteLine($"JObject: '{jobject.Output}' {jobject.Error?.Message}");
        output.WriteLine($"JObject strict: '{jobjectStrict.Output}' {jobjectStrict.Error?.GetType().Name}: {jobjectStrict.Error?.Message}");
        output.WriteLine($"JsonElement: '{element.Output}' {element.Error?.GetType().Name}: {element.Error?.Message}");
        output.WriteLine($"converted: '{converted.Output}' {converted.Error?.Message}");
        converted.Output.Should().Be("Ann|2|12");
    }

    [Fact]
    public async Task S_M4_Scriban_escapes_nothing_and_escaped_values_survive_MJML()
    {
        const string hostile = "<b>Ann & \"Co\"</b><script>x()</script>";
        var raw = await RenderAsync("{{ name }}", g => g["name"] = hostile, strict: true);
        var escaped = await RenderAsync("{{ name }}", g => g["name"] = WebUtility.HtmlEncode(hostile), strict: true);
        var mjml = $"<mjml><mj-body><mj-section><mj-column><mj-text>{escaped.Output}</mj-text><mj-button href=\"https://app.example/r?a=1&amp;b=2\">Go</mj-button></mj-column></mj-section></mj-body></mjml>";
        var rendered = new MjmlRenderer().Render(mjml, new MjmlOptions { Beautify = false });

        output.WriteLine($"raw: {raw.Output}");
        output.WriteLine($"escaped: {escaped.Output}");
        output.WriteLine($"mjml errors: {string.Join("; ", rendered.Errors.Select(e => e.Error))}");
        raw.Output.Should().Be(hostile, "Scriban writes values verbatim");
        rendered.Html.Should().Contain("&lt;b&gt;Ann &amp; &quot;Co&quot;&lt;/b&gt;&lt;script&gt;");
        rendered.Html.Should().NotContain("<script>x()");
        rendered.Html.Should().Contain("href=\"https://app.example/r?a=1&amp;b=2\"");
    }

    [Fact]
    public async Task S_M4_numbers_and_dates_follow_the_pushed_culture()
    {
        var when = new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc);
        const string text = "{{ amount }}|{{ amount | math.format 'N2' }}|{{ at | date.to_string '%d %B %Y' }}|{{ at | date.to_string '%x' }}";
        var nl = await RenderAsync(text, g => { g["amount"] = 1234.5; g["at"] = when; }, strict: true, CultureInfo.GetCultureInfo("nl-BE"));
        var en = await RenderAsync(text, g => { g["amount"] = 1234.5; g["at"] = when; }, strict: true, CultureInfo.GetCultureInfo("en-US"));
        var none = await RenderAsync(text, g => { g["amount"] = 1234.5; g["at"] = when; }, strict: true);

        output.WriteLine($"nl-BE: {nl.Output} {nl.Error?.Message}");
        output.WriteLine($"en-US: {en.Output} {en.Error?.Message}");
        output.WriteLine($"no culture pushed (thread {CultureInfo.CurrentCulture.Name}): {none.Output}");
        nl.Output.Should().Contain("1.234,50");
        nl.Output.Should().Contain("januari");
        en.Output.Should().Contain("1,234.50");
        en.Output.Should().Contain("January");
    }
}
