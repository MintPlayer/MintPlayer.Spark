using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>SPARK065: a typed id passed to a session's entity parameter compiles and fails at runtime (#536 D23).</summary>
public class IdAsEntityAnalyzerTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source)
        => GeneratorHarness.RunAnalyzerAsync("IdAsEntityAnalyzer", [source]);

    private const string Preamble = """
        using System.Threading.Tasks;
        using MintPlayer.Spark.Abstractions;

        namespace MintPlayer.Spark.Abstractions
        {
            public readonly struct Id<T> { public string Value => ""; }
        }

        namespace TestApp
        {
            public class Setting { public Id<Setting> Id { get; set; } public Id<Setting>? ParentId { get; set; } }

            // RavenDB's session shape: an entity overload that takes anything, and the id overload.
            public interface ISession
            {
                void Delete<T>(T entity);
                void Delete(string id);
                Task StoreAsync(object entity, string id);
            }
        """;

    [Fact]
    public async Task An_id_passed_to_Delete_is_an_error()
    {
        var diagnostics = await RunAsync(Preamble + """

                public class Use
                {
                    public void Run(ISession session, Setting setting) => session.Delete(setting.Id);
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK065").Should().ContainSingle()
            .Which.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_nullable_id_passed_as_an_object_entity_is_an_error()
    {
        var diagnostics = await RunAsync(Preamble + """

                public class Use
                {
                    public Task Run(ISession session, Setting setting) => session.StoreAsync(setting.ParentId, "settings/a");
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK065").Should().ContainSingle();
    }

    [Fact]
    public async Task The_id_text_and_the_entity_itself_are_clean()
    {
        var diagnostics = await RunAsync(Preamble + """

                public class Use
                {
                    public void Run(ISession session, Setting setting)
                    {
                        session.Delete(setting.Id.Value);
                        session.Delete(setting);
                    }
                }
            }
            """);

        diagnostics.Where(d => d.Id == "SPARK065").Should().BeEmpty();
    }
}
