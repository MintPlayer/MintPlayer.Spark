using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class InterceptorRegistrationProducer : Producer
{
    private readonly IEnumerable<InterceptorClassInfo> interceptorClasses;
    private readonly bool knowsInterceptors;

    public InterceptorRegistrationProducer(
        IEnumerable<InterceptorClassInfo> interceptorClasses,
        bool knowsInterceptors,
        string rootNamespace)
        : base(rootNamespace, "SparkInterceptorRegistrations.g.cs")
    {
        this.interceptorClasses = interceptorClasses;
        this.knowsInterceptors = knowsInterceptors;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var interceptorList = interceptorClasses
            .GroupBy(h => h.InterceptorTypeName)
            .Select(g => g.First())
            .OrderBy(h => h.InterceptorTypeName, StringComparer.Ordinal)
            .ToList();

        if (!knowsInterceptors || interceptorList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkInterceptorsBuilderExtensions"))
            {
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddInterceptors(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    foreach (var interceptorClass in interceptorList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderInterceptorExtensions.AddInterceptor<{interceptorClass.InterceptorTypeName}>(builder);");
                    }
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
