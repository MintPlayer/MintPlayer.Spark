using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MintPlayer.Spark.SourceGenerators.Tests._Spikes;

/// <summary>
/// SPIKE for docs/lane_attributes_PRD.md. The design binds a message to its lane with a GENERIC
/// attribute — <c>[Lane&lt;CoverageParseLane&gt;]</c> — so that the binding is a type reference
/// rather than a repeated string. Everything else in the design rests on that being discoverable,
/// so it is checked before anything is built on it.
/// </summary>
/// <remarks>
/// Two questions, neither safe to assume:
/// <list type="number">
/// <item>What metadata name does <c>ForAttributeWithMetadataName</c> want for a generic attribute?
/// The mangled arity form (<c>LaneAttribute`1</c>) is the candidate; the unmangled one is what a
/// person would guess, and guessing wrong yields a generator that silently never fires.</item>
/// <item>Can the type argument be recovered from the attribute application? Without it the
/// attribute carries no binding at all.</item>
/// </list>
/// </remarks>
public class GenericAttributeDiscoverySpike
{
    private const string Source = """
        using System;

        namespace Spike;

        [AttributeUsage(AttributeTargets.Class)]
        public sealed class LaneAttribute : Attribute
        {
            public bool Ordered { get; set; }
            public int MaxPartitionsInFlight { get; set; }
        }

        [AttributeUsage(AttributeTargets.Class)]
        public sealed class LaneAttribute<TLane> : Attribute { }

        [Lane(Ordered = true, MaxPartitionsInFlight = 2)]
        public record CoverageParseLane;

        [Lane<CoverageParseLane>]
        public record ParseSessionMessage { public string BuildId { get; init; } = ""; }

        [Lane<CoverageParseLane>]
        public record FinalizeBuildMessage { public string BuildId { get; init; } = ""; }
        """;

    [Fact]
    public void A_generic_attribute_is_found_by_its_mangled_metadata_name()
    {
        var found = RunCapture("Spike.LaneAttribute`1");

        // The arity-mangled name is what the pipeline matches on.
        found.Should().BeEquivalentTo(["Spike.ParseSessionMessage", "Spike.FinalizeBuildMessage"]);
    }

    [Fact]
    public void The_generic_and_non_generic_attributes_are_separate_metadata_names()
    {
        // The finding that shapes the generator: `Lane` and `Lane<T>` are two distinct types, so one
        // pipeline cannot serve both. The unmangled name matches ONLY the lane records that carry
        // policy; the mangled one matches ONLY the messages that bind to a lane. Two
        // ForAttributeWithMetadataName pipelines, joined afterwards.
        //
        // The trap this replaces is worth stating: had the design used one attribute name for both
        // roles, reaching for the name a person would guess would have matched the wrong set
        // silently — no throw, no warning, just a generator that emits the wrong thing.
        RunCapture("Spike.LaneAttribute").Should().BeEquivalentTo(["Spike.CoverageParseLane"]);
    }

    [Fact]
    public void The_lane_type_argument_is_recoverable_from_the_attribute()
    {
        var bindings = RunCapture("Spike.LaneAttribute`1", captureLaneArgument: true);

        bindings.Should().AllSatisfy(b => b.Should().EndWith("->Spike.CoverageParseLane"));
    }

    [Fact]
    public void A_non_generic_attribute_on_the_lane_record_still_yields_its_named_arguments()
    {
        // The lane record carries policy as named arguments; confirm they survive to the generator.
        var lanes = RunCapture("Spike.LaneAttribute", captureNamedArguments: true);

        lanes.Should().ContainSingle()
            .Which.Should().Be("Spike.CoverageParseLane[Ordered=True,MaxPartitionsInFlight=2]");
    }

    /// <summary>Runs a throwaway generator that records what the pipeline handed it.</summary>
    private static List<string> RunCapture(
        string metadataName,
        bool captureLaneArgument = false,
        bool captureNamedArguments = false)
    {
        var captured = new List<string>();
        var generator = new CapturingGenerator(metadataName, captured, captureLaneArgument, captureNamedArguments);

        var compilation = CSharpCompilation.Create(
            "SpikeAssembly",
            [CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest))],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(generator.AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return captured;
    }

    private sealed class CapturingGenerator(
        string metadataName,
        List<string> captured,
        bool captureLaneArgument,
        bool captureNamedArguments) : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var provider = context.SyntaxProvider.ForAttributeWithMetadataName(
                metadataName,
                predicate: static (_, _) => true,
                transform: (ctx, _) => Describe(ctx));

            context.RegisterSourceOutput(provider.Collect(), (_, items) =>
            {
                foreach (var item in items)
                    if (item is not null)
                        captured.Add(item);
            });
        }

        private string? Describe(GeneratorAttributeSyntaxContext ctx)
        {
            var target = ctx.TargetSymbol.ToDisplayString();
            var attribute = ctx.Attributes[0];

            if (captureLaneArgument)
            {
                var laneType = attribute.AttributeClass?.TypeArguments.FirstOrDefault();
                return laneType is null ? null : $"{target}->{laneType.ToDisplayString()}";
            }

            if (captureNamedArguments)
            {
                var named = string.Join(",", attribute.NamedArguments.Select(a => $"{a.Key}={a.Value.Value}"));
                return $"{target}[{named}]";
            }

            return target;
        }
    }
}
