using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Where the application keeps its users: the collection of the user type <c>AddAuthentication&lt;TUser&gt;()</c>
/// registered. Read lazily, because that call may come after <c>AddIdentityProvider</c>.
/// </summary>
internal sealed class OidcUserDocuments(SparkModuleRegistry registry, IDocumentStore store)
{
    /// <summary>The users' collection name, validated for splicing into RQL (identifiers cannot be parameters).</summary>
    public string Collection => RqlIdentifier.Collection(store.Conventions.FindCollectionName(
        registry.IdentityUserType ?? throw new InvalidOperationException(
            "The identity provider needs the application's user type. Call AddAuthentication<TUser>() on the Spark builder.")));
}
