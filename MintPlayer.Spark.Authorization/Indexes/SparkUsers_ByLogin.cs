using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Authorization.Indexes;

/// <summary>
/// Fanout index on the Logins collection property of SparkUser documents.
/// Required because RavenDB auto-indexes do not support Any() with multiple fields.
/// </summary>
internal class SparkUsers_ByLogin<TUser> : AbstractIndexCreationTask<TUser>
    where TUser : SparkUser
{
    public override string IndexName => "SparkUsers/ByLogin";

    public SparkUsers_ByLogin()
    {
        Map = users => from user in users
            from login in user.Logins
            select new
            {
                Logins_LoginProvider = login.LoginProvider,
                Logins_ProviderKey = login.ProviderKey,
            };
    }
}
