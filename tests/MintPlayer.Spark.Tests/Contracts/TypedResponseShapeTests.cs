using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Contracts;
using MintPlayer.Spark.Authorization.Endpoints.Account;
using MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;
using MintPlayer.Spark.Endpoints.Actions;
using MintPlayer.Spark.IdentityProvider.Endpoints.Portal;
using MintPlayer.Spark.SchemaGenerator;

namespace MintPlayer.Spark.Tests.Contracts;

/// <summary>
/// The responses that used to be anonymous objects and are typed records now (commerce X20 item 2,
/// <c>docs/guide-typescript-contracts.md</c>). Each test serializes the record and the anonymous object it replaced,
/// exactly as the endpoint used to build it, with the web defaults every Spark endpoint writes with, and requires the
/// same bytes: CodeCoverage and SeoTester are production apps on these endpoints, so the wire must not move.
/// </summary>
public sealed class TypedResponseShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void SameWire(object typed, object anonymous)
        => JsonSerializer.Serialize(typed, typed.GetType(), Web).Should().Be(JsonSerializer.Serialize(anonymous, anonymous.GetType(), Web));

    [Fact]
    public void External_logins_list_keeps_its_shape()
    {
        UserLoginInfo[] logins = [new("GitHub", "gh-1", "GitHub"), new("Microsoft", "ms-1", null)];
        AuthenticationScheme[] schemes =
        [
            new("GitHub", "GitHub", typeof(NoopHandler)),
            new("Google", "Google", typeof(NoopHandler)),
            new("Identity.Application", null, typeof(NoopHandler)),
        ];

        var typed = ListExternalLogins<SparkUser>.Describe(logins, canUnlink: true, schemes);

        var linked = logins.Select(l => l.LoginProvider).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anonymous = new
        {
            linked = logins.Select(l => new
            {
                provider = l.LoginProvider,
                providerKey = l.ProviderKey,
                displayName = l.ProviderDisplayName ?? l.LoginProvider,
                canUnlink = true,
            }),
            available = schemes
                .Where(scheme => scheme.DisplayName is not null && !linked.Contains(scheme.Name))
                .Select(scheme => new { provider = scheme.Name, displayName = scheme.DisplayName }),
        };

        SameWire(typed, anonymous);
        JsonSerializer.Serialize(typed, Web).Should().Contain("\"available\":[{\"provider\":\"Google\",\"displayName\":\"Google\"}]");
    }

    [Fact]
    public void Personal_data_keeps_its_shape()
    {
        var user = new SparkUser
        {
            Id = "users/1",
            UserName = "ada",
            Email = "ada@example.test",
            EmailConfirmed = true,
            PhoneNumber = null,
            TwoFactorEnabled = true,
            CreatedAtUtc = new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc),
            RegistrationMethod = "password",
            PreferredCulture = "nl-BE",
            Roles = ["Administrators"],
            Claims = [new SparkUserClaim { ClaimType = "tenant", ClaimValue = "t1" }],
            Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = "gh-1", ProviderDisplayName = null }],
            Passkeys = [new SparkUserPasskey { Name = "Laptop", CreatedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)) }],
        };
        var contributions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["orders"] = new[] { new { number = "O-1" } } };

        var typed = PersonalData<SparkUser>.Describe(user, hasPassword: false, contributions);

        var anonymous = new
        {
            account = new
            {
                id = user.Id,
                userName = user.UserName,
                email = user.Email,
                emailConfirmed = user.EmailConfirmed,
                phoneNumber = user.PhoneNumber,
                phoneNumberConfirmed = user.PhoneNumberConfirmed,
                twoFactorEnabled = user.TwoFactorEnabled,
                hasPassword = false,
                createdAtUtc = user.CreatedAtUtc,
                registrationMethod = user.RegistrationMethod,
                preferredCulture = user.PreferredCulture,
                preferredTimeZone = user.PreferredTimeZone,
                roles = user.Roles,
                claims = user.Claims.Select(c => new { type = c.ClaimType, value = c.ClaimValue }),
                externalLogins = user.Logins.Select(l => new { provider = l.LoginProvider, displayName = l.ProviderDisplayName }),
                passkeys = user.Passkeys.Select(p => new { name = p.Name, createdAt = p.CreatedAt }),
            },
            data = contributions,
        };

        SameWire(typed, anonymous);
        JsonSerializer.Serialize(typed, Web).Should().Contain("\"data\":{\"orders\":[{\"number\":\"O-1\"}]}", "a contributor's data keeps its runtime shape");
    }

    [Fact]
    public void Phone_verification_outcome_keeps_its_shape()
    {
        var expires = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        SameWire(new SparkPhoneVerificationOutcome("codeSent", "+32470123456", expires), new { outcome = "codeSent", phoneNumber = "+32470123456", expiresAtUtc = (DateTime?)expires });
        SameWire(new SparkPhoneVerificationOutcome("verified", "+32470123456", null), new { outcome = "verified", phoneNumber = "+32470123456", expiresAtUtc = (DateTime?)null });
    }

    [Fact]
    public void The_small_success_and_error_bodies_keep_their_shape()
    {
        SameWire(new SparkAuthError("last_credential"), new { error = "last_credential" });
        SameWire(SparkExternalLoginUnlinked.Done, new { unlinked = true });
        SameWire(SparkPasskeySignedIn.Done, new { success = true });
        SameWire(SparkGrantRevoked.Done, new { revoked = true });
    }

    /// <summary>The lookup-reference endpoints' refusals (AddValue, UpdateValue, DeleteValue, Get).</summary>
    [Fact]
    public void Lookup_reference_errors_keep_their_shape()
    {
        const string name = "Colors";
        SameWire(new SparkErrorResponse("Operation failed"), new { error = "Operation failed" });
        SameWire(new SparkErrorResponse("Invalid request body"), new { error = "Invalid request body" });
        SameWire(new SparkErrorResponse($"LookupReference '{name}' not found"), new { error = $"LookupReference '{name}' not found" });
        JsonSerializer.Serialize(new SparkErrorResponse("Operation failed"), Web).Should().Be("{\"error\":\"Operation failed\"}");
    }

    /// <summary>
    /// SparkDenial's uniform refusals (404 "Not found", 401 "Authentication required", and NotFoundOrSignIn's own
    /// message, null included), which CodeCoverage's controllers also answer with.
    /// </summary>
    [Fact]
    public void Denial_bodies_keep_their_shape()
    {
        var noServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        var anonymous = new DefaultHttpContext { RequestServices = noServices };
        var (body, status) = MintPlayer.Spark.Endpoints.SparkDenial.Refuse(anonymous);
        status.Should().Be(StatusCodes.Status401Unauthorized);
        SameWire(body, new { error = "Authentication required" });

        var signedIn = new DefaultHttpContext { RequestServices = noServices, User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([], "test")) };
        var (signedInBody, signedInStatus) = MintPlayer.Spark.Endpoints.SparkDenial.Refuse(signedIn);
        signedInStatus.Should().Be(StatusCodes.Status404NotFound);
        SameWire(signedInBody, new { error = "Not found" });

        SameWire(new SparkErrorResponse("Authentication required"), new { error = "Authentication required" });
        SameWire(new SparkErrorResponse("Entity type 'X' not found"), new { error = "Entity type 'X' not found" });
        string? none = null;
        SameWire(new SparkErrorResponse(none!), new { error = none });
    }

    [Fact]
    public void Identity_provider_portal_responses_keep_their_shape()
    {
        IReadOnlyList<string> changes = ["activated kid-2", "retired kid-1"];
        SameWire(new OidcKeyRotationResponse(changes), new { changes });
        SameWire(new OidcRegistrationTokenResponse("iat-123", 3600), new { initialAccessToken = "iat-123", expiresIn = 3600 });
    }

    [Fact]
    public void Custom_action_catalogue_entry_keeps_its_shape()
    {
        var custom = new global::MintPlayer.Spark.Models.ActionDefinition
        {
            Name = "Approve",
            Label = TranslatedString.Of("en", "Approve"),
            Icon = "check",
            ShowedOn = "both",
            SelectionRule = "=1",
            Offset = 10,
            DeclaredBy = "app",
            Sources = new Dictionary<string, string>(),
        };
        var builtIn = new global::MintPlayer.Spark.Models.ActionDefinition
        {
            Name = "New",
            Label = TranslatedString.Of("en", "New"),
            ShowedOn = "query",
            RequiresClient = "webauthn.create",
            DeclaredBy = "core",
            Sources = new Dictionary<string, string>(),
        };

        // The old private record had lower-case members and two members omitted when null.
        SameWire(ListCustomActions.Describe(custom), new
        {
            name = "Approve",
            label = custom.Label,
            icon = (string?)"check",
            description = (TranslatedString?)null,
            showedOn = "both",
            selectionRule = (string?)"=1",
            refreshOnCompleted = false,
            confirmation = (TranslatedString?)null,
            variant = (string?)null,
            offset = 10,
        });
        SameWire(ListCustomActions.Describe(builtIn), new
        {
            name = "New",
            label = builtIn.Label,
            icon = (string?)null,
            description = (TranslatedString?)null,
            showedOn = "query",
            selectionRule = (string?)null,
            refreshOnCompleted = false,
            confirmation = (TranslatedString?)null,
            variant = (string?)null,
            offset = 0,
            isDefault = true,
            requiresClient = "webauthn.create",
        });
    }

    /// <summary>The converted responses are generated into the modules ng-spark reads them from.</summary>
    [Fact]
    public void The_converted_responses_are_library_contracts()
    {
        var modules = SparkLibraryContracts.Generate().ToDictionary(m => m.Module, m => m.Content);

        var auth = modules["@mintplayer/ng-spark/auth/models"];
        foreach (var name in new[] { "SparkAuthError", "SparkExternalLogins", "SparkLinkedExternalLogin", "SparkAvailableExternalProvider", "SparkExternalLoginUnlinked", "SparkPasskeySignedIn", "SparkGrantRevoked", "SparkPhoneVerificationOutcome", "SparkPersonalData", "SparkPersonalDataAccount" })
            auth.Should().Contain($"export interface {name} {{\n");

        var portal = modules["@mintplayer/ng-spark/identity-provider/core"];
        foreach (var name in new[] { "SparkConnectedApplication", "SparkConnectedScope", "SparkWithdrawAccessRequest", "SparkDeveloperStatus", "SparkRegistrationToken", "SparkInvitationOutcome", "SparkSigningKey", "SparkKeyRotation" })
            portal.Should().Contain($"export interface {name} {{\n");

        var models = modules["@mintplayer/ng-spark/models"];
        foreach (var name in new[] { "EntityTypeDefinition", "EntityAttributeDefinition", "SparkQuery", "QueryResult", "QueryColumn", "ProgramUnitsConfiguration", "LookupReferenceDto", "LookupReferenceListItem", "CustomActionDefinition" })
            models.Should().Contain($"export interface {name} {{\n");
        models.Should().Contain("  queries: SparkSubQueryEntry[];\n", "a sub-query entry is the hand-written union, not the C# class");
        models.Should().Contain("import type { SparkSubQueryEntry } from '../sub-query';\n");
        models.Should().Contain("export type SparkSelectionModeSetting = 'auto' | 'none' | 'multiple';\n");
    }

    private sealed class NoopHandler : IAuthenticationHandler
    {
        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context) => Task.CompletedTask;
        public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
