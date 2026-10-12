using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK050: an MCP tool runs as the user who connected the agent, so a raw RavenDB session, the store, or
/// <c>IDatabaseAccess.…UncheckedAsync</c> inside an <c>[McpServerToolType]</c> class bypasses every right
/// (MCP PRD §4.5).
/// </summary>
public class McpToolDataAccessAnalyzerTests
{
    private const string AnalyzerName = "McpToolDataAccessAnalyzer";

    private static readonly Type[] Refs = [typeof(IAsyncDocumentSession), typeof(IDocumentStore), typeof(IDatabaseAccess)];

    /// <summary>The SDK's attribute, declared in source: the analyzer resolves it by metadata name.</summary>
    private const string ToolTypeAttribute = """
        namespace ModelContextProtocol.Server
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class McpServerToolTypeAttribute : System.Attribute { }
        }
        """;

    private static string Tool(string members, bool isTool = true) => $$"""
        using System.Threading.Tasks;
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents;
        using Raven.Client.Documents.Session;

        namespace TestApp;

        public class Contract { public string? Id { get; set; } }

        {{(isTool ? "[ModelContextProtocol.Server.McpServerToolType]" : "")}}
        public sealed class ContractTools
        {
            {{members}}
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source, bool withSdk = true)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, withSdk ? [ToolTypeAttribute, source] : [source], Refs);

    [Theory]
    [InlineData("private readonly IAsyncDocumentSession session = null!;", "'IAsyncDocumentSession' field 'session'")]
    [InlineData("private IDocumentStore Store { get; } = null!;", "'IDocumentStore' property 'Store'")]
    [InlineData("public ContractTools(IAsyncDocumentSession session) { }", "'IAsyncDocumentSession' parameter 'session'")]
    [InlineData("public Task<Contract> Load(IAsyncDocumentSession session) => session.LoadAsync<Contract>(\"contracts/1\");", "'IAsyncDocumentSession' parameter 'session'")]
    public async Task A_raw_session_or_store_member_is_flagged(string member, string expected)
    {
        var diagnostics = await RunAsync(Tool(member));

        diagnostics.Where(d => d.Id == "SPARK050").Select(d => d.GetMessage())
            .Should().Contain(m => m.StartsWith(expected), "a raw session enforces no rights for the user the agent acts for");
    }

    [Fact]
    public async Task A_call_into_a_raw_session_is_flagged_at_the_call()
    {
        var diagnostics = await RunAsync(Tool("""
            public Task<Contract> Load(IDocumentStore store)
            {
                using var session = store.OpenAsyncSession();
                return session.LoadAsync<Contract>("contracts/1");
            }
            """));

        diagnostics.Where(d => d.Id == "SPARK050").Select(d => d.GetMessage())
            .Should().Contain(m => m.StartsWith("'IDocumentStore.OpenAsyncSession'"))
            .And.Contain(m => m.StartsWith("'IAsyncDocumentSession.LoadAsync'"));
    }

    [Theory]
    [InlineData("GetDocumentUncheckedAsync<Contract>(\"contracts/1\")", "'IDatabaseAccess.GetDocumentUncheckedAsync'")]
    [InlineData("GetDocumentsUncheckedAsync<Contract>()", "'IDatabaseAccess.GetDocumentsUncheckedAsync'")]
    public async Task An_unchecked_IDatabaseAccess_call_is_flagged(string call, string expected)
    {
        var diagnostics = await RunAsync(Tool($$"""
            private readonly IDatabaseAccess data = null!;
            public Task Run() => data.{{call}};
            """));

        diagnostics.Where(d => d.Id == "SPARK050").Should().ContainSingle()
            .Which.GetMessage().Should().StartWith(expected).And.Contain("'ContractTools'");
    }

    [Fact]
    public async Task Checked_IDatabaseAccess_calls_are_not_flagged()
    {
        var diagnostics = await RunAsync(Tool("""
            private readonly IDatabaseAccess data = null!;
            public Task Run() => data.GetPersistentObjectAsync(System.Guid.Empty, "contracts/1");
            """));

        diagnostics.Where(d => d.Id == "SPARK050").Should().BeEmpty("the checked methods enforce the caller's rights and row security");
    }

    [Fact]
    public async Task A_class_that_is_not_an_MCP_tool_type_is_not_flagged()
    {
        var diagnostics = await RunAsync(Tool("private readonly IAsyncDocumentSession session = null!; public Task<Contract> Load() => session.LoadAsync<Contract>(\"x\");", isTool: false));

        diagnostics.Where(d => d.Id == "SPARK050").Should().BeEmpty("jobs and actions classes use sessions legitimately");
    }

    [Fact]
    public async Task A_compilation_without_the_MCP_SDK_is_not_flagged()
    {
        var source = Tool("private readonly IAsyncDocumentSession session = null!;", isTool: false);

        var diagnostics = await RunAsync(source, withSdk: false);

        diagnostics.Where(d => d.Id == "SPARK050").Should().BeEmpty();
    }
}
