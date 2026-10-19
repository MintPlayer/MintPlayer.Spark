using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MintPlayer.Spark.Tests.Architecture;

/// <summary>
/// #521 X20 item 9: an endpoint answers a <c>SparkAccessDeniedException</c> through the endpoint filter
/// (<c>SparkDenialFilters.RefuseDeniedInEnvelope</c> / <c>RefuseDeniedAsJson</c>, installed in its
/// <c>Configure</c>), never with a hand-written <c>catch</c>. 33 near-identical blocks were replaced; this
/// keeps a 34th from appearing.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>catch</c> of <c>SparkAccessDeniedException</c> or <c>SparkRowLevelAccessDeniedException</c> in
/// <c>libs/</c> and <c>apps/</c> must sit in a type on <see cref="Allowed"/>, each with its reason: places
/// that turn a denial into a domain outcome (an MCP refusal, a <c>null</c> row, a notification), not
/// into the HTTP refusal. A hand-written HTTP refusal is how the shapes drift apart and the existence
/// oracle (#453, M-3) comes back.
/// </para>
/// <para>
/// A syntax scan, as in <c>EndpointConventionsTests</c>: a text grep would match comments, and a parse
/// error fails the test rather than hiding a file. Allow-list entries that match nothing fail too.
/// </para>
/// </remarks>
public class DenialCatchGuardTests
{
    private static readonly (string TypeName, string Reason)[] Allowed =
    [
        ("SparkDenialFilters", "the filters themselves"),
        ("DistinctValues", "a 404 naming the query ('Query {id} not found'), the shape the query endpoints keep; not the uniform refusal"),
        ("CustomActionExecution", "maps a denial to CustomActionOutcome.Refused, shared by the HTTP endpoint and the MCP tools"),
        ("PersistentObjectReadPath", "maps a denial to a null row, shared by the HTTP read and the MCP tools"),
        ("QueryReadPath", "maps a denial to QueryReadOutcome.NotFound, shared by the HTTP read and the MCP tools"),
        ("SaveResponsePresenter", "may save, may not read: falls back to the posted object"),
        ("McpPlanner", "an MCP plan refusal, not an HTTP response"),
        ("McpCustomActions", "an MCP action outcome (Refused), not an HTTP response"),
        ("SeoTools", "MCP tool results (SparkMcpResult.Refuse), not HTTP responses"),
        ("OrganizationActionUser", "a custom action's notification ('organizations.notAllowed') and a false outcome"),
        ("OgImageEndpoint", "a row-level denial is a missing page, answered by the endpoint's own bare 404 below"),
    ];

    private static readonly HashSet<string> DenialTypes = ["SparkAccessDeniedException", "SparkRowLevelAccessDeniedException"];

    [Fact]
    public void No_code_catches_a_denial_outside_the_allow_list()
    {
        var findings = Findings();

        findings.Should().NotBeEmpty("the scan must find the allowed catches, or it passes by scanning nothing");
        var offenders = findings
            .Where(f => !Allowed.Any(a => a.TypeName == f.TypeName))
            .Select(f => $"{f.File}:{f.Line}: {f.TypeName}")
            .ToList();

        offenders.Should().BeEmpty(
            "an endpoint answers a denial through SparkDenialFilters in its Configure "
            + "(builder.RefuseDeniedInEnvelope() or RefuseDeniedAsJson()); a catch that maps a denial to a "
            + "domain outcome instead needs a reasoned entry in DenialCatchGuardTests.Allowed");
    }

    [Fact]
    public void Every_allow_list_entry_still_matches_a_catch()
    {
        var findings = Findings();

        var stale = Allowed
            .Where(a => !findings.Any(f => f.TypeName == a.TypeName))
            .Select(a => a.TypeName)
            .ToList();

        stale.Should().BeEmpty("an exception that no longer matches anything must be removed, not kept as a licence");
    }

    private sealed record Finding(string File, int Line, string TypeName);

    private static List<Finding>? cached;

    private static List<Finding> Findings()
    {
        if (cached is not null)
            return cached;

        var repo = RepositoryRoot();
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var findings = new List<Finding>();

        foreach (var top in new[] { "libs", "apps" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(repo, top), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repo, path).Replace('\\', '/');
                if (IsExcluded(relative))
                    continue;

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);
                tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Should().BeEmpty($"{relative} must parse, or its catches would be skipped");

                foreach (var clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
                {
                    if (clause.Declaration?.Type is not { } type || !DenialTypes.Contains(RightmostName(type)))
                        continue;

                    var owner = clause.Ancestors().OfType<TypeDeclarationSyntax>().LastOrDefault();
                    findings.Add(new Finding(
                        relative,
                        clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        owner?.Identifier.Text ?? "<top-level>"));
                }
            }
        }

        return cached = findings;
    }

    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => type.ToString(),
    };

    private static bool IsExcluded(string relative)
    {
        var segments = relative.Split('/');
        return segments.Any(s => s is "bin" or "obj" or "node_modules"
            || s.EndsWith(".Tests", StringComparison.Ordinal)
            || s.EndsWith("Generators", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
