using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

/// <summary>
/// Runs a single source generator against an in-memory compilation and hands back the
/// files it produced. The generator assembly is loaded via <see cref="Assembly.LoadFrom"/>
/// from the Generators/ sibling directory (see csproj CopyGeneratorToOutput target) so we
/// don't bring the generator's netstandard2.0 dependency polyfills into this net10.0
/// process's compile-time type graph.
/// </summary>
internal static class GeneratorHarness
{
    private const string DefaultAssemblyName = "MintPlayer.Spark.SourceGenerators";
    private static readonly Dictionary<string, Assembly> _loadedAssemblies = new(StringComparer.Ordinal);
    private static readonly Lazy<Assembly> _generatorAssembly = new(() => LoadGeneratorAssembly(DefaultAssemblyName));

    /// <summary>
    /// Runs the generator whose concrete type has the given name (e.g. <c>ActionsRegistrationGenerator</c>)
    /// against a compilation built from <paramref name="sources"/> plus any type references in
    /// <paramref name="referenceTypes"/>. Returns (diagnostics, generatedSources).
    /// </summary>
    public static GeneratorRunResult Run(
        string generatorTypeName,
        IEnumerable<string> sources,
        IEnumerable<Type>? referenceTypes = null,
        string? rootNamespace = null,
        IEnumerable<(string Path, string Text)>? additionalTexts = null,
        string? generatorAssemblyName = null)
    {
        var generator = InstantiateGenerator(generatorTypeName, generatorAssemblyName);

        // Always use at least one source so Roslyn can parse + produce a valid compilation.
        var sourceList = sources.ToList();
        if (sourceList.Count == 0)
            sourceList.Add("// intentionally empty");

        var compilation = BuildCompilation(sourceList, referenceTypes ?? Array.Empty<Type>());

        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;

        // Surface RootNamespace via analyzer config so the generator can read Settings.
        var optionsProvider = new StubAnalyzerConfigOptionsProvider(rootNamespace);

        var additionalTextList = additionalTexts?
            .Select(t => (AdditionalText)new InMemoryAdditionalText(t.Path, t.Text))
            .ToArray();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            additionalTexts: additionalTextList,
            parseOptions: parseOptions,
            optionsProvider: optionsProvider);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

        var result = driver.GetRunResult();
        var generated = result.GeneratedTrees
            .Select(tree => (HintName: Path.GetFileName(tree.FilePath), Source: tree.GetText().ToString()))
            .OrderBy(x => x.HintName, StringComparer.Ordinal)
            .ToList();

