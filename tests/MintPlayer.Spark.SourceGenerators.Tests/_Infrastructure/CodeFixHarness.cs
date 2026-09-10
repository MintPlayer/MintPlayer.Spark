using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

/// <summary>
/// One project of a code-fix fixture. <paramref name="Sources"/> are keyed by file name so a test
/// can assert on the file it expects to change.
/// </summary>
internal sealed record FixtureProject(string Name, IReadOnlyDictionary<string, string> Sources)
{
    public static FixtureProject Of(string name, params (string FileName, string Source)[] sources)
        => new(name, sources.ToDictionary(s => s.FileName, s => s.Source, StringComparer.Ordinal));
}

/// <summary>The state of every document in the workspace after a fix was applied.</summary>
internal sealed record CodeFixResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> Documents,
    IReadOnlyList<string> OfferedTitles)
{
    /// <summary>The text of one document, by file name, with line endings normalised to \n.</summary>
    public string Document(string fileName) => Documents.TryGetValue(fileName, out var text)
        ? text.Replace("\r\n", "\n")
        : throw new InvalidOperationException(
            $"No document named '{fileName}'. Have: {string.Join(", ", Documents.Keys)}");
}

/// <summary>
/// Runs a <see cref="CodeFixProvider"/> against a real <c>AdhocWorkspace</c> and returns the
/// resulting documents.
/// </summary>
/// <remarks>
/// ⚠️ <b>Multi-project on purpose.</b> The scenario that matters for SPARK017 is a type declared in
/// an entity library and reported from the application's compilation, so a single-project harness
/// cannot express it at all — the equivalent harness in MintPlayer.Dotnet.Tools builds one project
/// and one document, and consequently none of its code-fix tests covers the case its fix exists for.
/// Projects here are listed in dependency order and each references all the ones before it;
/// diagnostics are produced from the <b>last</b> project's compilation, which is the application.
/// <para>
/// ⚠️ Every document gets a real <c>filePath</c>. Roslyn matches a diagnostic's
/// <c>Location.SourceTree</c> to a document, and a workspace built without file paths silently
/// matches nothing — which in the neighbouring repo left a fix body unreachable while its tests went
/// on passing. <see cref="ApplyAsync"/> therefore also asserts the fix actually changed something.
/// </para>
/// </remarks>
internal static class CodeFixHarness
{
    /// <summary>
    /// Produces diagnostics with <paramref name="analyzerTypeName"/>, then applies
    /// <paramref name="codeFixTypeName"/> to the first one matching <paramref name="diagnosticId"/>.
    /// </summary>
    public static Task<CodeFixResult> RunAnalyzerFixAsync(
        string analyzerTypeName,
        string codeFixTypeName,
        string diagnosticId,
        IEnumerable<FixtureProject> projects,
        IEnumerable<Type>? referenceTypes = null,
        string? analyzerAssemblyName = null,
        string? codeFixAssemblyName = null)
        => RunAsync(
            compilation =>
            {
                var analyzer = (DiagnosticAnalyzer)GeneratorHarness.InstantiateComponent(
                    analyzerTypeName, analyzerAssemblyName, typeof(DiagnosticAnalyzer));

                return compilation
                    .WithAnalyzers(ImmutableArray.Create(analyzer))
                    .GetAnalyzerDiagnosticsAsync(default);
            },
            codeFixTypeName, diagnosticId, projects, referenceTypes, codeFixAssemblyName);

    /// <summary>
    /// The same, for a diagnostic emitted by a <b>generator</b> rather than an analyzer — which is
    /// what SPARK016 is: <c>ValueObjectKeyReporter</c> runs inside <c>ValueObjectKeyGenerator</c>'s
    /// pipeline, not as a <see cref="DiagnosticAnalyzer"/>.
    /// </summary>
    public static Task<CodeFixResult> RunGeneratorFixAsync(
        string generatorTypeName,
        string codeFixTypeName,
        string diagnosticId,
        IEnumerable<FixtureProject> projects,
        IEnumerable<Type>? referenceTypes = null,
        string? generatorAssemblyName = null,
        string? codeFixAssemblyName = null)
        => RunAsync(
            compilation =>
            {
                var generator = (IIncrementalGenerator)GeneratorHarness.InstantiateComponent(
                    generatorTypeName, generatorAssemblyName, typeof(IIncrementalGenerator));

                CSharpGeneratorDriver
                    .Create(
                        generators: [generator.AsSourceGenerator()],
                        parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options)
                    .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

                return Task.FromResult(diagnostics);
            },
            codeFixTypeName, diagnosticId, projects, referenceTypes, codeFixAssemblyName);

