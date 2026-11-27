using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A model's <c>clrType</c> resolves whatever order the app's assemblies load in (#536 M9b). SqlDemo refused to start:
/// <c>Author</c> was looked up before anything had loaded <c>SqlDemo.Library</c>. The resolver's type scan loaded it
/// (a view in the app assembly derives from a type over <c>Book</c>), but searched only the assemblies loaded before the
/// scan, and cached the miss. The startup model then had no <c>Authors</c> table while synchronize's model had one.
/// </summary>
public class SparkTypeResolverLoadOrderTests
{
    [Fact]
    public void A_type_in_an_assembly_the_scan_itself_loads_is_found()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var libraryName = "LoadOrderLibrary" + suffix;
        var library = Compile(libraryName, $"namespace LoadOrder{suffix} {{ public class Base {{ }} public class Target {{ }} }}");
        var app = Compile("LoadOrderApp" + suffix, $"namespace LoadOrderApp{suffix} {{ public class Derived : LoadOrder{suffix}.Base {{ }} }}",
            MetadataReference.CreateFromImage(library));

        Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
            => name.Name == libraryName ? context.LoadFromStream(new MemoryStream(library)) : null;
        AssemblyLoadContext.Default.Resolving += OnResolving;
        try
        {
            AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(app));
            AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(a => a.GetName().Name == libraryName,
                "because only the app assembly is loaded: the library loads when the scan reads Derived's base type");

            SparkTypeResolver.ResolveClrType($"LoadOrder{suffix}.Target").Should().NotBeNull(
                "because the scan loaded the library that declares Target");
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= OnResolving;
        }
    }

    private static byte[] Compile(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
             .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var dll = new MemoryStream();
        var emitted = compilation.Emit(dll);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return dll.ToArray();
    }
}
