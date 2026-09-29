using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.MailManager;

/// <summary>
/// Declares an assembly's embedded mail templates, so a library can ship default templates that an
/// application overrides by placing a file with the same name in its own template folder.
/// </summary>
/// <param name="Assembly">The assembly holding the resources.</param>
/// <param name="ResourcePrefix">
/// The common prefix of the resource names, e.g. <c>SparkMail/</c>. A resource
/// <c>{prefix}SparkAuth/PasswordReset.nl.mjml</c> is the <c>nl</c> file of template
/// <c>SparkAuth/PasswordReset</c>. Embed with a <c>LogicalName</c> and <c>WithCulture="false"</c> —
/// without the latter MSBuild moves every <c>name.{culture}.mjml</c> into a satellite assembly
/// (#460, spike S-M1):
/// <code>&lt;EmbeddedResource Include="Mail\**\*.mjml" LogicalName="SparkMail/%(RecursiveDir)%(Filename)%(Extension)" WithCulture="false" /&gt;</code>
/// </param>
public sealed record SparkMailTemplateAssembly(Assembly Assembly, string ResourcePrefix);

/// <summary>
/// The recipient's stored culture preference, asked when a mail is queued without an explicit
/// culture. Multi-registered; the first non-null answer wins. The Authorization package registers one
/// that reads <c>SparkUser.PreferredCulture</c>.
/// </summary>
public interface ISparkMailRecipientCulture
{
    /// <summary>The culture name (<c>nl-BE</c>) <paramref name="email"/> prefers, or null when unknown.</summary>
    ValueTask<string?> GetCultureAsync(string email, CancellationToken cancellationToken = default);
}

/// <summary>Registration helpers usable without the MailManager package.</summary>
public static class SparkMailTemplateServiceCollectionExtensions
{
    /// <summary>Declares <paramref name="assembly"/>'s embedded templates under <paramref name="resourcePrefix"/>.</summary>
    public static IServiceCollection AddSparkMailTemplates(this IServiceCollection services, Assembly assembly, string resourcePrefix)
    {
        if (!services.Any(d => d.ServiceType == typeof(SparkMailTemplateAssembly)
                && d.ImplementationInstance is SparkMailTemplateAssembly existing
                && existing.Assembly == assembly && existing.ResourcePrefix == resourcePrefix))
            services.AddSingleton(new SparkMailTemplateAssembly(assembly, resourcePrefix));
        return services;
    }
}
