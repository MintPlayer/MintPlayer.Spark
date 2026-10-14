using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Extensions;

/// <summary>
/// O5 of <c>docs/coverage_merge_queue_PRD.md</c>: <c>--spark-synchronize-security</c> keeps
/// <c>security.json</c>'s <c>rights[]</c> sorted by target, group, right and key, and
/// <c>--spark-verify-security</c> exits 3 when it is not.
/// </summary>
/// <remarks>
/// <para>
/// Why: a right was appended at the end of the array, so every two PRs that granted something conflicted
/// on the same line. Sorted, two PRs for different entities insert in different places.
/// </para>
/// <para>
/// The file is hand-authored, so the rewrite moves whole rights and nothing else: each right's own text
/// (one line or several, with its column padding) and everything outside the array stay byte-identical.
/// Red before the change: synchronize wrote only the posture baseline and verify passed an unsorted array.
/// </para>
/// </remarks>
[Collection(MintPlayer.Spark.Tests.Builder.ProcessExitCodeCollection.Name)]
public class SecurityRightsOrderTests : IDisposable
{
    private const string Anon = "00000000-0000-0000-0000-000000000000";
    private const string Auth = "d1b2c3d4-0000-0000-0000-00000000000f";

    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-rights-order-" + Guid.NewGuid().ToString("N"));

    public SecurityRightsOrderTests() => Directory.CreateDirectory(Path.Combine(root, "App_Data"));

    public void Dispose()
    {
        Environment.ExitCode = 0;
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static string Right(string key, string resource, string group, string pad = "")
        => $"    {{ \"key\": \"{key}\", \"resource\": \"{resource}\",{pad} \"groupId\": \"{group}\", \"isDenied\": false }}";

    private static string File_(params string[] rights)
    {
        var lines = new List<string>
        {
            "{",
            "  \"$schema\": \"../../../../schemas/security.schema.json\",",
            "  \"_comment\": [",
            "    \"Kept as written.\"",
            "  ],",
            "  \"groups\": {",
            $"    \"{Anon}\": \"Anonymous visitors\"",
            "  },",
            "  \"rights\": [",
        };
        for (var i = 0; i < rights.Length; i++)
            lines.Add(rights[i] + (i < rights.Length - 1 ? "," : ""));
        lines.Add("  ]");
        lines.Add("}");
        return Crlf([.. lines]);
    }

    // ── The reorder ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rights_are_sorted_by_target_then_group_then_right_then_key_and_each_line_is_kept()
    {
        var person = Right("k1", "QueryReadEditNewDelete/Person", Anon, pad: "  ");
        var carAuth = Right("k2", "Query/Car", Auth);
        var carAnonRead = Right("k3", "Read/Car", Anon);
        var carAnonQuery = Right("k5", "Query/Car", Anon, pad: "      ");
        var carAnonQueryLowKey = Right("k4", "Query/Car", Anon);

        var sorted = SecurityRightsOrder.Sort(File_(person, carAuth, carAnonRead, carAnonQuery, carAnonQueryLowKey));

        sorted.Should().Be(File_(carAnonQueryLowKey, carAnonQuery, carAnonRead, carAuth, person));
    }

    [Fact]
    public void Multi_line_rights_move_as_whole_blocks()
    {
        static string Block(string key, string resource) =>
            $"    {{\r\n      \"key\": \"{key}\",\r\n      \"resource\": \"{resource}\",\r\n      \"groupId\": \"{Anon}\",\r\n      \"isDenied\": false\r\n    }}";

        var sorted = SecurityRightsOrder.Sort(File_(Block("b", "QueryRead/Repository"), Block("a", "QueryRead/Account")));

        sorted.Should().Be(File_(Block("a", "QueryRead/Account"), Block("b", "QueryRead/Repository")));
    }

    [Fact]
    public void A_sorted_array_is_returned_byte_identical_blank_lines_included()
    {
        var text = File_(Right("a", "Query/Car", Anon), Right("b", "Query/Person", Anon))
            .Replace("\"isDenied\": false },\r\n", "\"isDenied\": false },\r\n\r\n");

        SecurityRightsOrder.Sort(text).Should().Be(text);
        SecurityRightsOrder.IsSorted(text).Should().BeTrue();
    }

    [Fact]
    public void Reordering_joins_the_rights_with_the_files_usual_separator()
    {
        // A blank line grouped the rights by group; sorted by target that grouping no longer holds, so the
        // rights are joined the way most of the file joins them.
        var personAnon = Right("p1", "Query/Person", Anon);
        var carAnon = Right("c1", "Query/Car", Anon);
        var personAuth = Right("p2", "Query/Person", Auth);
        var carAuth = Right("c2", "Query/Car", Auth);
        var grouped = File_(personAnon, carAnon, personAuth, carAuth)
            .Replace(carAnon + ",\r\n", carAnon + ",\r\n\r\n");

        SecurityRightsOrder.Sort(grouped).Should().Be(File_(carAnon, carAuth, personAnon, personAuth));
    }

    [Fact]
    public void An_unreadable_file_is_refused_with_a_message()
    {
        var act = () => SecurityRightsOrder.Sort("{ \"rights\": [ { \"key\": \"a\" }, <<<<<<< HEAD ] }");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("rights");
    }

    // ── The commands ────────────────────────────────────────────────────────────────────────────

    private string SecurityPath => Path.Combine(root, "App_Data", "security.json");
    private string BaselinePath => Path.Combine(root, "App_Data", "securityPosture.txt");

    private WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        var reporter = Substitute.For<ISecurityPostureReporter>();
        reporter.Describe().Returns(new SecurityPosture([], []));
        builder.Services.AddSingleton(reporter);
        return builder;
    }

