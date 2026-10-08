using Microsoft.AspNetCore.Routing;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization;

/// <summary>
/// Stand-in endpoint types for the part of <c>MapIdentityApi</c> Spark keeps, which has no endpoint
/// class. Each of those routes carries an <see cref="EndpointTypeMetadata"/> naming one of these, so any
/// code can ask whether it is served with
/// <see cref="EndpointDataSourceExtensions.IsEndpointMapped{TEndpoint}"/> instead of matching route strings:
/// <code>
/// endpointDataSource.IsEndpointMapped&lt;SparkIdentityEndpoints.TwoFactor&gt;()
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Ask at request time or from <c>ApplicationStarted</c> on: the container's
/// <see cref="EndpointDataSource"/> is empty until the host has started. Which of these are mapped
/// depends on <see cref="Configuration.SparkLocalCredentials"/>; an endpoint the mode excludes is
/// absent, so it answers <see langword="false"/>.
/// </para>
/// <para>
/// Spark's own account endpoints (register, confirmation, recovery, the <c>/manage</c> pages) are
/// endpoint classes and are asked about by class; they need no stand-in.
/// </para>
/// </remarks>
public static class SparkIdentityEndpoints
{
    /// <summary><c>POST /spark/auth/login</c> (Microsoft's): password sign-in.</summary>
    public sealed class Login { private Login() { } }

    /// <summary><c>POST /spark/auth/refresh</c> (Microsoft's): bearer-token refresh.</summary>
    public sealed class Refresh { private Refresh() { } }

    /// <summary><c>POST /spark/auth/manage/2fa</c> (Microsoft's): read and change two-factor settings.</summary>
    public sealed class TwoFactor { private TwoFactor() { } }

    /// <summary><c>GET /spark/auth/manage/info</c> (Microsoft's): the signed-in user's email.</summary>
    public sealed class Info { private Info() { } }
}
