using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Organizations.Endpoints;
using MintPlayer.Spark.Organizations.Identity;
using MintPlayer.Spark.Organizations.Interceptors;
using MintPlayer.Spark.Organizations.Services;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// Organizations (<c>docs/seo_analyzer_PRD.md</c> X11):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddMailManager();
///     spark.AddOrganizations();
/// });
/// // App_Data/security.json: "bindings": { "organizations:users": [ "Users" ] }
/// public class Site : IOrganizationOwned { public string? OrganizationId { get; set; } … }
/// </code>
/// </summary>
public static class SparkOrganizationsExtensions
{
    /// <summary>
    /// Registers the organization model and its rules: the row policy for <see cref="IOrganizationOwned"/>
    /// types and <see cref="Models.Organization"/>, <see cref="ISparkOrganizations"/>, the personal
    /// organization on sign-in, the account deletion and export hooks, the invitation mails,
    /// <c>POST /spark/organizations/accept</c> and Approvals with the join-request kind
    /// (<see cref="OrganizationJoinApproval"/>). Needs <c>AddAuthentication&lt;TUser&gt;()</c>, in either order.
    /// </summary>
    public static ISparkBuilder AddOrganizations(this ISparkBuilder builder, Action<SparkOrganizationsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<OrganizationsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkOrganizationsOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<OrganizationMembershipReader>();
        services.TryAddScoped<CurrentOrganization>();
        services.TryAddScoped<SparkOrganizations>();
        services.TryAddScoped<ISparkOrganizations>(sp => sp.GetRequiredService<SparkOrganizations>());
        services.TryAddScoped<OrganizationMail>();

        // D26: a join request is a decision request (kind organizations.join), decided in-app by the organization's
        // Owners and Admins. Approvals keeps the request, tells the deciders and the requester, and closes it with
        // the requester's account.
        builder.AddApprovals();
        services.TryAddScoped<IApprovalDeciders<OrganizationJoinApproval>, OrganizationJoinDeciders>();
        services.TryAddScoped<IApprovalHandler<OrganizationJoinApproval>, OrganizationJoinHandler>();
        // The embedded SparkOrganizations/* templates (MailManager reads them; an app's own file of the same name wins).
        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(OrganizationMailTemplates).Assembly, "SparkMail/");

        builder.AddSparkRowPolicy<OrganizationRowPolicy>();
        builder.AddInterceptor<OrganizationInterceptors>();
        builder.AddInterceptor<OrganizationMembershipInterceptors>();
        builder.AddInterceptor<OrganizationOwnedDefaults>();
        // After the defaults: a new row's organization is known when publishing it is judged (X8).
        builder.AddInterceptor<OrganizationPublishGate>();

        // Open generics: they close over whichever user type AddAuthentication<TUser>() registered.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkSignedInHandler<>), typeof(PersonalOrganizationOnSignIn<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(OrganizationAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(OrganizationPersonalData<>)));

        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkOrganizationsEndpoints());

        // Invitations carry @expires (X1); expiration is enabled by UseSpark() for any IExpiring model type.
        return builder;
    }

    /// <summary>
    /// Declares a custom organization role (<c>docs/commerce_PRD.md</c> X8) that Admins can give members and that
    /// rights name with <c>[RequiresOrganizationRole(action, name)]</c>. For libraries (the commerce package's
    /// <c>scanner</c>, <c>finance</c>) and hosts alike; declaring a name twice keeps the last description. Adds the
    /// organizations package when it is not added yet.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> fails <see cref="OrganizationCustomRoles.IsValidName"/>.</exception>
    public static ISparkBuilder AddOrganizationRole(this ISparkBuilder builder, string name, string description)
    {
        if (!OrganizationCustomRoles.IsValidName(name))
            throw new ArgumentException(
                $"'{name}' is not a valid custom organization role: a letter, then letters, digits, '-' or '_' (at most 64), and not Viewer, Member, Admin or Owner.",
                nameof(name));

        builder.AddOrganizations();
        builder.Services.Configure<SparkOrganizationsOptions>(options => options.CustomRoles[name] = description ?? string.Empty);
        return builder;
    }

    /// <summary>Marks the package as added, so a second call only adds options.</summary>
    private sealed class OrganizationsMarker;
}