    private (int ExitCode, string Reported) Run(string flag)
    {
        Environment.ExitCode = 0;
        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            Builder().VerifySparkSecurityIfRequested([flag]).Should().BeTrue();
        }
        finally
        {
            Console.SetError(previous);
        }
        return (Environment.ExitCode, captured.ToString());
    }

    [Fact]
    public void Synchronize_sorts_the_file_and_a_second_run_is_byte_identical()
    {
        var b = Right("b", "Query/Person", Anon);
        var a = Right("a", "Query/Car", Anon);
        File.WriteAllText(SecurityPath, File_(b, a));

        Run("--spark-synchronize-security").ExitCode.Should().Be(0);
        var first = File.ReadAllBytes(SecurityPath);
        Run("--spark-synchronize-security").ExitCode.Should().Be(0);

        File.ReadAllText(SecurityPath).Should().Be(File_(a, b));
        File.ReadAllBytes(SecurityPath).Should().Equal(first);
    }

    [Fact]
    public void Verify_exits_3_on_an_unsorted_array_and_writes_nothing()
    {
        var unsorted = File_(Right("b", "Query/Person", Anon), Right("a", "Query/Car", Anon));
        File.WriteAllText(SecurityPath, unsorted);
        File.WriteAllText(BaselinePath, SparkSecurityVerificationExtensions.Render(new SecurityPosture([], [])));

        var (exitCode, reported) = Run("--spark-verify-security");

        exitCode.Should().Be(3);
        reported.Should().Contain("--spark-synchronize-security");
        File.ReadAllText(SecurityPath).Should().Be(unsorted);
    }

    [Fact]
    public void Verify_passes_a_sorted_array()
    {
        File.WriteAllText(SecurityPath, File_(Right("a", "Query/Car", Anon), Right("b", "Query/Person", Anon)));
        File.WriteAllText(BaselinePath, SparkSecurityVerificationExtensions.Render(new SecurityPosture([], [])));

        Run("--spark-verify-security").ExitCode.Should().Be(0);
    }

    [Fact]
    public void Synchronize_exits_2_on_an_unreadable_file_and_leaves_it_alone()
    {
        const string broken = "{ \"rights\": [\r\n<<<<<<< HEAD\r\n] }\r\n";
        File.WriteAllText(SecurityPath, broken);

        var (exitCode, reported) = Run("--spark-synchronize-security");

        exitCode.Should().Be(2);
        reported.Should().Contain("security.json");
        File.ReadAllText(SecurityPath).Should().Be(broken);
        File.Exists(BaselinePath).Should().BeFalse("nothing is written when the rights cannot be read");
    }
}