        return new GeneratorRunResult(diagnostics, generated, updated.GetDiagnostics());
    }

    /// <summary>
    /// Runs a <see cref="DiagnosticAnalyzer"/> by type name against a compilation built from
    /// <paramref name="sources"/>. Returns the diagnostics the analyzer emitted (filtered to
    /// rules declared by the analyzer — ignores generic compile errors from test fixtures).
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> RunAnalyzerAsync(
        string analyzerTypeName,
        IEnumerable<string> sources,
        IEnumerable<Type>? referenceTypes = null)
    {
        var analyzer = InstantiateAnalyzer(analyzerTypeName);
        var compilation = BuildCompilation(sources, referenceTypes ?? Array.Empty<Type>());

        var withAnalyzer = compilation.WithAnalyzers(
            System.Collections.Immutable.ImmutableArray.Create(analyzer));
        var diagnostics = await withAnalyzer.GetAnalyzerDiagnosticsAsync(default);

        var analyzerIds = analyzer.SupportedDiagnostics.Select(d => d.Id).ToHashSet();
        return diagnostics.Where(d => analyzerIds.Contains(d.Id)).ToList();
    }

    private static Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer InstantiateAnalyzer(string typeName)
    {
        var asm = _generatorAssembly.Value;
        var type = asm.GetTypes()
            .FirstOrDefault(t => t.Name == typeName && typeof(Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer).IsAssignableFrom(t))
            ?? throw new InvalidOperationException($"Analyzer type '{typeName}' not found in {asm.Location}.");
        return (Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer)Activator.CreateInstance(type)!;
    }

    private static IIncrementalGenerator InstantiateGenerator(string typeName, string? assemblyName = null)
    {
        var asm = assemblyName is null ? _generatorAssembly.Value : GetOrLoadAssembly(assemblyName);
        var type = asm.GetTypes()
            .FirstOrDefault(t => t.Name == typeName && typeof(IIncrementalGenerator).IsAssignableFrom(t))
            ?? throw new InvalidOperationException(
                $"Generator type '{typeName}' not found in {asm.Location}. " +
                $"Candidates: {string.Join(", ", asm.GetTypes().Where(t => typeof(IIncrementalGenerator).IsAssignableFrom(t)).Select(t => t.Name))}");

        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    }

    private static Assembly GetOrLoadAssembly(string assemblyName)
    {
        lock (_loadedAssemblies)
        {
            if (_loadedAssemblies.TryGetValue(assemblyName, out var cached))
                return cached;
            var loaded = LoadGeneratorAssembly(assemblyName);
            _loadedAssemblies[assemblyName] = loaded;
            return loaded;
        }
    }

    private static Assembly LoadGeneratorAssembly(string assemblyName)
    {
        var testAsmDir = Path.GetDirectoryName(typeof(GeneratorHarness).Assembly.Location)!;
        var generatorsDir = Path.Combine(testAsmDir, "Generators");
        var generatorPath = Path.Combine(generatorsDir, assemblyName + ".dll");

        if (!File.Exists(generatorPath))
            throw new FileNotFoundException(
                $"Generator assembly not found at {generatorPath}. Build the solution first.", generatorPath);

        // Preload the generator's runtime dependency so IncrementalGenerator base class resolves.
        var toolsPath = Path.Combine(generatorsDir, "MintPlayer.SourceGenerators.Tools.dll");
        if (File.Exists(toolsPath))
        {
            Assembly.LoadFrom(toolsPath);
        }

        return Assembly.LoadFrom(generatorPath);
    }

    private static CSharpCompilation BuildCompilation(IEnumerable<string> sources, IEnumerable<Type> referenceTypes)
    {
        var syntaxTrees = sources.Select((src, i) =>
            CSharpSyntaxTree.ParseText(src, path: $"Source{i}.cs")).ToList();

        // Minimum BCL references so Roslyn can build + resolve symbols.
        var references = new HashSet<MetadataReference>(
            new[]
            {
                typeof(object).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(System.ComponentModel.AttributeProviderAttribute).Assembly,
                typeof(System.Linq.Enumerable).Assembly,
            }
            .Concat(AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Where(a => a.GetName().Name?.StartsWith("System.") == true
                         || a.GetName().Name == "netstandard"
                         || a.GetName().Name == "mscorlib"))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)));

        foreach (var t in referenceTypes)
            references.Add(MetadataReference.CreateFromFile(t.Assembly.Location));

        return CSharpCompilation.Create(
            assemblyName: "TestInput",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}

internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly Microsoft.CodeAnalysis.Text.SourceText _text;
    public InMemoryAdditionalText(string path, string text)
    {
        Path = path;
        _text = Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
    public override string Path { get; }
    public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => _text;
}

internal sealed record GeneratorRunResult(
    IEnumerable<Diagnostic> GeneratorDiagnostics,
    IReadOnlyList<(string HintName, string Source)> GeneratedSources,
    IEnumerable<Diagnostic> FinalCompilationDiagnostics);

/// <summary>
/// Minimal stub that lets us surface build_property.rootnamespace through AnalyzerConfigOptions.
/// </summary>
internal sealed class StubAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly StubOptions _options;
    public StubAnalyzerConfigOptionsProvider(string? rootNamespace) => _options = new StubOptions(rootNamespace);

    public override AnalyzerConfigOptions GlobalOptions => _options;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;

    private sealed class StubOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;
        public StubOptions(string? rootNamespace)
        {
            _values = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(rootNamespace))
                _values["build_property.rootnamespace"] = rootNamespace;
        }

        public override bool TryGetValue(string key, out string value)
            => _values.TryGetValue(key, out value!);
    }
}
