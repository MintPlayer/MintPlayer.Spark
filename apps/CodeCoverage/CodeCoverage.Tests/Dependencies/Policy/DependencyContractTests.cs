using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.Dependencies;
using CodeCoverage.Git.Dependencies;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// The contract test, bot side (PRD §13.11.2): every <c>fixtures/valid</c> document round-trips through the C# DTOs and still
/// validates; every <c>fixtures/invalid</c> one is refused before it is deserialized; the plans the bot writes validate.
/// </summary>
public class DependencyContractTests
{
    private static string Directory(string kind) => Path.Combine(DependencyTestKit.ContractsDirectory(), "fixtures", kind);

    public static TheoryData<string> ValidFixtures() => Fixtures("valid");

    public static TheoryData<string> InvalidFixtures() => Fixtures("invalid");

    private static TheoryData<string> Fixtures(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var file in System.IO.Directory.GetFiles(Directory(kind), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    /// <summary>The file-name prefix names the schema (fixtures/SOURCE.md).</summary>
    private static EDependencyContractSchema SchemaOf(string fileName) => fileName.Split('.')[0] switch
    {
        "lookup" => EDependencyContractSchema.Lookup,
        "plan" => EDependencyContractSchema.Plan,
        "result" => EDependencyContractSchema.ApplyResult,
        "platform" => EDependencyContractSchema.Platform,
        "migrate-result" => EDependencyContractSchema.MigrateResult,
        var other => throw new InvalidOperationException($"'{fileName}' has no schema prefix ({other})."),
    };

    [Fact]
    public void The_sweep_finds_both_kinds_of_fixture()
    {
        ValidFixtures().Count.Should().BeGreaterThanOrEqualTo(11, "every schema has at least one real valid document");
        InvalidFixtures().Count.Should().BeGreaterThanOrEqualTo(20, "each schema has targeted mutations");
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void A_valid_fixture_validates_and_round_trips_through_the_DTOs(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(Directory("valid"), fileName));
        var schema = SchemaOf(fileName);
        DependencyContractReader.Validate(schema, json).Should().BeEmpty();

        var written = schema switch
        {
            EDependencyContractSchema.Lookup => RoundTrip(DependencyContractReader.ReadLookup(json)),
            EDependencyContractSchema.Plan => RoundTrip(DependencyContractReader.ReadPlan(json)),
            EDependencyContractSchema.ApplyResult => RoundTrip(DependencyContractReader.ReadApplyResult(json)),
            EDependencyContractSchema.Platform => RoundTrip(DependencyContractReader.ReadPlatform(json)),
            _ => RoundTrip(DependencyContractReader.ReadMigrateResult(json)),
        };

        DependencyContractReader.Validate(schema, written).Should().BeEmpty("the DTOs write a document the schema still accepts");
        // A property the schema leaves optional is omitted when null, so the comparison treats null and absent alike; the
        // validation above is what proves no REQUIRED property was dropped.
        JsonNode.DeepEquals(WithoutNulls(JsonNode.Parse(json)), WithoutNulls(JsonNode.Parse(written))).Should()
            .BeTrue($"{fileName} must survive the DTOs unchanged");
    }

    private static string RoundTrip<T>(DependencyContractRead<T> read) where T : class
    {
        read.Errors.Should().BeEmpty();
        return DependencyContract.Serialize(read.Document!);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void An_invalid_fixture_is_refused_before_it_is_read(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(Directory("invalid"), fileName));
        var read = DependencyContractReader.Read<object>(SchemaOf(fileName), json);

        read.IsValid.Should().BeFalse();
        read.Document.Should().BeNull();
        read.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_lookup_whose_platform_record_breaks_the_referenced_schema_is_refused()
    {
        // record.schema.json reaches platform-record.schema.json through a $ref: the reference must resolve and bite.
        var lookup = JsonNode.Parse(DependencyTestKit.ContractFixture("valid", "lookup.docker-actions-platform.json"))!;
        lookup["platforms"]![0]!["sdk"] = "rust";

        DependencyContractReader.ReadLookup(lookup.ToJsonString()).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Not_json_and_an_oversized_document_are_errors_never_exceptions()
    {
        DependencyContractReader.ReadLookup("{ not json").Errors.Should().ContainSingle();
        DependencyContractReader.ReadLookup(new string(' ', DependencyContractReader.MaxBytes + 1)).Errors.Should().ContainSingle();
    }

    [Fact]
    public void The_embedded_schemas_are_the_shared_files_on_disk()
    {
        foreach (var schema in Enum.GetValues<EDependencyContractSchema>())
        {
            var onDisk = File.ReadAllText(Path.Combine(DependencyTestKit.ContractsDirectory(), DependencyContractReader.FileName(schema)));
            DependencyContractReader.SchemaText(schema).Should().Be(onDisk);
        }
    }

    [Fact]
    public void The_bot_refuses_to_write_a_plan_the_runner_would_refuse()
    {
        var plan = new DependencyPlan
        {
            PlanId = "has spaces",
            ExpectedSha = DependencyTestKit.Sha,
            Groups = [new PlanGroup { Group = "x", Items = [new PlanItem { DepName = "x", Datasource = "npm", Manager = "npm", PackageFile = "../package.json", CurrentValue = "1", NewValue = "2", NewVersion = "2", UpdateType = "major" }] }],
        };

        var act = () => DependencyContractReader.WritePlan(plan);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_plan_written_by_the_bot_reads_back_identically()
    {
        var fixture = DependencyContractReader.ReadPlan(DependencyTestKit.ContractFixture("valid", "plan.npm-three-groups.json")).Document!;

        var written = DependencyContractReader.WritePlan(fixture);

        DependencyContractReader.ReadPlan(written).Document!.Groups.Select(g => g.Group).Should().Equal(["express", "react-dom", "chalk"]);
    }

    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                    obj.Remove(key);
                foreach (var (_, value) in obj)
                    WithoutNulls(value);
                return obj;
            case JsonArray array:
                foreach (var item in array)
                    WithoutNulls(item);
                return array;
            default:
                return node;
        }
    }
}
