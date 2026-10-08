using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// The regression guard for <c>docs/endpoints_generator_completion_PRD.md</c> (plan M8): every
/// minimal-API route Spark owns, in libraries and applications, is a generator endpoint class that
/// takes its dependencies through <c>[Inject]</c> and its input through typed binding. Three rules,
/// each with an explicit allow-list whose every entry names its reason:
/// <list type="number">
///   <item><b>R1 — no service location and no hand-read input in endpoint code.</b></item>
///   <item><b>R2 — no static class holds endpoint logic.</b></item>
///   <item><b>R3 — no route is mapped by hand.</b></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Why a Roslyn syntax scan, not BannedApiAnalyzers and not reflection.</b>
/// <list type="bullet">
///   <item>The rules are about <i>where</i> a call sits ("inside a class implementing
///   <c>IPostEndpoint</c>", "inside a static class"), which BannedSymbols cannot express: banning
///   <c>RequestServices</c> outright would also ban the middleware that legitimately uses it, and
///   the BannedApi wiring keyed on <c>IsTestProject</c> already rotted once in this repo.</item>
///   <item>Reflection sees the compiled shape but not the call sites: it cannot tell a
///   <c>Request.Query[…]</c> read from a <c>[QueryParam]</c>.</item>
///   <item>A text grep matches comments and the string templates inside source generators, and
///   cannot tell a class from its members. The syntax tree can, and a parse error fails the test
///   rather than hiding a file.</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>Allow-list entries that match nothing fail too</b>
/// (<see cref="Every_allow_list_entry_still_matches_a_finding"/>), so an exception that is fixed
/// cannot linger as a silent licence for the next regression.
/// </para>
/// </remarks>
public class EndpointConventionsTests
{
    // ---- R1 ------------------------------------------------------------------------------------

    /// <summary>
    /// R1 exceptions: (type, enclosing member, rule) → reason. Matched on the declaring type's simple
    /// name and the enclosing method, so a second read in the same type is not covered.
    /// </summary>
    private static readonly AllowedFinding[] HandReadExceptions =
    [
        new("DeleteAccount", "HandleAsync", Rule.ReadFromJsonAsync,
            "the DELETE body is optional (a password confirmation only for accounts that have one); a "
            + "typed DELETE body would be required. Plan S3."),
        new("ReceiveBounce", "HandleAsync", Rule.RequestBody,
            "the bounce secret is checked before anything is parsed, and the body is read under a size "
            + "cap; typed binding would buffer and parse an unauthenticated body first. PRD D3a."),
        new("SparkDenial", "AuthenticatingWouldHelp", Rule.RequestServices,
            "a static refusal helper shared by every endpoint (no instance to inject into); it reads the "
            + "singleton SparkModuleRegistry and fails toward 401 when it is absent. Plan M3/M3b."),
        new("SparkAuthenticationExtensions", "ExternalLoginOutcome", Rule.RequestQuery,
            "the shared exit of every external-login callback decides popup vs redirect from ?popup; it "
            + "is a helper called with the callback's context, not an endpoint. Plan M3/M3b."),
        new("SparkAntiforgeryMiddleware", "UseSparkAntiforgery", Rule.RequestServices,
            "middleware (PRD §3 non-goal) resolving IAntiforgery per request; no endpoint instance exists yet."),
        new("SparkPresentation", "Check", Rule.RequestServices,
            "a System.Text.Json serialization hook, which has no DI of its own; it only asks whether the host "
            + "is Development to throw on an unpresented object."),
    ];

    [Fact]
    public void Endpoint_code_neither_locates_services_nor_reads_input_by_hand()
    {
        var scan = Scan();
        var findings = R1Findings(scan);

        var offenders = findings
            .Where(f => !HandReadExceptions.Any(a => a.Matches(f)))
            .Select(f => f.ToString())
            .ToList();

        scan.EndpointTypes.Count.Should().BeGreaterThan(100,
            "the endpoint classes must be found, or this test passes by scanning nothing");
        offenders.Should().BeEmpty(
            "endpoint classes take services through [Inject] fields and input through typed binding "
            + "([RouteParam], [QueryParam], I…Endpoint<TRequest>, or a BindRequestAsync override). Fix the "
            + "code, or add a reasoned entry to HandReadExceptions in EndpointConventionsTests");
    }

