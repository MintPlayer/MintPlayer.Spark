using MintPlayer.Spark;

namespace SparkId;

/// <summary>
/// SparkId has no business entities of its own: it is the identity provider HR, Fleet and QnA sign
/// in against (<c>docs/identity_provider_platform_PRD.md</c> R1). The provider's screens, queries and
/// rights come from the MintPlayer.Spark.IdentityProvider library layer; this application only binds
/// its slots (<c>App_Data/security.json</c>).
/// </summary>
public class SparkIdContext : SparkContext
{
}
