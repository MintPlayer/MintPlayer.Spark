using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Tests.Architecture;

/// <summary>
/// Exactly one place in the framework answers <i>"which entity type is this request about?"</i> —
/// <c>SparkRequestType.Resolve</c>. No endpoint reaches around it.
/// </summary>
/// <remarks>
/// <para>
/// This is the inside half of the guard whose outside half is
/// <c>Endpoints/PersistentObject/TypeConflationTests.cs</c>. That one proves the <b>behaviour</b>: a
/// payload naming a different type must not change which type is used, nor buy access to a denied one.
/// This one proves the <b>shape</b> that makes the behaviour reviewable — that there is a single line
/// to get wrong rather than nine.
/// </para>
/// <para>
/// ⚠️ <b>Why a source scan and not a runtime assertion.</b> "Reads the route value" and "reads the
/// payload's nested field" are not observable at runtime from outside the endpoint; a test that drove
/// every endpoint with a lying payload would prove the invariant holds for the endpoints it happened
/// to cover, and say nothing about the tenth one someone adds next month. Reading the source says it
/// about all of them, including ones that do not exist yet.
/// </para>
/// <para>
/// ⚠️ <b>Why it matters more after the route table moves.</b> Today the authoritative value is a
/// <c>{objectTypeId}</c> route segment and the untrusted one is a field inside the submitted document
/// — impossible to confuse. The route table is becoming fully literal (<c>POST /spark/po/create</c>),
/// after which both are JSON fields one word apart in the same body. At that point this test is what
/// stops the two being swapped, so when the migration lands, the second pattern below is the one that
/// has to keep failing.
/// </para>
/// <para>
/// Nothing here depends on <c>MintPlayer.Spark.Authorization</c>. That package is optional and may be
/// absent from the container entirely — which is the reason this invariant cannot be left to
/// authorization to enforce. The resolved type decides <i>which collection is read and written</i>,
/// not merely which permission is consulted, so getting it from the client is a defect in a
/// deployment with no authentication at all.
/// </para>
/// </remarks>
public class SparkRequestTypeSingleSourceTests
{
    /// <summary>The framework source that must go through the single resolver.</summary>
    private static readonly string[] ScannedDirectories =
    [
        Path.Combine("libs", "spark", "MintPlayer.Spark", "Endpoints"),
    ];

    /// <summary>The one file allowed to read the route value — the resolver itself.</summary>
    private const string Resolver = "SparkRequestType.cs";

    [Fact]
    public void No_endpoint_reads_the_route_value_directly()
    {
        // The literal string, not the route template: "/{objectTypeId}/refresh" in a Path property is
        // a declaration and stays. An indexer into RouteValues is a read, and that is what moves.
        var offenders = Scan(new Regex(@"RouteValues\s*\[\s*""objectTypeId""\s*\]"));

        offenders.Should().BeEmpty(
            "the entity type must come from SparkRequestType.Resolve, which is the one line that changes "
            + "when the type moves out of the route and into the request body");
    }

    [Fact]
    public void No_endpoint_takes_the_type_from_the_submitted_object()
    {
        // persistentObject.objectTypeId is submitted data — the object declaring what it is. Using it
        // to decide what the request may touch is the confused-deputy shape (security sweep C3), and
        // it is why Create overwrites the field on the way in rather than reading it.
        //
        //
        // ⚠️ The rule is "no ObjectTypeId reaches a type lookup", not the broader "no ObjectTypeId is
        // read anywhere". The broad version was written first and is wrong: Get.cs:95 compares
        // obj.ObjectTypeId against a client-operation target, on an object the SERVER built from the
        // database. That is a legitimate read, and a test that forbids it only teaches people to
        // work around the test. What is never legitimate is one of these deciding which type a
        // request gets — the call shape below, and the only way the claim can become authority.
        var offenders = Scan(new Regex(
            @"\b(?:ResolveEntityType|GetEntityType(?:ByName|ByAlias|ByClrType)?|EnsureAuthorizedAsync)\s*\([^)]*\bObjectTypeId\b"));

        offenders.Should().BeEmpty(
            "the submitted object's own objectTypeId is a claim, never the authority for what the "
            + "request may touch — resolve the type through SparkRequestType.Resolve instead");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static List<string> Scan(Regex pattern)
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var directory in ScannedDirectories)
        {
            var full = Path.Combine(root, directory);
            Directory.Exists(full).Should().BeTrue($"'{directory}' must exist, or this test silently passes by scanning nothing");

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == Resolver)
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // Skip comments and doc comments — they describe the old shape on purpose.
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith('*'))
                        continue;

                    if (pattern.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {trimmed}");
                }
            }
        }

        return offenders;
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution. ⚠️ A wrong answer here
    /// makes the test vacuous rather than red, which is why <see cref="Scan"/> asserts the scanned
    /// directory exists before believing an empty result.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