    // ---- R2 ------------------------------------------------------------------------------------

    /// <summary>
    /// R2 exceptions: static classes that return <c>IResult</c> or take <c>HttpContext</c> but hold no
    /// route of their own. Each is pure: it shapes a response or a decision for an endpoint that
    /// already ran its authorization and binding. R1 still applies to every one of them.
    /// </summary>
    private static readonly AllowedStaticClass[] StaticHelperExceptions =
    [
        // spark core
        new("SparkDenial", "the one refusal shape (401 vs 403 vs 404) every endpoint answers with"),
        new("ClientResult", "the client-operations response envelope, built from values the endpoint passes in"),
        new("SparkPresentation", "reads the per-request presentation caller from HttpContext.Items; no route"),
        new("LookupReferenceBodies", "the shared OnBindFailedAsync refusal of lookup-reference add and update; its services arrive as arguments"),
        new("SparkAntiforgeryMiddleware", "cross-cutting middleware, a PRD §3 non-goal; it runs before endpoint selection"),
        new("SparkServiceWorkerCacheHeaders", "cross-cutting middleware (#464 D8) installed by an IStartupFilter ahead of the static files; it only sets Cache-Control on a fixed set of paths and maps no route"),
        // authorization
        new("SparkExternalLoginNonce", "the nonce shape check (#490 D1) shared by the two external-login challenges; Reject turns the value they already bound into the one 400 shape, and reads nothing from the request"),
        new("SparkAuthenticationExtensions", "registration extensions plus ExternalLoginOutcome, the shared popup/redirect exit of the external-login callbacks"),
        new("PasskeyEndpoints", "the passkey sign-in failure shape (SignInFailed), shared by the passkey endpoints"),
        // moderation
        new("ModerationEndpoint", "the shared refusal mapping of the 15 moderation endpoints; services arrive as explicit arguments from each endpoint's [Inject] fields"),
        // replication
        new("ModuleIdentity", "authentication helper that sets the module principal on HttpContext; no route"),
        new("SparkAccount", "the account endpoints' shared bare-status refusals (BindFailed) and validation-problem shape"),
        // identity provider (plan M4 'For M8'). The plan also named RedirectUrl, Token,
        // OidcAuthorizationFlow, OidcCors and OidcUserEndpoints: they neither return IResult nor take
        // HttpContext, so R2 does not flag them and they need no entry (the hygiene test would reject one).
        new("ConnectPage", "renders the /connect HTML pages from values the endpoint already resolved"),
        new("ConnectResults", "the OIDC error/redirect result shapes shared by the /connect endpoints"),
        new("ConnectPageTheme", "reads the theme cookie to pick the page's colour scheme; no route"),
        new("InteractiveUserExtensions", "reads the signed-in interactive user off HttpContext.User; no service lookup"),
    ];

    [Fact]
    public void No_static_class_holds_endpoint_logic()
    {
        var scan = Scan();
        var offenders = R2Findings(scan)
            .Where(f => !StaticHelperExceptions.Any(a => a.TypeName == f.TypeName))
            .Select(f => f.ToString())
            .ToList();

        offenders.Should().BeEmpty(
            "a static class returning IResult or taking HttpContext is a hand-written handler; make it a "
            + "generator endpoint class with [Inject] fields, or, if it is a pure helper, add it to "
            + "StaticHelperExceptions in EndpointConventionsTests with its reason");
    }

    // ---- R3 ------------------------------------------------------------------------------------

