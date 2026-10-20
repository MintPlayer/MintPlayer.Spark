using System.Text.Json;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Contracts;
using MintPlayer.Spark.SchemaGenerator;
using MintPlayer.Spark.Tests.Contracts.Fixtures.Converted;
using MintPlayer.Spark.Tests.Contracts.Fixtures.Unclaimed;
using MintPlayer.Spark.Tests.Contracts.Fixtures.Wire;
using MintPlayer.Spark.Tests.Schemas;

namespace MintPlayer.Spark.Tests.Contracts;

/// <summary>
/// The C# → TypeScript contracts generator (commerce X20 item 2, <c>docs/guide-typescript-contracts.md</c>): the
/// TypeScript it writes states the wire as System.Text.Json writes it, it refuses what it cannot state, the
/// committed library files are what it generates, and the verify mode fails on a drifted file.
/// </summary>
public sealed class SparkContractGeneratorTests
{
    private static readonly System.Reflection.Assembly Fixtures = typeof(WireShape).Assembly;

    private static readonly System.Reflection.Assembly[] Libraries = [typeof(PersistentObject).Assembly, typeof(TranslatedString).Assembly];

    private static string GenerateNamespace(Type marker)
    {
        var modules = SparkContractGenerator.Generate([Fixtures], Libraries, "regenerate", t => t.Namespace == marker.Namespace);
        return modules.Single(m => m.Module == SparkClientModuleAttribute.Application).Content;
    }

    private static string Block(string content, string declaration)
    {
        var start = content.IndexOf(declaration + " ", StringComparison.Ordinal);
        (start >= 0).Should().BeTrue($"the file declares {declaration}:\n{content}");
        var end = content.IndexOf("\n}", start, StringComparison.Ordinal);
        return content[start..(end + 2)];
    }

    [Fact]
    public void States_the_wire_shape_System_Text_Json_writes()
    {
        var content = GenerateNamespace(typeof(WireShape));

        Block(content, "export interface WireShape").Should().Be(
            """
            export interface WireShape {
              name: string;
              nickname?: string | null;
              big: number;
              price: number;
              count?: number | null;
              id: string;
              at: string;
              custom_name: string;
              sometimes?: string;
              kind: NumberedKind;
              named: NamedKind;
              flags: string;
              kindByName: 'First' | 'Second';
              tags: (string | null)[];
              scores: Record<string, number>;
              child: WireChild;
              optionalChild?: WireChild | null;
              label: TranslatedString;
              object: PersistentObject;
              anything?: any;
            }
            """.ReplaceLineEndings("\n"));

        content.Should().Contain("export enum NumberedKind {\n  First = 1,\n  Second = 2,\n}");
        content.Should().Contain("export type NamedKind = 'alpha' | 'Beta';");
        content.Should().NotContain("FlaggedKind", "a [Flags] enum written by name is a plain string where it is used");
        content.Should().Contain("export interface WireChild {\n  value: string;\n}");
        content.Should().NotContain("hidden");
    }

    [Fact]
    public void Imports_library_types_and_hand_written_types_from_their_modules()
    {
        var content = GenerateNamespace(typeof(WireShape));

        content.Should().Contain("import type { PersistentObject, TranslatedString } from '@mintplayer/ng-spark/models';");
        content.Should().NotContain("export interface PersistentObject", "a library's type is the library module's to declare");
    }

    [Fact]
    public void Renames_and_inheritance_follow_the_attributes_and_the_class_hierarchy()
    {
        var content = GenerateNamespace(typeof(WireShape));

        content.Should().Contain("export interface RenamedForTheClient {\n  value: string;\n}");
        content.Should().Contain("export interface DerivedShape extends BaseShape {\n  derivedValue: number;\n}");
    }

