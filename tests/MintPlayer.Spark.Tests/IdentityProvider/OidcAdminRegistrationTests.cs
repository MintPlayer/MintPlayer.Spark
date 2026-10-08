using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The admin screens ship with the package. The identity provider is a Spark library layer (alias
/// <c>identity-provider</c>) that embeds its own <c>App_Data/Model/*.json</c>, so a consuming app gets
/// the OidcApplication and OidcResource persistent objects without declaring anything on its context.
/// <para>
/// This replaces the M12.7 test that a consumer's <c>IOidcApplicationContext</c> property made the
/// synchronizer generate the models: that interface is gone, and the layer is now the registration.
/// </para>
/// </summary>
public class OidcAdminRegistrationTests
{
    private static SparkLibrary Library()
    {
        var libraries = SparkLayerCatalog.Discover([typeof(OidcApplication).Assembly]);
        return libraries.Should().ContainSingle().Which;
    }

    private static JsonElement Model(string entityName)
    {
        var layer = Library().Layers.SingleOrDefault(l => l.Kind == "model" && l.Path == $"Model/{entityName}.json");
        layer.Should().NotBeNull($"the package should ship {entityName}.json as a model layer");

        return JsonDocument.Parse(layer!.Json).RootElement.GetProperty("persistentObject");
    }

    private static List<string?> Attributes(JsonElement model)
        => [.. model.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString())];

    [Fact]
    public void The_package_is_a_library_layer_under_its_alias()
    {
        Library().Alias.Should().Be("identity-provider");
    }

    [Fact]
    public void A_library_entity_becomes_a_persistent_object()
    {
        var model = Model("OidcApplication");

        model.GetProperty("name").GetString().Should().Be("OidcApplication");
        model.GetProperty("clrType").GetString().Should().Contain("MintPlayer.Spark.IdentityProvider",
            "the entity lives in the package, not the consumer's assembly — which is the point");
    }

    [Fact]
    public void The_shipped_model_carries_the_fields_an_operator_must_set()
    {
        var attributes = Attributes(Model("OidcApplication"));

        // Every one of these is load-bearing: the audit found each failing silently when wrong.
        attributes.Should().Contain("ClientId");
        attributes.Should().Contain("RedirectUris");
        attributes.Should().Contain("Scopes");
        attributes.Should().Contain("AllowedGrantTypes");
        attributes.Should().Contain("Enabled");
        attributes.Should().Contain("MayIntrospectAnyAudience");
        attributes.Should().Contain("Mode");
    }

    [Fact]
    public void Resources_are_shipped_alongside_applications()
    {
        var attributes = Attributes(Model("OidcResource"));

        attributes.Should().Contain("Name",
            "an API resource's name is the audience its tokens carry (D11)");
        attributes.Should().Contain("Kind");
        attributes.Should().Contain("Enabled");
        attributes.Should().Contain("Scopes");
    }
}
