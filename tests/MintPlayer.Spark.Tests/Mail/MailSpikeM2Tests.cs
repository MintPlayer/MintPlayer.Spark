using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Templates;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 spike S-M2: Mjml.Net against npm <c>mjml</c> on the three real templates MailManager ships
/// (Authorization's <c>SparkAuth/*</c>, each with an <c>mj-include</c>): errors, visible text, Outlook
/// (<c>&lt;!--[if mso</c>) conditionals, tables, size. The npm half runs only when
/// <c>SPARK_SPIKE_SM2_MJML</c> points at the mjml CLI (e.g. <c>node_modules/.bin/mjml.cmd</c>).
/// </summary>
public class MailSpikeM2Tests(ITestOutputHelper output)
{
    private static readonly (string Template, object Data)[] Cases =
    [
        ("SparkAuth/ConfirmEmail", new { link = "https://app.example/confirm-email?userId=u1&code=abc", app_name = "Demo", changed_email = (string?)null, valid_for_hours = 24 }),
        ("SparkAuth/PasswordReset", new { link = "https://app.example/reset-password?email=a%40b.example&code=xyz", app_name = "Demo", valid_for_hours = 24 }),
        ("SparkAuth/LinkConfirmation", new { link = "https://app.example/link?t=abc", app_name = "Demo", provider = "GitHub", provider_identity = "octo", valid_for_minutes = 60, valid_for_hours = 1.0 }),
    ];

    private static SparkMailRenderer Renderer() => new(new SparkMailTemplateResolver(
        [new EmbeddedMailTemplateStore(new SparkMailTemplateAssembly(typeof(MintPlayer.Spark.Authorization.Identity.SparkUser).Assembly, "SparkMail/"))],
        NullLogger<SparkMailTemplateResolver>.Instance));

    [Fact]
    public async Task S_M2_MjmlNet_against_npm_mjml_on_the_shipped_templates()
    {
        var cli = Environment.GetEnvironmentVariable("SPARK_SPIKE_SM2_MJML");
        var renderer = Renderer();
        foreach (var (template, data) in Cases)
        {
            var sources = new Dictionary<string, string>();
            var json = JsonSerializer.SerializeToElement(data);
            var net = await renderer.RenderAsync(template, CultureInfo.GetCultureInfo("en"), json, sources, CancellationToken.None);

            output.WriteLine($"== {template}: subject '{net.Subject}', Mjml.Net warnings: {net.Warnings.Count} {string.Join("; ", net.Warnings)}");
            output.WriteLine($"   Mjml.Net: {Describe(net.Html)}");
            net.Warnings.Should().BeEmpty();
            sources.Keys.Should().Contain("_Footer.mjml", "the include went through the loader");

            if (string.IsNullOrEmpty(cli))
                continue;

            var dir = Directory.CreateTempSubdirectory("sm2-").FullName;
            foreach (var (path, text) in sources)
                await File.WriteAllTextAsync(Path.Combine(dir, path.Length == 0 ? "main.mjml" : path), text);
            var npmHtml = RunMjml(cli, dir);
            output.WriteLine($"   npm mjml: {Describe(npmHtml)}");
            var netText = SparkMailRenderer.HtmlToText(net.Html);
            var npmText = SparkMailRenderer.HtmlToText(npmHtml);
            output.WriteLine($"   visible text equal: {netText == npmText}");
            if (netText != npmText)
                output.WriteLine($"   net: {netText}\n   npm: {npmText}");
        }
    }

    private static string Describe(string html) =>
        $"{html.Length} chars, {Regex.Matches(html, "<!--\\[if mso").Count} mso conditionals, {Regex.Matches(html, "<table").Count} tables, " +
        $"{Regex.Matches(html, "<a ").Count} links, {Regex.Matches(html, "v:roundrect").Count} VML buttons";

    private static string RunMjml(string cli, string dir)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c \"\"{cli}\" main.mjml -s --config.beautify false --config.allowIncludes true\"")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, stderr);
        return stdout;
    }
}
