using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// #460 D5 — secrets at rest — and the rest of spike SP-B: Data Protection round trip through the
/// store, a restart over the persisted Raven key ring, legacy plaintext, the backfill's idempotence,
/// the unreadable-key rule, and the token path CodeCoverage's GitHub refresh uses.
/// </summary>
public class UserSecretsAtRestTests : SparkTestDriver
{
    /// <summary>
    /// A container resolving Identity over the test store with Spark's own Data Protection wiring —
    /// a fresh call is a restarted process.
    /// </summary>
    private ServiceProvider Services(string applicationName = "SecretsTests")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:DataProtection:ApplicationName"] = applicationName,
            ["Spark:DataProtection:Storage"] = "RavenDb",
        }).Build());
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Production", ContentRootPath = Path.GetTempPath() });
        services.AddSingleton(Store);
        services.AddLogging();
        services.AddSparkDataProtection();
        services.AddSparkAuthentication<SparkUser>();
        return services.BuildServiceProvider();
    }

    private static async Task<T> InScope<T>(ServiceProvider provider, Func<UserManager<SparkUser>, Task<T>> action)
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>());
    }

    private async Task<SparkUser> RawAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<SparkUser>(id))!;
    }

    private async Task<string> CreateAsync(ServiceProvider provider, string email)
        => await InScope(provider, async users =>
        {
            var user = new SparkUser { UserName = email, Email = email, EmailConfirmed = true };
            (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
            return user.Id!;
        });

    [Fact]
    public async Task SP_B_the_authenticator_key_and_tokens_are_stored_protected_and_read_back()
    {
        using var provider = Services();
        var id = await CreateAsync(provider, "dp@example.com");

        var (key, token) = await InScope(provider, async users =>
        {
            var user = (await users.FindByIdAsync(id))!;
            await users.ResetAuthenticatorKeyAsync(user);
            // The calls CodeCoverage's GitHubUserTokenService makes for its access/refresh tokens.
            await users.SetAuthenticationTokenAsync(user, "GitHub", "access_token", "gho_example-access");
            return ((await users.GetAuthenticatorKeyAsync(user))!, (await users.GetAuthenticationTokenAsync(user, "GitHub", "access_token"))!);
        });

        var raw = await RawAsync(id);
        raw.AuthenticatorKey.Should().StartWith(UserStore<SparkUser>.ProtectedValuePrefix);
        raw.AuthenticatorKey.Should().NotContain(key);
        raw.Tokens.Single().Value.Should().StartWith(UserStore<SparkUser>.ProtectedValuePrefix);
        raw.Tokens.Single().Value.Should().NotContain("gho_example-access");

        key.Should().MatchRegex("^[A-Z2-7]+$", "the plaintext read back is the base32 key Identity generated");
        token.Should().Be("gho_example-access");
    }

    [Fact]
    public async Task SP_B_a_restarted_process_reads_what_the_previous_one_protected()
    {
        string id, key;
        using (var first = Services())
        {
            id = await CreateAsync(first, "restart@example.com");
            key = await InScope(first, async users =>
            {
                var user = (await users.FindByIdAsync(id))!;
                await users.ResetAuthenticatorKeyAsync(user);
                await users.SetAuthenticationTokenAsync(user, "GitHub", "refresh_token", "ghr_example");
                return (await users.GetAuthenticatorKeyAsync(user))!;
            });
        }

        using var second = Services();
        var (readKey, readToken) = await InScope(second, async users =>
        {
            var user = (await users.FindByIdAsync(id))!;
            return (await users.GetAuthenticatorKeyAsync(user), await users.GetAuthenticationTokenAsync(user, "GitHub", "refresh_token"));
        });

        readKey.Should().Be(key);
        readToken.Should().Be("ghr_example");
    }

    [Fact]
    public async Task SP_B_legacy_plaintext_is_read_and_the_backfill_protects_it_once()
    {
        var legacy = new SparkUser
        {
            UserName = "legacy",
            Email = "legacy@example.com",
            AuthenticatorKey = "JBSWY3DPEHPK3PXP",
            Tokens = [new SparkUserToken { LoginProvider = "GitHub", Name = "access_token", Value = "gho_legacy" }],
        };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(legacy);
            await session.SaveChangesAsync();
        }

        using var provider = Services();
        (await InScope(provider, async users => await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(legacy.Id!))!)))
            .Should().Be("JBSWY3DPEHPK3PXP", "a value without the prefix is legacy plaintext");

        var backfill = provider.GetRequiredService<SparkUserBackfill<SparkUser>>();
        var first = await backfill.RunAsync();
        var raw = await RawAsync(legacy.Id!);
        var second = await backfill.RunAsync();

        first.Skipped.Should().BeFalse();
        first.SecretsProtected.Should().Be(1);
        raw.AuthenticatorKey.Should().StartWith(UserStore<SparkUser>.ProtectedValuePrefix);
        raw.Tokens.Single().Value.Should().StartWith(UserStore<SparkUser>.ProtectedValuePrefix);
        second.Skipped.Should().BeTrue("the completion marker exists");

        // Idempotent even without the marker: nothing is re-protected.
        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(SparkUserBackfill<SparkUser>.MarkerId);
            await session.SaveChangesAsync();
        }
        var third = await backfill.RunAsync();
        third.SecretsProtected.Should().Be(0);
        (await RawAsync(legacy.Id!)).AuthenticatorKey.Should().Be(raw.AuthenticatorKey);

        (await InScope(provider, async users => await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(legacy.Id!))!)))
            .Should().Be("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public async Task SP_B_an_unreadable_authenticator_key_keeps_two_factor_required()
    {
        string id, key;
        using (var original = Services("OriginalRing"))
        {
            id = await CreateAsync(original, "lost-ring@example.com");
            key = await InScope(original, async users =>
            {
                var user = (await users.FindByIdAsync(id))!;
                await users.ResetAuthenticatorKeyAsync(user);
                await users.SetTwoFactorEnabledAsync(user, true);
                return (await users.GetAuthenticatorKeyAsync(user))!;
            });
        }

        // Another application name: the key ring cannot unprotect what the first one wrote — the same
        // position as a redeploy that lost its keys.
        using var lost = Services("AnotherRing");
        var (validProviders, codeAccepted) = await InScope(lost, async users =>
        {
            var user = (await users.FindByIdAsync(id))!;
            var providers = await users.GetValidTwoFactorProvidersAsync(user);
            var accepted = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider,
                Extensions.AccountTestHost.Totp(key));
            return (providers, accepted);
        });

        validProviders.Should().Contain(TokenOptions.DefaultAuthenticatorProvider,
            "a null key would leave no valid provider, and Identity would skip the second factor");
        codeAccepted.Should().BeFalse("nobody holds the key the store answers");
    }

    [Fact]
    public async Task SP_B_ravendb_keeps_no_creation_timestamp_in_metadata()
    {
        // The measurement behind SparkUserBackfill's CreatedAtUtc source (PRD §4.1).
        using var session = Store.OpenAsyncSession();
        var user = new SparkUser { UserName = "meta", Email = "meta@example.com" };
        await session.StoreAsync(user);
        await session.SaveChangesAsync();

        using var reader = Store.OpenAsyncSession();
        var loaded = await reader.LoadAsync<SparkUser>(user.Id);
        var keys = reader.Advanced.GetMetadataFor(loaded).Keys.Order(StringComparer.Ordinal).ToArray();

        keys.Should().Contain("@last-modified");
        keys.Should().NotContain("@created");
    }

    [Fact]
    public async Task The_backfill_fills_CreatedAtUtc_from_the_oldest_revision()
    {
        // Stored before revisions exist: no history, so its CreatedAtUtc must stay unknown.
        var withoutRevisions = new SparkUser { UserName = "norev", Email = "norev@example.com" };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(withoutRevisions);
            await session.SaveChangesAsync();
        }

        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                ["SparkUsers"] = new() { Disabled = false },
            },
        }));

        var user = new SparkUser { UserName = "rev", Email = "rev@example.com" };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(user);
            await session.SaveChangesAsync();
        }
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<SparkUser>(user.Id)).PhoneNumber = "changed";
            await session.SaveChangesAsync();
        }
        using var provider = Services();
        var result = await provider.GetRequiredService<SparkUserBackfill<SparkUser>>().RunAsync();

        result.CreatedAtFilled.Should().Be(1);
        var raw = await RawAsync(user.Id!);
        raw.CreatedAtUtc.HasValue.Should().BeTrue();
        (DateTime.UtcNow - raw.CreatedAtUtc!.Value).Should().BeLessThan(TimeSpan.FromMinutes(5));
        (await RawAsync(withoutRevisions.Id!)).CreatedAtUtc.HasValue.Should().BeFalse("a last-modified date is not a creation date");
    }

    [Fact]
    public async Task Create_stamps_CreatedAtUtc_and_the_registration_method()
    {
        using var provider = Services();
        var id = await InScope(provider, async users =>
        {
            var user = new SparkUser { UserName = "stamped", Email = "stamped@example.com" };
            (await users.CreateAsync(user, "Correct-horse-1")).Succeeded.Should().BeTrue();
            return user.Id!;
        });
        var admin = await CreateAsync(provider, "admin-made@example.com");

        var raw = await RawAsync(id);
        raw.CreatedAtUtc.HasValue.Should().BeTrue();
        raw.RegistrationMethod.Should().Be(SparkRegistrationMethods.Password);
        (await RawAsync(admin)).RegistrationMethod.Should().Be(SparkRegistrationMethods.Other);
    }
}