    private static async Task<CodeFixResult> RunAsync(
        Func<Compilation, Task<ImmutableArray<Diagnostic>>> produceDiagnostics,
        string codeFixTypeName,
        string diagnosticId,
        IEnumerable<FixtureProject> projects,
        IEnumerable<Type>? referenceTypes,
        string? codeFixAssemblyName)
    {
        using var workspace = new AdhocWorkspace();
        var solution = BuildSolution(workspace, projects.ToList(), referenceTypes ?? []);

        // The last project is the application: it references every library before it, and it is the
        // compilation SPARK017's context-rooted walk would run in.
        var analysisProject = solution.Projects.Last();
        var compilation = await analysisProject.GetCompilationAsync()
            ?? throw new InvalidOperationException($"No compilation for project '{analysisProject.Name}'.");

        // ⚠️ A fixture that does not compile produces zero diagnostics from the analyzer, and every
        // "the fix did not add a duplicate" assertion then passes for entirely the wrong reason.
        // Measured: a two-project fixture whose ProjectReference was not wired reported nothing at
        // all, and three of eight tests went green on an empty result. Fail loudly instead.
        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (compileErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Fixture project '{analysisProject.Name}' does not compile:{Environment.NewLine}" +
                string.Join(Environment.NewLine, compileErrors.Select(d => d.ToString())));
        }

        var produced = await produceDiagnostics(compilation);
        var matching = produced.Where(d => d.Id == diagnosticId).ToList();

        var target = matching.FirstOrDefault();
        if (target is null)
            return new CodeFixResult(matching, await ReadDocumentsAsync(solution), []);

        var (fixedSolution, titles) = await ApplyAsync(solution, target, codeFixTypeName, codeFixAssemblyName);
        return new CodeFixResult(matching, await ReadDocumentsAsync(fixedSolution), titles);
    }

    private static Solution BuildSolution(
        AdhocWorkspace workspace, IReadOnlyList<FixtureProject> projects, IEnumerable<Type> referenceTypes)
    {
        var references = GeneratorHarness.BuildReferences(referenceTypes).ToList();
        var solution = workspace.CurrentSolution;
        var previous = new List<ProjectId>();

        foreach (var fixture in projects)
        {
            var projectId = ProjectId.CreateNewId(fixture.Name);

            solution = solution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    name: fixture.Name,
                    assemblyName: fixture.Name,
                    language: LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: references,
                    // Each project references the ones declared before it, so a two-project fixture
                    // is library-then-application. In a loaded solution this is what becomes a
                    // CompilationReference and keeps the library's source locations alive — the very
                    // thing a `dotnet build` metadata reference loses.
                    projectReferences: previous.Select(p => new ProjectReference(p))));

            foreach (var (fileName, source) in fixture.Sources)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId, fileName),
                    fileName,
                    SourceText.From(source),
                    filePath: $"/{fixture.Name}/{fileName}");
            }

            previous.Add(projectId);
        }

        return solution;
    }

    private static async Task<(Solution Solution, IReadOnlyList<string> Titles)> ApplyAsync(
        Solution solution, Diagnostic diagnostic, string codeFixTypeName, string? codeFixAssemblyName)
    {
        var document = FindDocument(solution, diagnostic)
            ?? throw new InvalidOperationException(
                $"No document in the solution owns the location of {diagnostic.Id}. " +
                "A workspace whose documents have no filePath is the usual cause.");

        var provider = (CodeFixProvider)GeneratorHarness.InstantiateComponent(
            codeFixTypeName, codeFixAssemblyName, typeof(CodeFixProvider));

        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(
            document, diagnostic, (action, _) => actions.Add(action), default));

        var titles = actions.Select(a => a.Title).ToList();
        if (actions.Count == 0)
            return (solution, titles);

        var operations = await actions[0].GetOperationsAsync(default);
        var applied = operations
            .OfType<ApplyChangesOperation>()
            .Select(op => op.ChangedSolution)
            .LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Code fix '{titles[0]}' produced no ApplyChangesOperation.");

        // The trap this guards: a fix whose document lookup silently matched nothing still returns a
        // perfectly valid, entirely unchanged solution.
        if (!applied.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()).Any())
            throw new InvalidOperationException($"Code fix '{titles[0]}' changed no document.");

        return (applied, titles);
    }

    /// <summary>
    /// The document whose syntax tree the diagnostic points into — in <b>any</b> project, because a
    /// diagnostic raised in the application can name a file owned by a library.
    /// </summary>
    private static Document? FindDocument(Solution solution, Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree is null)
            return null;

        return solution.Projects
            .Select(project => project.GetDocument(tree))
            .FirstOrDefault(d => d is not null);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadDocumentsAsync(Solution solution)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var text = await document.GetTextAsync();
            result[document.Name] = text.ToString();
        }

        return result;
    }
}
