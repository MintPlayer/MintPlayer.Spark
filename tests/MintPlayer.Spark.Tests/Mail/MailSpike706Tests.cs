using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Templates;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #706 spikes (docs/mail_url_header_safety_plan.md). S1: which <c>href</c>/<c>src</c> attributes Mjml.Net emits for a
/// shipped template, and in which form (quotes, entities), so a check on the rendered HTML reads every one. S2: what
/// MimeKit does with a CR/LF in a display name, an address and a header value.
/// </summary>
public class MailSpike706Tests(ITestOutputHelper output)
{
    private static SparkMailRenderer Renderer() => new(new SparkMailTemplateResolver(
        [new EmbeddedMailTemplateStore(new SparkMailTemplateAssembly(typeof(MintPlayer.Spark.Authorization.Identity.SparkAuthMailTemplates).Assembly, "SparkMail/"))],
        NullLogger<SparkMailTemplateResolver>.Instance));

    [Fact]
    public async Task S1_rendered_attributes_of_a_shipped_template()
    {
        var data = JsonSerializer.SerializeToElement(new { link = "https://app.example/reset?email=a%40b.example&code=x\"y", app_name = "Demo", valid_for_hours = 24 });
        var mail = await Renderer().RenderAsync("SparkAuth/PasswordReset", CultureInfo.GetCultureInfo("en"), data, CancellationToken.None);

        foreach (Match m in Regex.Matches(mail.Html, """\s(href|src|background)\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)""", RegexOptions.IgnoreCase))
            output.WriteLine($"{m.Groups[1].Value} = {m.Groups[2].Value}");

        // Before the fix this printed "javascript link reaches href: True" (PasswordReset then put any link in href). The
        // fix moved the check into the renderer (MailRendererSafetyTests); PasswordReset now shows a non-http link as a code.
        mail.Html.Should().Contain("href=\"https://app.example/reset?email=a%40b.example&amp;code=x&quot;y\"", "double-quoted and entity-encoded (S1)");
    }

    [Fact]
    public void S2_mimekit_and_crlf()
    {
        Report("display name", () =>
        {
            var m = New();
            m.To.Add(new MailboxAddress("Eve\r\nBcc: victim@evil.example", "to@example.com"));
            return m;
        });
        Report("address", () =>
        {
            var m = New();
            m.To.Add(new MailboxAddress("Eve", "to@example.com\r\nBcc: victim@evil.example"));
            return m;
        });
        Report("header value", () =>
        {
            var m = New();
            m.Headers.Replace("X-Spark-Campaign", "c1\r\nBcc: victim@evil.example");
            return m;
        });
    }

    private static MimeMessage New()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("App", "app@example.com"));
        m.Subject = "s";
        m.Body = new TextPart("plain") { Text = "b" };
        return m;
    }

    private void Report(string label, Func<MimeMessage> build)
    {
        try
        {
            var text = build().ToString();
            var injected = Regex.IsMatch(text, @"^Bcc:", RegexOptions.Multiline);
            output.WriteLine($"{label}: built; a Bcc header line: {injected}");
            foreach (var line in text.Split("\r\n").TakeWhile(l => l.Length > 0))
                output.WriteLine("  | " + line);
        }
        catch (Exception ex)
        {
            output.WriteLine($"{label}: throws {ex.GetType().Name}: {ex.Message}");
        }
    }
}