    /// <summary>R3 exceptions: (file name, mapping call) → reason. PRD §5.</summary>
    private static readonly AllowedMapping[] MappingExceptions =
    [
        new("SparkBuilderExtensions.cs", "MapGitHubWebhooks",
            "PRD §5/D4: Octokit owns the request-level X-Hub-Signature-256 refusal for the GitHub webhook "
            + "POST; see docs/endpoints_generator_webhooks_exception.md"),
        new("LocalCredentialEndpointFilter.cs", "MapIdentityApi",
            "PRD §5: Microsoft's monolithic MapIdentityApi<TUser>() is mapped onto a throwaway builder, "
            + "filtered, and republished through FixedEndpointDataSource"),
        new("SparkControllersExtensions.cs", "MapControllers",
            "PRD §5/D1: MVC controllers by design; applications may keep controllers"),
        new("SparkIdentityProviderExtensions.cs", "Map",
            "not a route: OidcUserEndpoints.Map closes the user-generic /connect endpoints and maps them "
            + "through MapEndpoint<T>()"),
    ];

    private static readonly HashSet<string> MappingMethods =
    [
        "Map", "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods",
        "MapFallback", "MapFallbackToFile", "MapHub", "MapHealthChecks",
        "MapControllers", "MapControllerRoute", "MapDefaultControllerRoute",
        "MapIdentityApi", "MapGitHubWebhooks",
    ];

    [Fact]
    public void Routes_are_mapped_only_through_the_generator()
    {
        var scan = Scan();
        var offenders = R3Findings(scan)
            .Where(f => !MappingExceptions.Any(a => a.Matches(f)))
            .Select(f => f.ToString())
            .ToList();

        offenders.Should().BeEmpty(
            "routes in libraries and applications are generator endpoint classes (PRD §2, D1b); a hand "
            + "mapping is allowed only for the PRD §5 exceptions listed in MappingExceptions");
    }

    // ---- allow-list hygiene ---------------------------------------------------------------------

    [Fact]
    public void Every_allow_list_entry_still_matches_a_finding()
    {
        var scan = Scan();
        var r1 = R1Findings(scan);
        var r2 = R2Findings(scan);
        var r3 = R3Findings(scan);

        var stale = HandReadExceptions.Where(a => !r1.Any(a.Matches)).Select(a => $"HandReadExceptions: {a}")
            .Concat(StaticHelperExceptions.Where(a => !r2.Any(f => f.TypeName == a.TypeName)).Select(a => $"StaticHelperExceptions: {a.TypeName}"))
            .Concat(MappingExceptions.Where(a => !r3.Any(a.Matches)).Select(a => $"MappingExceptions: {a}"))
            .ToList();

        stale.Should().BeEmpty("an exception that no longer matches anything must be removed, not kept as a licence");
    }

    // ---- rules ---------------------------------------------------------------------------------

    internal enum Rule
    {
        RequestServices,
        FromServices,
        ReadFromJsonAsync,
        ReadFormAsync,
        RequestBody,
        RequestForm,
        RequestQuery,
        RouteValues,
        StaticEndpointLogic,
        HandMappedRoute,
    }

    private sealed record Finding(Rule Rule, string File, int Line, string TypeName, string Member, string Snippet)
    {
        public override string ToString() => $"{File}:{Line}: [{Rule}] {TypeName}.{Member}: {Snippet}";
    }

    private sealed record AllowedFinding(string TypeName, string Member, Rule Rule, string Reason)
    {
        public bool Matches(Finding f) => f.Rule == Rule && f.TypeName == TypeName && f.Member == Member;
        public override string ToString() => $"{TypeName}.{Member} [{Rule}]";
    }

    private sealed record AllowedStaticClass(string TypeName, string Reason);

    private sealed record AllowedMapping(string FileName, string Method, string Reason)
    {
        public bool Matches(Finding f) => f.Rule == Rule.HandMappedRoute && Path.GetFileName(f.File) == FileName && f.Member == Method;
        public override string ToString() => $"{FileName} {Method}";
    }

