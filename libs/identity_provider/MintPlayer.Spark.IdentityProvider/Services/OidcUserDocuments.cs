using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Where the application keeps its users: the collection of the user type <c>AddAuthentication&lt;TUser&gt;()</c>
/// registered. Read lazily, because that call may come after <c>AddIdentityProvider</c>.
/// </summary>
internal sealed class OidcUserDocuments(SparkModuleRegistry registry, IDocumentStore store, IServiceScopeFactory scopes)
{
    private Func<IServiceProvider, string, Task<string?>>? findIdByEmail;

    /// <summary>The users' collection name, validated for splicing into RQL (identifiers cannot be parameters).</summary>
    public string Collection => RqlIdentifier.Collection(store.Conventions.FindCollectionName(UserType));

    /// <summary>
    /// The id of the account whose address is <paramref name="email"/>, or null. Through the Identity user store's own
    /// lookup (<c>UserManager.FindByEmailAsync</c>), which reads the address's compare-exchange reservation and loads
    /// the user by id, so an account registered a moment ago is found. A query on <c>NormalizedEmail</c> rides an index
    /// that may not have caught up yet, and then answered "no such account" (parity PRD A8).
    /// </summary>
    public async Task<string?> FindIdByEmailAsync(string email)
    {
        // Closed over the user type once, on first use: the type is known only at run time.
        var find = findIdByEmail ??= typeof(OidcUserDocuments)
            .GetMethod(nameof(FindIdByEmailFor), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(UserType)
            .CreateDelegate<Func<IServiceProvider, string, Task<string?>>>();

        // UserManager is scoped; this service is a singleton.
        using var scope = scopes.CreateScope();
        return await find(scope.ServiceProvider, email);
    }

    private static async Task<string?> FindIdByEmailFor<TUser>(IServiceProvider services, string email)
        where TUser : SparkUser
        => (await services.GetRequiredService<UserManager<TUser>>().FindByEmailAsync(email))?.Id;

    private Type UserType => registry.IdentityUserType ?? throw new InvalidOperationException(
        "The identity provider needs the application's user type. Call AddAuthentication<TUser>() on the Spark builder.");
}
