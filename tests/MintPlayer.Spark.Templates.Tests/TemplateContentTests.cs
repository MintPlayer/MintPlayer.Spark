using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Templates.Tests;

/// <summary>
/// The hand-written template content against the manifest (plan S6: "generate data, guard code"). Program.cs,
/// appsettings.json and the ClientApp are not generated, so these tests are what keeps them in step with features.json.
/// </summary>
public partial class TemplateContentTests
{
    private static readonly FeatureManifest Manifest = TemplateRepository.Manifest();
    private static readonly TemplateFiles Generated = SparkTemplateGenerator.Generate(Manifest, TemplateRepository.HandWritten());

    /// <summary>
    /// Every project the template ships is in the .slnx the CLI gets and in primaryOutputs in both layouts: Visual Studio
    /// adds only the primary outputs to the solution it writes itself (plan S1, test 3).
    /// </summary>
    [Fact]
    public void Every_template_project_is_in_the_solution_and_the_primary_outputs()
    {
        var projects = Directory.EnumerateFiles(TemplateRepository.Template, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(TemplateRepository.Template, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "node_modules"))
            .ToList();
        var solution = XDocument.Parse(TemplateRepository.Read("SparkApp.slnx")).Descendants("Project")
            .Select(p => p.Attribute("Path")!.Value)
            .ToList();
        var outputs = JsonNode.Parse(TemplateRepository.Read(".template.config/template.json"))!["primaryOutputs"]!.AsArray()
            .Select(o => o!["path"]!.GetValue<string>())
            .ToList();

        projects.Should().Contain("SparkApp.Tests/SparkApp.Tests.csproj");
        projects.Where(p => !solution.Contains(p)).Should().BeEmpty("the .slnx lists every project");
        projects.Where(p => !outputs.Contains(p) || !outputs.Contains("../" + p)).Should().BeEmpty("primaryOutputs lists every project in both layouts");
    }

    /// <summary>The test project is built on MintPlayer.Spark.Testing (SparkTestDriver), which the template pins (core).</summary>
    [Fact]
    public void The_tests_project_references_the_testing_package_and_the_host()
    {
        var csproj = XDocument.Parse(TemplateRepository.Read("SparkApp.Tests/SparkApp.Tests.csproj"));

        csproj.Descendants("PackageReference").Select(r => r.Attribute("Include")?.Value).Should().Contain("MintPlayer.Spark.Testing");
        csproj.Descendants("ProjectReference").Select(r => r.Attribute("Include")?.Value).Should().Contain(@"..\SparkApp\SparkApp.csproj");
        Manifest.Core.Select(p => Path.GetFileNameWithoutExtension(p.Project)).Should().Contain("MintPlayer.Spark.Testing");
    }

    [Fact]
    public void Program_registers_each_feature_under_its_condition()
    {
        var program = TemplateRepository.Read("SparkApp/Program.cs");
        var missing = Manifest.Features.Where(f => f.Registration is not null)
            .Where(f => !Blocks(program, f.Condition).Any(block => Regex.IsMatch(block, $@"spark\.{f.Registration}[(<]")))
            .Select(f => $"{f.Id}: spark.{f.Registration}(...) inside #if ({f.Condition})")
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void The_client_provides_each_features_ng_spark_providers_under_its_condition()
    {
        var config = TemplateRepository.Read("SparkApp/ClientApp/src/app/app.config.ts");
        var missing = Manifest.Features
            .SelectMany(f => f.Client.Select(provider => (f, provider)))
            .Where(x => !Blocks(config, x.f.Condition).Any(block => block.Contains($"{x.provider}(")))
            .Select(x => $"{x.f.Id}: {x.provider}() inside //#if ({x.f.Condition})")
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Appsettings_holds_each_features_section_under_its_condition()
    {
        var appsettings = TemplateRepository.Read("SparkApp/appsettings.json");
        var missing = Manifest.Features.Where(f => f.Appsettings is not null)
            .Where(f => !Blocks(appsettings, f.Condition).Any(block => block.Contains($"\"{f.Appsettings!.Split(':').Last()}\":")))
            .Select(f => $"{f.Id}: {f.Appsettings} inside //#if ({f.Condition})")
            .ToList();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Every_condition_in_the_template_names_a_defined_symbol()
    {
        var symbols = TemplateRepository.Symbols(Generated.TemplateJson).Select(s => s.Key).ToHashSet();
        var conditions = Directory.EnumerateFiles(TemplateRepository.Template, "*", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "node_modules" or "bin" or "obj" or ".angular" or "dist"))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".json" or ".ts" or ".html" or ".scss" or ".props")
            .SelectMany(path => File.ReadLines(path)
                .Select(line => Directive().Match(line))
                .Where(m => m.Success && m.Groups["kind"].Value is "if" or "elif")
                .Select(m => (file: Path.GetRelativePath(TemplateRepository.Template, path), condition: m.Groups["condition"].Value)))
            .Concat(JsonConditions(Generated.TemplateJson).Select(c => (file: "template.json", condition: c)));

        var unknown = conditions
            .SelectMany(c => Identifiers(c.condition).Select(name => (c.file, name)))
            .Where(x => x.name is not ("true" or "false") && !symbols.Contains(x.name))
            .Select(x => $"{x.file}: {x.name}")
            .Distinct()
            .ToList();

        unknown.Should().BeEmpty("a condition on an undefined symbol is silently false");
    }

    /// <summary>The text of every branch opened by <c>#if (condition)</c> or <c>#elif (condition)</c>, nested blocks included.</summary>
    private static IEnumerable<string> Blocks(string text, string condition)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var open = Directive().Match(lines[i]);
            if (!open.Success || open.Groups["kind"].Value is not ("if" or "elif") || open.Groups["condition"].Value != $"({condition})")
                continue;

            var depth = 0;
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var directive = Directive().Match(lines[j]);
                var kind = directive.Success ? directive.Groups["kind"].Value : "";
                if (kind == "if") depth++;
                else if (kind == "endif" && depth > 0) depth--;
                else if (depth == 0 && kind is "elif" or "else" or "endif") break;
                body.Add(lines[j]);
            }
            yield return string.Join('\n', body);
        }
    }

    private static IEnumerable<string> JsonConditions(string templateJson)
    {
        var stack = new Stack<JsonNode?>([JsonNode.Parse(templateJson)]);
        while (stack.TryPop(out var node))
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key is "condition" or "isEnabled" or "isRequired" or "value" && value is JsonValue v && v.TryGetValue<string>(out var s)
                            && (key != "value" || obj["type"]?.GetValue<string>() == "computed"))
                            yield return s;
                        stack.Push(value);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) stack.Push(item);
                    break;
            }
        }
    }

    [GeneratedRegex(@"^\s*(?://|<!--)?#(?<kind>if|elif|else|endif)\b\s*(?<condition>.*?)\s*(?:-->)?\s*$")]
    private static partial Regex Directive();

    /// <summary>Symbol names in a condition, once its quoted strings (choice values) are removed.</summary>
    private static IEnumerable<string> Identifiers(string condition)
        => IdentifierPattern().Matches(QuotedPattern().Replace(condition, "")).Select(m => m.Value);

    [GeneratedRegex(@"""[^""]*""")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex IdentifierPattern();
}
