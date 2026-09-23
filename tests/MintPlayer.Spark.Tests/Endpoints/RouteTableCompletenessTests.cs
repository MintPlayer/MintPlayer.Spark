using MintPlayer.AspNetCore.Endpoints;
using System.Reflection;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// Every endpoint Spark serves must appear in the places that are maintained by hand (#431 M10).
/// </summary>
/// <remarks>
/// Endpoint registration is source-generated from the <c>IMemberOf&lt;TGroup&gt;</c> marker, so adding
/// an endpoint needs no central <c>Map*</c> edit — which is the right design and also why nothing
/// notices when the hand-maintained companions are not updated with it. A new endpoint can ship
/// absent from the protocol client, the README's route table, the API specification and the deny-all
/// mirror, and every existing test stays green.
/// <para>
/// This test is deliberately about <b>presence</b>, not correctness. It cannot tell whether the
/// documented shape is right; it can only refuse to let an endpoint exist that nothing mentions,
/// which is the failure that actually happened repeatedly.
/// </para>
/// <para>
/// ⚠️ It reads repository files by walking up from the test assembly. If the layout moves, this test
/// fails loudly rather than silently passing on an empty search — an assertion over a file that was
/// never found is the exact shape of a test that guards nothing.
/// </para>
/// </remarks>
public class RouteTableCompletenessTests
{
    /// <summary>Every concrete endpoint's fully-qualified route, derived the way registration derives it.</summary>
    public static TheoryData<string> EndpointRoutes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var route in DiscoverRoutes())
                data.Add(route);
            return data;
        }
    }

    [Fact]
    public void The_discovery_itself_finds_endpoints()
    {
        // Without this, every assertion below would vacuously pass the day the marker interface or
        // the assembly reference changes shape.
        DiscoverRoutes().Should().NotBeEmpty("route discovery must actually find Spark's endpoints");
    }

    [Theory]
    [MemberData(nameof(EndpointRoutes))]
    public void Every_endpoint_appears_in_the_client_readme_route_table(string route)
    {
        var readme = ReadRepositoryFile("libs/client/MintPlayer.Spark.Client/README.md");

        readme.Should().Contain(route,
            $"'{route}' is served but the protocol client's route table does not mention it — a caller "
            + "reading that table would conclude the endpoint does not exist");
    }

    [Theory]
    [MemberData(nameof(EndpointRoutes))]
    public void Every_endpoint_appears_in_the_api_specification(string route)
    {
        var spec = ReadRepositoryFile("docs/Spark-API-Specification.md");

        spec.Should().Contain(route,
            $"'{route}' is served but is undocumented; the specification is what an external "
            + "implementer builds against");
    }

    private static IReadOnlyList<string> DiscoverRoutes()
    {
        var assembly = typeof(SparkContext).Assembly;
        var routes = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;

            var isEndpoint = type.GetInterfaces().Any(i =>
                i == typeof(IGetEndpoint) || i == typeof(IPostEndpoint)
                || i == typeof(IPutEndpoint) || i == typeof(IDeleteEndpoint));

            if (!isEndpoint) continue;

            var path = type.GetProperty("Path", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string;
            if (path is null) continue;

            routes.Add(ResolvePrefix(type) + path);
        }

        return routes;
    }

    /// <summary>Walks the <c>IMemberOf&lt;TGroup&gt;</c> chain, concatenating each group's prefix.</summary>
    private static string ResolvePrefix(Type type)
    {
        var memberOf = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMemberOf<>));

        if (memberOf is null) return string.Empty;

        var group = memberOf.GetGenericArguments()[0];
        var prefix = group.GetProperty("Prefix", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as string ?? string.Empty;

        return ResolvePrefix(group) + prefix;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' by walking up from {AppContext.BaseDirectory}. "
            + "This test asserts on repository files; a layout change must fail here rather than "
            + "silently assert over nothing.");
    }
}