    [Fact]
    public void The_member_names_are_the_ones_the_serializer_writes()
    {
        var content = GenerateNamespace(typeof(WireShape));
        var declared = Regex.Matches(Block(content, "export interface WireShape"), @"^  '?([A-Za-z_$][\w$]*)'?\??:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var instance = new WireShape(
            "n", null, long.MaxValue, 1.5m, null, Guid.NewGuid(), DateTimeOffset.UnixEpoch, "r", "s",
            NumberedKind.First, NamedKind.Alpha, FlaggedKind.A | FlaggedKind.B, NumberedKind.Second,
            ["a", null], new() { ["x"] = 1 }, new WireChild("c"), null, TranslatedString.Of("en", "Label"),
            new PersistentObject { Name = "Car", ObjectTypeId = Guid.Empty }, null);
        var written = JsonSerializer.SerializeToNode(instance, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject()
            .Select(p => p.Key)
            .ToHashSet();

        declared.Order(StringComparer.Ordinal).Should().BeEquivalentTo(written.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_contract_no_module_claims_is_an_error()
    {
        Action act = () => SparkContractGenerator.Generate([Fixtures], Libraries, "regenerate", t => t.Namespace == typeof(Orphan).Namespace);

        act.Should().Throw<SparkContractException>().Which.Message.Should().Contain(typeof(Orphan).FullName!);
    }

    [Fact]
    public void A_type_written_by_an_unvouched_converter_is_an_error()
    {
        Action act = () => SparkContractGenerator.Generate([Fixtures], Libraries, "regenerate", t => t.Namespace == typeof(HoldsConverted).Namespace);

        act.Should().Throw<SparkContractException>().Which.Message.Should().Contain(nameof(OpaqueConverter));
    }

    [Fact]
    public void Library_generation_is_deterministic()
    {
        var first = SparkLibraryContracts.Generate();
        var second = SparkLibraryContracts.Generate();

        second.Count.Should().Be(first.Count);
        foreach (var (module, again) in first.Zip(second))
        {
            again.Should().Be(module, $"{module.Module} must be byte-for-byte stable");
            module.Content.Should().NotContain("\r", $"{module.Module} must use \\n line endings on every OS");
            module.Content.Should().EndWith("\n");
        }
    }

    /// <summary>The committed files are what the generator writes: the CI verify step, as a test.</summary>
    [Fact]
    public void The_committed_library_contracts_are_up_to_date()
    {
        var root = SparkSchemaGeneratorTests.RepositoryRoot();
        foreach (var module in SparkLibraryContracts.Generate())
        {
            var path = SparkContractGenerator.LibraryModulePath(root, module.Module);
            File.Exists(path).Should().BeTrue($"{path} is committed");
            File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(module.Content, $"{path} is regenerated with: {SparkLibraryContracts.RegenerateCommand}");
        }
    }

    /// <summary>
    /// The two drifts the hand-written mirrors had (commerce X20): the attribute's <c>options</c> was missing, and six
    /// of the eleven always-sent permission flags were optional.
    /// </summary>
    [Fact]
    public void The_library_contracts_state_the_previously_drifted_members()
    {
        var models = SparkLibraryContracts.Generate().Single(m => m.Module == "@mintplayer/ng-spark/models").Content;

        Block(models, "export interface PersistentObjectAttribute").Should().Contain("  options?: PersistentObjectAttributeOption[] | null;\n");
        var permissions = Block(models, "export interface EntityPermissions");
        foreach (var flag in new[] { "canQuery", "canRead", "canCreate", "canEdit", "canDelete", "canRestore", "canPurge", "canViewDeleted", "canViewHistory", "canRevert", "canRevertContribution" })
            permissions.Should().Contain($"  {flag}: boolean;\n");
    }

    /// <summary>
    /// <see cref="PersistentObject.Replacement"/> (commerce X7) is server-side only and <c>[JsonIgnore]</c>: the
    /// endpoints answer the receipt itself, so neither the member nor its type reaches the TypeScript.
    /// </summary>
    [Fact]
    public void The_json_ignored_save_replacement_receipt_is_not_a_contract()
    {
        var models = SparkLibraryContracts.Generate().Single(m => m.Module == "@mintplayer/ng-spark/models").Content;

        Block(models, "export interface PersistentObject").Should().NotContain("replacement");
        models.Should().NotContain(nameof(MintPlayer.Spark.Abstractions.Interceptors.SaveReplacementReceipt));
    }

    [Fact]
    public void Verify_fails_on_a_drifted_file_and_leaves_it_alone()
    {
        var root = Directory.CreateTempSubdirectory("spark-contracts-").FullName;
        try
        {
            var module = new SparkContractModule("@mintplayer/ng-spark/models", "// generated\nexport interface A {\n  b: string;\n}\n");
            var path = SparkContractGenerator.LibraryModulePath(root, module.Module);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string drifted = "// generated\nexport interface A {\n  b?: string;\n}\n";
            File.WriteAllText(path, drifted);

            var error = new StringWriter();
            SparkLibraryContracts.Run(() => [module], root, verifyOnly: true, TextWriter.Null, error).Should().Be(3);
            File.ReadAllText(path).Should().Be(drifted, "--verify writes nothing");
            error.ToString().Should().Contain("out of date");

            SparkLibraryContracts.Run(() => [module], root, verifyOnly: false, TextWriter.Null, TextWriter.Null).Should().Be(0);
            File.ReadAllText(path).Should().Be(module.Content);
            SparkLibraryContracts.Run(() => [module], root, verifyOnly: true, TextWriter.Null, TextWriter.Null).Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Verify_reports_a_missing_file_and_accepts_a_CRLF_checkout()
    {
        var root = Directory.CreateTempSubdirectory("spark-contracts-").FullName;
        try
        {
            var module = new SparkContractModule("@mintplayer/ng-spark/models", "export {};\n");
            SparkLibraryContracts.Run(() => [module], root, verifyOnly: true, TextWriter.Null, TextWriter.Null).Should().Be(3);

            var path = SparkContractGenerator.LibraryModulePath(root, module.Module);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "export {};\r\n");
            SparkLibraryContracts.Run(() => [module], root, verifyOnly: true, TextWriter.Null, TextWriter.Null).Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_contract_that_cannot_be_stated_exits_2()
        => SparkLibraryContracts.Run(() => throw new SparkContractException(["broken"]), Path.GetTempPath(), verifyOnly: true, TextWriter.Null, TextWriter.Null)
            .Should().Be(2);

    [Fact]
    public void Library_modules_map_to_their_entry_points()
    {
        SparkContractGenerator.LibraryModulePath("/repo", "@mintplayer/ng-spark/auth/models").Replace('\\', '/')
            .Should().Be("/repo/libs/node_packages/ng-spark/auth/models/src/generated/contracts.ts");
        SparkContractGenerator.ApplicationModulePath("/repo/apps/HR/HR").Replace('\\', '/')
            .Should().Be("/repo/apps/HR/HR/ClientApp/src/app/generated/contracts.ts");
    }

    [Fact]
    public void An_application_that_claims_nothing_has_no_contracts_and_writes_nothing()
    {
        var root = Directory.CreateTempSubdirectory("spark-contracts-app-").FullName;
        try
        {
            // MintPlayer.Spark.Attributes claims no namespace and declares no contract.
            SparkContractCommands.Run(typeof(SparkContractAttribute).Assembly, root, verifyOnly: false, TextWriter.Null, TextWriter.Null).Should().Be(0);
            Directory.Exists(Path.Combine(root, "ClientApp")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
