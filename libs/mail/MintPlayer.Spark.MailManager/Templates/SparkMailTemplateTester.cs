using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.MailManager.Templates;

/// <summary>
/// For an app's own tests: renders every template in every culture with sample data, so a typo in a
/// variable, a missing include or invalid MJML fails a test instead of a send. Uses the app's real
/// resolver (its folder over embedded defaults).
/// </summary>
public static class SparkMailTemplateTester
{
    /// <summary>
    /// Renders each template (partials excluded) × each of <paramref name="cultures"/> with
    /// <paramref name="sampleData"/>(template name). Throws one <see cref="AggregateException"/> listing
    /// every failure, including MJML warnings. Returns the rendered mails otherwise.
    /// </summary>
    public static async Task<IReadOnlyList<SparkRenderedMail>> RenderAllAsync(IServiceProvider services, Func<string, object?> sampleData, params string[] cultures)
    {
        var resolver = services.GetRequiredService<ISparkMailTemplateResolver>();
        var renderer = services.GetRequiredService<SparkMailRenderer>();
        var appName = services.GetService<IOptions<SparkMailOptions>>()?.Value.ApplicationName;
        if (cultures.Length == 0)
            cultures = [string.Empty];

        var failures = new List<Exception>();
        var rendered = new List<SparkRenderedMail>();
        foreach (var template in resolver.TemplateNames)
        {
            foreach (var culture in cultures)
            {
                try
                {
                    var data = SparkMailRenderer.WithAppName(SparkMailer.SerializeData(sampleData(template)), appName);
                    var mail = await renderer.RenderAsync(template, CultureInfo.GetCultureInfo(culture), data);
                    if (mail.Warnings.Count > 0)
                        throw new SparkMailTemplateException($"'{template}' ({culture}): MJML {string.Join("; ", mail.Warnings)}");
                    rendered.Add(mail);
                }
                catch (Exception ex)
                {
                    failures.Add(new SparkMailTemplateException($"'{template}' ({(culture.Length == 0 ? "neutral" : culture)}): {ex.Message}", ex));
                }
            }
        }
        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} mail template render(s) failed.", failures);
        return rendered;
    }
}