    /// <summary>
    /// R1 runs over every endpoint class (and every class deriving from one), and over the
    /// allow-listed static helpers of R2: an exception there must not become a place to launder a
    /// service lookup out of an endpoint.
    /// </summary>
    private static List<Finding> R1Findings(ScanResult scan)
    {
        var helperNames = StaticHelperExceptions.Select(a => a.TypeName).ToHashSet();
        var findings = new List<Finding>();

        foreach (var (file, type) in scan.Types)
        {
            if (!scan.EndpointTypes.Contains(Key(type)) && !(IsStatic(type) && helperNames.Contains(type.Identifier.Text)))
                continue;

            foreach (var node in type.DescendantNodes())
            {
                // Nested types are visited as types of their own.
                if (node.Ancestors().OfType<TypeDeclarationSyntax>().First() != type)
                    continue;

                var rule = Classify(node);
                if (rule is null)
                    continue;

                // The typed-binding hook itself: D3 has the form-urlencoded OIDC endpoints override it.
                var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (method is not null
                    && method.Identifier.Text == "BindRequestAsync"
                    && method.Modifiers.Any(SyntaxKind.OverrideKeyword)
                    && rule is not (Rule.RequestServices or Rule.FromServices))
                    continue;

                findings.Add(MakeFinding(rule.Value, file, type, node));
            }
        }

        return findings;
    }

    private static Rule? Classify(SyntaxNode node)
    {
        switch (node)
        {
            case MemberAccessExpressionSyntax ma:
                var name = ma.Name.Identifier.Text;
                if (name == "RequestServices")
                    return Rule.RequestServices;
                if (name == "RouteValues")
                    return Rule.RouteValues;
                if (RightmostName(ma.Expression) == "Request")
                {
                    return name switch
                    {
                        "Body" or "BodyReader" => Rule.RequestBody,
                        "Form" => Rule.RequestForm,
                        // Not QueryString: the /connect pages echo the raw query into a returnUrl
                        // (Path + QueryString) without reading any value out of it.
                        "Query" => Rule.RequestQuery,
                        _ => null,
                    };
                }
                return null;

            case InvocationExpressionSyntax inv:
                return InvokedName(inv) switch
                {
                    "ReadFromJsonAsync" => Rule.ReadFromJsonAsync,
                    "ReadFormAsync" => Rule.ReadFormAsync,
                    "GetRouteValue" => Rule.RouteValues,
                    _ => null,
                };

            case AttributeSyntax attr:
                return RightmostName(attr.Name) is "FromServices" or "FromServicesAttribute" or "FromKeyedServices" or "FromKeyedServicesAttribute"
                    ? Rule.FromServices
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// A static class with a method that returns a response (<c>IResult</c>, <c>Results&lt;…&gt;</c>)
    /// or takes the request (<c>HttpContext</c>) holds endpoint logic.
    /// </summary>
    private static List<Finding> R2Findings(ScanResult scan)
    {
        var findings = new List<Finding>();

        foreach (var (file, type) in scan.Types)
        {
            if (type is not ClassDeclarationSyntax || !IsStatic(type))
                continue;

            var method = type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m =>
                ReturnsResult(m.ReturnType) || m.ParameterList.Parameters.Any(p => p.Type is not null && RightmostName(p.Type) == "HttpContext"));
            if (method is null)
                continue;

            findings.Add(MakeFinding(Rule.StaticEndpointLogic, file, type, method));
        }

        return findings;
    }

    private static bool ReturnsResult(TypeSyntax type) =>
        type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n => n.Identifier.Text is "IResult" or "Results");

    private static List<Finding> R3Findings(ScanResult scan)
    {
        var findings = new List<Finding>();

        foreach (var (file, root) in scan.Roots)
        {
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvokedName(inv);
                if (name is null || !MappingMethods.Contains(name))
                    continue;

                var type = inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                findings.Add(new Finding(Rule.HandMappedRoute, file, Line(inv), type?.Identifier.Text ?? "<top-level>", name, Snippet(inv)));
            }
        }

