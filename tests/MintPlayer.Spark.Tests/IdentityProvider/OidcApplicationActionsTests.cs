using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.IdentityProvider.Interceptors;
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
    // No session: the before-write uniqueness query is skipped, as for any interceptor called by hand.
    private static OidcApplicationInterceptors Interceptors() => new(corsOrigins: new OidcCorsOrigins(), accessControl: Substitute.For<MintPlayer.Spark.Abstractions.Authorization.IAccessControl>());

    /// <summary>A save context with no session: the rules are judged, the uniqueness query is skipped.</summary>
    internal static SaveContext Context(Type entityType, object entity,
        PersistentObjectOperation operation = PersistentObjectOperation.New) => new()
    {
        EntityType = entityType,
        Operation = operation,
        PersistentObject = new PersistentObject { Name = entityType.Name, ObjectTypeId = Guid.NewGuid() },
        Entity = entity,
        Session = new object(),
    };

    private static OidcApplication Valid() => new()
    {
        ClientId = "webapp",
        DisplayName = "Web App",
        ClientType = "confidential",
        RedirectUris = ["https://webapp.test/cb"],
        Scopes = [new OidcApplicationScope { Name = "openid", Required = true }],
        AllowedGrantTypes = ["authorization_code"],
    };

    private static async Task<Exception?> SaveAsync(OidcApplication app,
        PersistentObjectOperation operation = PersistentObjectOperation.New)
    {
        try
        {
            await Interceptors().OnBeforeSaveAsync(app, Context(typeof(OidcApplication), app, operation));
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

    /// <summary>
    /// Anything that cannot be reduced to an origin is refused, because a browser's <c>Origin</c>
    /// header is only ever scheme + host + optional port — so a value carrying more than that is a
    /// rule that can never match, and nothing would say so.
    /// </summary>
    [Theory]
    [InlineData("https://app.example.com/callback")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("app.example.com")]
    [InlineData("ftp://app.example.com")]
    public async Task A_cors_origin_that_can_never_match_is_rejected(string origin)
    {
        var app = Valid();
        app.AllowedCorsOrigins = [origin];

        (await SaveAsync(app))!.Message.Should().Contain("browser origin");
    }

    [Fact]
    public async Task A_bare_cors_origin_with_a_port_is_accepted()
    {
        var app = Valid();
        app.AllowedCorsOrigins = ["https://app.example.com", "http://localhost:4200"];

        (await SaveAsync(app)).Should().BeNull();
    }

    /// <summary>
    /// A trailing slash is accepted and normalised away rather than refused.
    /// </summary>
    /// <remarks>
    /// It is the single most likely thing an operator types, it is unambiguous, and there is exactly
    /// one thing it can have meant. Refusing it would be correct and useless; normalising removes
    /// the failure instead of reporting it. Everything that is genuinely ambiguous — a path, a
    /// query, credentials — is still refused.
    /// </remarks>
    [Fact]
    public async Task A_cors_origin_with_a_trailing_slash_is_normalised_not_refused()
    {
        var app = Valid();
        app.AllowedCorsOrigins = ["https://app.example.com/"];

        (await SaveAsync(app)).Should().BeNull();
        OidcCorsOrigins.Normalize("https://app.example.com/").Should().Be("https://app.example.com");
    }

    [Fact]
    public async Task A_duplicate_cors_origin_is_rejected()
    {
        var app = Valid();
        app.AllowedCorsOrigins = ["https://app.example.com", "https://app.example.com"];

        (await SaveAsync(app))!.Message.Should().Contain("more than once");
    }

    [Fact]
    public async Task A_relative_redirect_uri_is_rejected()
    {
        var app = Valid();
        app.RedirectUris = ["/callback"];

        (await SaveAsync(app))!.Message.Should().Contain("absolute");
    }

    /// <summary>
    /// The same rejection, pinned platform-independently.
    /// <para>
    /// <c>A_relative_redirect_uri_is_rejected</c> above passed on Windows and failed on Linux,
    /// because <c>Uri.TryCreate(..., UriKind.Absolute, ...)</c> accepts a bare path on Unix and
    /// silently gives it the <c>file</c> scheme. Windows developers therefore saw a green test for
    /// validation that was failing open on the deployment platform. These two cases fail the same
    /// way everywhere.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/callback")]                 // relative — parses as file:/// on Unix only
    [InlineData("callback")]
    [InlineData("//evil.example.com/cb")]     // protocol-relative
    [InlineData("file:///etc/passwd")]        // scheme declared, still not a redirect target
    public async Task A_redirect_uri_without_a_usable_scheme_is_rejected(string value)
    {
        var app = Valid();
        app.RedirectUris = [value];

        var error = await SaveAsync(app);

        error.Should().NotBeNull(
            $"'{value}' can never be a redirect target, on any platform — and Uri.TryCreate alone "
            + "does not say so, since on Unix a bare path parses as a file URI");
    }

    [Fact]
    public async Task A_custom_scheme_is_still_accepted_for_native_clients()
    {
        var app = Valid();
        app.RedirectUris = ["com.example.app:/oauth2redirect"];

        (await SaveAsync(app)).Should().BeNull(
            "native and mobile clients legitimately register a custom scheme; tightening the "
            + "absoluteness check must not shut them out");
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
    public async Task A_missing_client_id_is_generated_on_create()
    {
        var app = Valid();
        app.ClientId = "";

        (await SaveAsync(app)).Should().BeNull();
        app.ClientId.Should().NotBeNullOrWhiteSpace("a new application gets a server-generated client id (D5)");
    }

    [Fact]
    public async Task A_missing_client_id_is_rejected()
    {
        var app = Valid();
        app.ClientId = "";

        (await SaveAsync(app, PersistentObjectOperation.Save))!.Message.Should().Contain("Client id is required");
    }

    [Fact]
    public async Task An_unknown_mode_is_rejected()
    {
        var app = Valid();
        app.Mode = "Production";

        (await SaveAsync(app))!.Message.Should().Contain("Mode must be");
    }
}

/// <summary>Validation for the resource screen — the half that decides what a token carries.</summary>
public class OidcResourceActionsTests
{
    private static OidcResourceInterceptors Interceptors() => new();

    private static async Task<Exception?> SaveAsync(OidcResource resource)
    {
        try
        {
            await Interceptors().OnBeforeSaveAsync(resource, OidcApplicationActionsTests.Context(typeof(OidcResource), resource));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static OidcResource Api(string name, params string[] scopes) => new()
    {
        Kind = OidcResourceKinds.Api,
        Name = name,
        Scopes = [.. scopes.Select(s => new OidcApiScope { Name = s })],
    };

    [Fact]
    public async Task A_valid_resource_is_accepted_and_gets_its_natural_id()
    {
        var api = Api("api", "api.read");

        (await SaveAsync(api)).Should().BeNull();
        api.Id.Should().Be(OidcScopeCatalog.ResourceId("api"));
        (await SaveAsync(new OidcResource { Name = "profile" })).Should().BeNull();
    }

    [Fact]
    public async Task A_resource_name_with_whitespace_is_rejected()
    {
        var error = await SaveAsync(new OidcResource { Name = "api read" });

        error!.Message.Should().Contain("space-delimited",
            "scopes are space-delimited on the wire, so this would be read as two names, "
            + "neither of which exists");
    }

    [Fact]
    public async Task An_api_scope_name_with_whitespace_is_rejected()
    {
        (await SaveAsync(Api("api", "api.read all")))!.Message.Should().Contain("space-delimited");
    }

    [Fact]
    public async Task An_empty_resource_name_is_rejected()
    {
        (await SaveAsync(new OidcResource { Name = "" }))!.Message.Should().Contain("required");
    }

    [Fact]
    public async Task An_empty_api_scope_name_is_rejected()
    {
        (await SaveAsync(Api("api", "")))!.Message.Should().Contain("needs a name");
    }

    [Theory]
    [InlineData("api.read", "dot")]
    [InlineData("api/read", "slash")]
    public async Task A_resource_name_with_a_separator_is_rejected(string name, string expected)
    {
        (await SaveAsync(new OidcResource { Name = name }))!.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task An_api_scope_outside_the_apis_prefix_is_rejected()
    {
        (await SaveAsync(Api("api", "other.read")))!.Message.Should().Contain("must start with the API's name");
    }

    [Fact]
    public async Task An_identity_resource_with_scopes_is_rejected()
    {
        var resource = new OidcResource { Name = "profile", Scopes = [new OidcApiScope { Name = "profile.x" }] };

        (await SaveAsync(resource))!.Message.Should().Contain("no scopes of its own");
    }
}
