using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.IdentityProvider.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The admin screen's validation. Each rule corresponds to an assumption the protocol endpoints
/// make and the audit found failing silently — a client that looks configured and cannot work,
/// with nothing anywhere saying why. The point of refusing here is that the operator is the only
/// one who can still act on the answer.
/// </summary>
public class OidcApplicationActionsTests
{
    private static OidcApplicationActions Actions() => new(Substitute.For<IEntityMapper>());

    private static OidcApplication Valid() => new()
    {
        ClientId = "webapp",
        DisplayName = "Web App",
        ClientType = "confidential",
        RedirectUris = ["https://webapp.test/cb"],
        AllowedScopes = ["openid"],
        AllowedGrantTypes = ["authorization_code"],
    };

    private static async Task<Exception?> SaveAsync(OidcApplication app)
    {
        try
        {
            await Actions().OnBeforeSaveAsync(new PersistentObject { Name = "OidcApplication", ObjectTypeId = Guid.NewGuid() }, app);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task A_valid_application_is_accepted()
    {
        (await SaveAsync(Valid())).Should().BeNull();
    }

    [Fact]
    public async Task A_relative_redirect_uri_is_rejected()
    {
        var app = Valid();
        app.RedirectUris = ["/callback"];

        (await SaveAsync(app))!.Message.Should().Contain("absolute");
    }

    [Fact]
    public async Task A_redirect_uri_with_a_fragment_is_rejected()
    {
        var app = Valid();
        app.RedirectUris = ["https://webapp.test/cb#section"];

        (await SaveAsync(app))!.Message.Should().Contain("fragment",
            "a browser never sends the fragment, so the registered value could never match");
    }

    [Fact]
    public async Task A_duplicated_redirect_uri_is_rejected()
    {
        var app = Valid();
        app.RedirectUris = ["https://webapp.test/cb", "https://webapp.test/cb"];

        (await SaveAsync(app))!.Message.Should().Contain("more than once");
    }

    [Fact]
    public async Task An_unknown_grant_type_is_rejected()
    {
        var app = Valid();
        app.AllowedGrantTypes = ["authorization_code", "implicit"];

        (await SaveAsync(app))!.Message.Should().Contain("not supported",
            "an unrecognised grant is not inert — the token endpoint tests membership of this "
            + "list, so a typo yields a client refused every grant that reads as configured");
    }

    [Fact]
    public async Task A_client_with_no_grant_types_is_rejected()
    {
        var app = Valid();
        app.AllowedGrantTypes = [];

        (await SaveAsync(app))!.Message.Should().Contain("At least one grant type");
    }

    [Fact]
    public async Task Refresh_token_without_authorization_code_is_rejected()
    {
        var app = Valid();
        app.AllowedGrantTypes = ["refresh_token"];

        (await SaveAsync(app))!.Message.Should().Contain("requires authorization_code",
            "there is no other way for this client to obtain a refresh token, so the combination "
            + "is unreachable rather than merely unusual");
    }

    [Fact]
    public async Task A_public_client_cannot_use_client_credentials()
    {
        var app = Valid();
        app.ClientType = "public";
        app.AllowedGrantTypes = ["client_credentials"];

        (await SaveAsync(app))!.Message.Should().Contain("no secret to authenticate with");
    }

    [Fact]
    public async Task A_secret_entered_in_cleartext_is_stored_hashed()
    {
        var app = Valid();
        app.Secrets.Add(new ClientSecret { Hash = "my-plaintext-secret" });

        (await SaveAsync(app)).Should().BeNull();

        app.Secrets[0].Hash.Should().NotBe("my-plaintext-secret");
        ClientSecretHasher.IsHashed(app.Secrets[0].Hash).Should().BeTrue();
        ClientSecretHasher.Verify("my-plaintext-secret", app.Secrets[0].Hash).Should().BeTrue(
            "the secret the operator typed must still authenticate");
    }

    [Fact]
    public async Task An_already_hashed_secret_is_left_alone()
    {
        var hashed = ClientSecretHasher.Hash("existing-secret");
        var app = Valid();
        app.Secrets.Add(new ClientSecret { Hash = hashed });

        (await SaveAsync(app)).Should().BeNull();

        app.Secrets[0].Hash.Should().Be(hashed,
            "re-hashing on every save would invalidate the secret the client already holds");
    }

    [Fact]
    public async Task An_empty_secret_is_rejected()
    {
        var app = Valid();
        app.Secrets.Add(new ClientSecret { Hash = "" });

        (await SaveAsync(app))!.Message.Should().Contain("cannot be empty");
    }

    [Fact]
    public async Task A_missing_client_id_is_rejected()
    {
        var app = Valid();
        app.ClientId = "";

        (await SaveAsync(app))!.Message.Should().Contain("Client id is required");
    }
}

/// <summary>Validation for the scope screen — the half that decides what a token carries.</summary>
public class OidcScopeActionsTests
{
    private static OidcScopeActions Actions() => new(Substitute.For<IEntityMapper>());

    private static async Task<Exception?> SaveAsync(OidcScope scope)
    {
        try
        {
            await Actions().OnBeforeSaveAsync(new PersistentObject { Name = "OidcScope", ObjectTypeId = Guid.NewGuid() }, scope);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task A_valid_scope_is_accepted()
    {
        (await SaveAsync(new OidcScope { Name = "api.read", Enabled = true })).Should().BeNull();
    }

    [Fact]
    public async Task A_scope_name_with_whitespace_is_rejected()
    {
        var error = await SaveAsync(new OidcScope { Name = "api read" });

        error!.Message.Should().Contain("space-delimited",
            "scopes are space-delimited on the wire, so this would be read as two names, "
            + "neither of which exists");
    }

    [Fact]
    public async Task An_empty_scope_name_is_rejected()
    {
        (await SaveAsync(new OidcScope { Name = "" }))!.Message.Should().Contain("required");
    }

    [Fact]
    public async Task An_empty_audience_is_rejected()
    {
        var scope = new OidcScope { Name = "api.read", Audiences = [""] };

        (await SaveAsync(scope))!.Message.Should().Contain("audience cannot be empty");
    }
}
