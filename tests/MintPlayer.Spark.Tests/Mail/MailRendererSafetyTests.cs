using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Templates;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #706 (docs/mail_url_header_safety_PRD.md D1-D3, D7): the real <see cref="SparkMailRenderer"/> refuses a link that is not
/// an absolute http(s) URL (or a <c>mailto:</c> in an <c>href</c>), and keeps a quote breakout and an MJML tag injection
/// inside their text.
/// </summary>
public sealed class MailRendererSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-mail-706-" + Guid.NewGuid().ToString("N"));

    public MailRendererSafetyTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private SparkMailRenderer Renderer() => new(new SparkMailTemplateResolver(
        [new FileSystemMailTemplateStore(root), new EmbeddedMailTemplateStore(new SparkMailTemplateAssembly(typeof(SparkAuthMailTemplates).Assembly, "SparkMail/"))],
        NullLogger<SparkMailTemplateResolver>.Instance));

    private void Template(string name, string body)
        => File.WriteAllText(Path.Combine(root, name + ".mjml"),
            $"<mjml><mj-head><mj-title>Subject</mj-title></mj-head><mj-body><mj-section><mj-column>{body}</mj-column></mj-section></mj-body></mjml>");

    private Task<SparkRenderedMail> Render(string template, object data)
        => Renderer().RenderAsync(template, CultureInfo.GetCultureInfo("en"), JsonSerializer.SerializeToElement(data), CancellationToken.None);

    private Task<SparkRenderedMail> PasswordReset(string link)
        => Render("SparkAuth/PasswordReset", new { link, app_name = "Demo", valid_for_hours = 24 });

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/reset-password?code=abc")]
    [InlineData("reset-password?code=abc")]
    [InlineData("//evil.example/reset")]
    [InlineData("https://app.example@evil.example/reset")]
    [InlineData("https://app.example/\treset")]
    public async Task A_link_that_is_not_absolute_http_is_refused(string link)
    {
        // An app template, as an app override of any shipped one would be: the check does not depend on the caller.
        Template("Button", """<mj-button href="{{ link }}">Open</mj-button>""");

        var render = () => Render("Button", new { link });

        var thrown = await render.Should().ThrowAsync<SparkMailTemplateException>();
        thrown.Which.Message.Should().Contain("href");
        thrown.Which.Message.Should().Contain("Button.mjml");
        thrown.Which.Message.Should().NotContain(link, "the value may be personal data");
    }

    [Fact]
    public async Task An_empty_link_is_refused()
    {
        Template("Empty", """<mj-text><a href="{{ link }}">Open</a></mj-text>""");
        Template("EmptyButton", """<mj-button href="{{ link }}">Open</mj-button>""");

        var render = () => Render("Empty", new { link = "" });
        await render.Should().ThrowAsync<SparkMailTemplateException>();

        // Mjml.Net leaves an empty href off mj-button's <a> altogether, so there is no link to refuse.
        (await Render("EmptyButton", new { link = "" })).Html.Should().NotContain("href=\"\"");
    }

    [Theory]
    [InlineData("https://app.example/reset-password?email=a%40b.example&code=xyz", "https://app.example/reset-password?email=a%40b.example&amp;code=xyz")]
    [InlineData("http://localhost:5000/reset", "http://localhost:5000/reset")]
    public async Task An_absolute_http_link_is_rendered(string link, string expected)
    {
        var mail = await PasswordReset(link);

        mail.Html.Should().Contain($"href=\"{expected}\"");
    }

    [Fact]
    public async Task A_mailto_href_is_rendered_and_a_mailto_src_is_not()
    {
        Template("Contact", """<mj-text><a href='mailto:{{ to }}'>Write to us</a></mj-text>""");
        Template("Picture", """<mj-image src="{{ src }}" />""");

        var mail = await Render("Contact", new { to = "support@app.example" });
        mail.Html.Should().Contain("mailto:support@app.example", "Mjml.Net rewrites the single quotes; the address is only in the href");

        var picture = () => Render("Picture", new { src = "mailto:support@app.example" });
        await picture.Should().ThrowAsync<SparkMailTemplateException>();
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://cdn.app.example/logo.png", true)]
    public async Task An_image_src_follows_the_same_rule(string src, bool rendered)
    {
        Template("Picture", """<mj-image src="{{ src }}" />""");

        var render = () => Render("Picture", new { src });

        if (rendered)
            (await render()).Html.Should().Contain($"src=\"{src}\"");
        else
            await render.Should().ThrowAsync<SparkMailTemplateException>();
    }

    [Theory]
    [InlineData("en", "SparkAuth/PasswordReset")]
    [InlineData("nl", "SparkAuth/PasswordReset")]
    public async Task A_reset_code_is_shown_as_text_not_as_a_link(string culture, string template)
    {
        // PRD I3/D4: SendPasswordResetCodeAsync sends a code, which used to land in href="CODE".
        var mail = await Renderer().RenderAsync(template, CultureInfo.GetCultureInfo(culture),
            JsonSerializer.SerializeToElement(new { link = "K7Q2-9XPA", app_name = "Demo", valid_for_hours = 24 }), CancellationToken.None);

        mail.Html.Should().Contain("K7Q2-9XPA");
        mail.Html.Should().NotContain("href=\"K7Q2-9XPA\"");
        mail.Html.Should().NotContain("href=\"\"");
    }

    [Fact]
    public async Task A_quote_cannot_break_out_of_an_attribute()
    {
        Template("Titled", """<mj-text><a href="https://app.example/" title="{{ title }}">Open</a></mj-text>""");

        var mail = await Render("Titled", new { title = "x\" onclick=\"alert(1)" });

        mail.Html.Should().NotContain("onclick=\"alert(1)\"");
        mail.Html.Should().Contain("x&quot; onclick=&quot;alert(1)");
    }

    [Fact]
    public async Task A_value_cannot_add_MJML_or_HTML_tags()
    {
        Template("Text", """<mj-text>{{ body }}</mj-text>""");

        var mail = await Render("Text", new { body = "</mj-text><mj-raw><script>alert(1)</script></mj-raw><mj-text>" });

        mail.Html.Should().NotContain("<script>");
        mail.Html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }
}
