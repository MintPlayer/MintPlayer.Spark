using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Proves M12.7's premise: a consumer exposes the package's entities on its own context and gets
/// admin screens, with no framework change and nothing hand-authored.
/// <para>
/// This is the claim the milestone rests on, and it was worth testing rather than asserting —
/// an earlier draft of the plan concluded the opposite from reading <c>ModelLoader</c> alone,
/// and proposed a registry mechanism for a problem that does not exist.
/// </para>
/// </summary>
public class OidcAdminRegistrationTests : OidcTestHost
{
    /// <summary>Exactly what a consuming app writes — the interface is the whole registration.</summary>
    private sealed class AdminContext : SparkContext, IOidcApplicationContext
    {
        public IRavenQueryable<OidcApplication> OidcApplications { get; set; } = default!;
        public IRavenQueryable<OidcScope> OidcScopes { get; set; } = default!;
    }

    private JsonElement Synchronize(string entityName)
    {
        Factory.GetService<IModelSynchronizer>().SynchronizeModels(new AdminContext());

        var contentRoot = Factory.GetService<IHostEnvironment>().ContentRootPath;
        var path = Path.Combine(contentRoot, "App_Data", "Model", entityName + ".json");

        File.Exists(path).Should().BeTrue(
            $"the synchronizer should have generated {entityName}.json from the context property");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("persistentObject");
    }

    [Fact]
    public void A_library_entity_on_the_context_becomes_a_persistent_object()
    {
        var model = Synchronize("OidcApplication");

        model.GetProperty("name").GetString().Should().Be("OidcApplication");
        model.GetProperty("clrType").GetString().Should().Contain("MintPlayer.Spark.IdentityProvider",
            "the entity lives in the package, not the consumer's assembly — which is the point");
    }

    [Fact]
    public void The_generated_model_carries_the_fields_an_operator_must_set()
    {
        var model = Synchronize("OidcApplication");

        var attributes = model.GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        // Every one of these is load-bearing: the audit found each failing silently when wrong.
        attributes.Should().Contain("ClientId");
        attributes.Should().Contain("RedirectUris");
        attributes.Should().Contain("AllowedScopes");
        attributes.Should().Contain("AllowedGrantTypes");
        attributes.Should().Contain("Enabled");
        attributes.Should().Contain("MayIntrospectAnyAudience");
    }

    [Fact]
    public void Scopes_are_registered_alongside_applications()
    {
        var model = Synchronize("OidcScope");

        var attributes = model.GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToList();

        attributes.Should().Contain("Name");
        attributes.Should().Contain("Enabled");
        attributes.Should().Contain("Audiences",
            "audiences are what make a token addressable to a resource server (D11)");
    }
}