        return findings;
    }

    // ---- scanning ------------------------------------------------------------------------------

    private sealed record ScanResult(
        List<(string File, SyntaxNode Root)> Roots,
        List<(string File, TypeDeclarationSyntax Type)> Types,
        HashSet<string> EndpointTypes);

    private static ScanResult? cached;

    /// <summary>The generator's endpoint contracts and base classes, by simple name.</summary>
    private static bool IsEndpointBaseName(string name) =>
        name is "IEndpointBase" or "IEndpoint" or "IResponseEndpoint" or "EndpointBase" or "BodyEndpoint"
            or "IGetEndpoint" or "IPostEndpoint" or "IPutEndpoint" or "IDeleteEndpoint" or "IPatchEndpoint"
            or "GetEndpoint" or "PostEndpoint" or "PutEndpoint" or "DeleteEndpoint" or "PatchEndpoint";

    private static ScanResult Scan()
    {
        if (cached is not null)
            return cached;

        var repo = RepositoryRoot();
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var roots = new List<(string, SyntaxNode)>();
        var types = new List<(string, TypeDeclarationSyntax)>();

        foreach (var top in new[] { "libs", "apps" })
        {
            var dir = Path.Combine(repo, top);
            Directory.Exists(dir).Should().BeTrue($"'{top}' must exist, or this test passes by scanning nothing");

            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repo, path).Replace('\\', '/');
                if (IsExcluded(relative))
                    continue;

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);
                var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                errors.Should().BeEmpty($"{relative} must parse, or its endpoints would be skipped");

                var root = tree.GetRoot();
                roots.Add((relative, root));
                types.AddRange(root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(t => (relative, t)));
            }
        }

        // Endpoint types: a declaration whose base list names a generator contract, then every type
        // deriving from one of those, to a fixed point. Partial declarations share one key.
        var endpointKeys = new HashSet<string>();
        var endpointNames = new HashSet<string>();
        bool changed;
        do
        {
            changed = false;
            foreach (var (_, type) in types)
            {
                if (endpointKeys.Contains(Key(type)) || type.BaseList is null)
                    continue;

                if (type.BaseList.Types.Any(b => RightmostName(b.Type) is { } n && (IsEndpointBaseName(n) || endpointNames.Contains(n))))
                {
                    endpointKeys.Add(Key(type));
                    endpointNames.Add(type.Identifier.Text);
                    changed = true;
                }
            }
        } while (changed);

        return cached = new ScanResult(roots, types, endpointKeys);
    }

    /// <summary>
    /// Build output, test projects (whose inline <c>MapGet</c> fixtures are PRD §3 non-goals) and the
    /// source generators (whose string templates are not code) are not scanned.
    /// </summary>
    private static bool IsExcluded(string relative)
    {
        var segments = relative.Split('/');
        return segments.Any(s => s is "bin" or "obj" or "node_modules"
            || s.EndsWith(".Tests", StringComparison.Ordinal)
            || s.EndsWith("Generators", StringComparison.Ordinal));
    }

    private static bool IsStatic(TypeDeclarationSyntax type) => type.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static string Key(TypeDeclarationSyntax type)
    {
        var names = type.AncestorsAndSelf().Select(n => n switch
        {
            TypeDeclarationSyntax t => $"{t.Identifier.Text}`{t.Arity}",
            BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
            _ => null,
        }).Where(n => n is not null).Reverse();

        return string.Join(".", names);
    }

    private static Finding MakeFinding(Rule rule, string file, TypeDeclarationSyntax type, SyntaxNode node)
    {
        var member = node.AncestorsAndSelf().Select(n => n switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            FieldDeclarationSyntax f => f.Declaration.Variables.First().Identifier.Text,
            _ => null,
        }).FirstOrDefault(n => n is not null) ?? "<type>";

        return new Finding(rule, file, Line(node), type.Identifier.Text, member, Snippet(node));
    }

    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        SimpleNameSyntax sn => sn.Identifier.Text,
        _ => null,
    };

    private static string? RightmostName(SyntaxNode node) => node switch
    {
        SimpleNameSyntax sn => sn.Identifier.Text,
        QualifiedNameSyntax qn => qn.Right.Identifier.Text,
        AliasQualifiedNameSyntax an => an.Name.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        NullableTypeSyntax nt => RightmostName(nt.ElementType),
        _ => null,
    };

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Snippet(SyntaxNode node)
    {
        var text = node.ToString().ReplaceLineEndings(" ");
        return text.Length > 100 ? text[..100] + "…" : text;
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution. A wrong answer makes the
    /// test vacuous rather than red, which is why the scan asserts it found endpoint classes.
    /// </summary>
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
