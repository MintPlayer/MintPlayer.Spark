using System.Globalization;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// The model hash decides whether a deployed application is allowed to start, so these tests treat
/// determinism as a safety property rather than a nicety. A hash that varies between runs of the
/// same code does not produce a merge conflict — it stops a healthy app from booting, on some
/// machines and not others.
/// </summary>
public class SparkModelShapeTests
{
    private const string PinnedShapeProbeHash = "de52c0ccefd7a80ae1467274245f4a35edec3cf1a65a14dd3b078a7115ef9938";

    private static SparkModelType Shape<T>(string? queryType = null, string? index = null)
        => new(typeof(T), queryType, index);

    // --- determinism ----------------------------------------------------

    [Fact]
    public void Member_order_does_not_affect_the_hash()
    {
        // The load-bearing invariant. Reflection does not guarantee member order, and this codebase
        // is full of partial classes and source generators, so GetProperties() order genuinely does
        // move between builds. ReorderedShapeProbe declares the same members as ShapeProbe in a
        // different order; if the ordinal sort is ever "simplified" away, this fails here rather
        // than bricking a production deployment on an unrelated rebuild.
        var first = SparkModelShape.Describe(Shape<ShapeProbe>());
        var second = SparkModelShape.Describe(Shape<ReorderedShapeProbe>());

        // Strip the type header, which legitimately differs.
        static string Body(string text) => string.Join('\n', text.Split('\n').Skip(1));

        Body(second).Should().Be(Body(first),
            "property order in source must not change the canonical shape");
    }

    [Fact]
    public void Hash_is_stable_across_repeated_computation()
    {
        var hashes = Enumerable.Range(0, 25)
            .Select(_ => SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        hashes.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("tr-TR")]  // the Turkish-I trap
    [InlineData("nl-BE")]
    [InlineData("en-US")]
    public void Hash_is_independent_of_the_current_culture(string culture)
    {
        var invariantHash = ComputeUnder(CultureInfo.InvariantCulture);
        var localisedHash = ComputeUnder(new CultureInfo(culture));

        localisedHash.Should().Be(invariantHash);

        static string ComputeUnder(CultureInfo culture)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                return SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>());
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
    }

    [Fact]
    public void Hash_matches_the_pinned_value()
    {
        // A golden hash, committed from a Windows developer machine. CI runs on Linux, so this
        // assertion is the cross-OS and cross-machine determinism gate: if the canonical text or the
        // digest ever differs by platform, runtime version or machine, it fails here rather than as
        // a container that refuses to start after a redeploy.
        //
        // If this fails after a deliberate change to the canonical format, that is expected —
        // re-pin it, and treat every deployed modelHashes.json as needing regeneration.
        SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>())
            .Should().Be(PinnedShapeProbeHash);
    }

    // The CRLF/LF breadcrumb-normalization test was removed with the class-level [Breadcrumb]
    // template (#273): breadcrumb templates now live only in the model JSON, which this hash
    // deliberately does not cover, so no author-supplied newline can reach it any more.

    [Fact]
    public void TranslatedString_is_its_own_data_type_not_AsDetail()
    {
        // It is a class with properties, so the complex-type fallback would classify it as AsDetail —
        // which emits it as a nested detail object with a null value on the wire, breaking the
        // per-language merge, and generates a spurious TranslatedString.json model file.
        SparkModelShape.GetDataType(typeof(TranslatedString)).Should().Be("TranslatedString");
        SparkModelShape.IsComplexType(typeof(TranslatedString)).Should().BeFalse(
            "it is a value carried in one attribute, not a nested entity");
    }

    [Fact]
    public void Canonical_text_uses_newline_and_no_BOM()
    {
        // Windows writes the hash file, Linux verifies it. Environment.NewLine or a BOM here would
        // make every containerised deployment fail verification.
        var text = SparkModelShape.Describe(Shape<ShapeProbe>());

        text.Should().NotContain("\r", "a CRLF would make the hash differ between Windows and Linux");
        text.Should().NotStartWith("﻿");
        Encoding.UTF8.GetBytes(text).Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
    }

    // --- sensitivity: changes that MUST move the hash --------------------

    [Fact]
    public void Adding_a_property_changes_the_hash()
        => ShapeBody<ShapeProbe>().Should().NotBe(ShapeBody<ShapeProbeWithExtraProperty>());

    [Fact]
    public void Removing_a_setter_changes_the_hash()
        => ShapeBody<ShapeProbe>().Should().NotBe(ShapeBody<ShapeProbeWithGetOnly>(),
            "a get-only property becomes a read-only, non-required attribute");

    [Fact]
    public void Changing_a_property_type_changes_the_hash()
        => ShapeBody<ShapeProbe>().Should().NotBe(ShapeBody<ShapeProbeWithChangedType>());

    [Fact]
    public void Adding_IgnoreProperty_changes_the_hash()
        => ShapeBody<ShapeProbe>().Should().NotBe(ShapeBody<ShapeProbeWithIgnoredProperty>());

    [Fact]
    public void Adding_Sortable_changes_the_hash()
        => ShapeBody<ShapeProbe>().Should().NotBe(ShapeBody<ShapeProbeWithSortable>());

    [Fact]
    public void Changing_a_Reference_target_changes_the_hash()
        => ShapeBody<ShapeProbeWithReference>().Should().NotBe(ShapeBody<ShapeProbeWithOtherReference>());

    [Fact]
    public void Changing_the_index_or_query_type_changes_the_hash()
    {
        var plain = SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>());
        var indexed = SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>(queryType: "V.Probe", index: "Probes/Overview"));

        indexed.Should().NotBe(plain);
    }

    // --- insensitivity: changes that produce identical JSON --------------

    [Fact]
    public void Widening_int_to_long_does_not_change_the_hash()
        => ShapeBody<ShapeProbe>().Should().Be(ShapeBody<ShapeProbeWithLong>(),
            "both generate dataType 'number', so the model file is byte-identical");

    [Fact]
    public void Swapping_a_list_for_an_array_does_not_change_the_hash()
        => ShapeBody<ShapeProbe>().Should().Be(ShapeBody<ShapeProbeWithArray>(),
            "both generate an array of the same element type");

    // --- roll-up and context roots ---------------------------------------

    [Fact]
    public void Removing_a_queryable_root_changes_the_context_roots_hash()
    {
        // Per-entity hashes cannot see this on their own: the orphaned model file and its CLR class
        // both still exist and still agree with each other.
        var before = SparkModelShape.ComputeContextRootsHash(["Company", "Person"]);
        var after = SparkModelShape.ComputeContextRootsHash(["Company"]);

        after.Should().NotBe(before);
    }

    [Fact]
    public void Context_roots_hash_ignores_declaration_order()
        => SparkModelShape.ComputeContextRootsHash(["Person", "Company"])
            .Should().Be(SparkModelShape.ComputeContextRootsHash(["Company", "Person"]));

    [Fact]
    public void Model_hash_changes_when_any_entity_hash_changes()
    {
        var roots = SparkModelShape.ComputeContextRootsHash(["ShapeProbe"]);
        var baseline = SparkModelShape.ComputeModelHash(
            SparkModelShape.ComputePerEntityHashes([Shape<ShapeProbe>()]), roots, "files");
        var changed = SparkModelShape.ComputeModelHash(
            SparkModelShape.ComputePerEntityHashes([Shape<ShapeProbeWithExtraProperty>()]), roots, "files");

        changed.Should().NotBe(baseline);
    }

    [Fact]
    public void Per_entity_hashes_are_keyed_by_entity_name_and_ordinally_sorted()
    {
        var hashes = SparkModelShape.ComputePerEntityHashes(
            [Shape<ShapeProbeWithReference>(), Shape<ShapeProbe>()]);

        hashes.Keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
        hashes.Should().ContainKey(nameof(ShapeProbe));
    }

    [Fact]
    public void Hash_is_full_length_lowercase_hex()
    {
        var hash = SparkModelShape.ComputeEntityHash(Shape<ShapeProbe>());

        hash.Should().MatchRegex("^[0-9a-f]{64}$",
            "truncating only invites a later argument about whether it is still enough");
    }

    /// <summary>
    /// Canonical text with the type-name header removed, so two fixture classes can be compared on
    /// the part that describes their properties. Comparing whole-entity hashes across fixtures would
    /// always differ on the type name and prove nothing.
    /// </summary>
    private static string ShapeBody<T>()
        => string.Join('\n', SparkModelShape.Describe(Shape<T>()).Split('\n').Skip(1));

}

// --- fixtures -----------------------------------------------------------

public sealed class ShapeProbeAddress
{
    public string? Street { get; set; }
    public string? City { get; set; }
}

public sealed class ShapeProbeCompany
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class ShapeProbeOtherCompany
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class ShapeProbe
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

/// <summary>
/// Same members as <see cref="ShapeProbe"/>, declared in a different order. Pins that the canonical
/// text is sorted rather than reflection-ordered.
/// </summary>
public sealed class ReorderedShapeProbe
{
    public ShapeProbeAddress? Home { get; set; }
    public List<string>? Tags { get; set; }
    public int Age { get; set; }
    public string? Name { get; set; }
    public string? Id { get; set; }
}

public sealed class ShapeProbeWithExtraProperty
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
    public string? Nickname { get; set; }
}

public sealed class ShapeProbeWithGetOnly
{
    public string? Id { get; set; }
    public string? Name { get; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

public sealed class ShapeProbeWithChangedType
{
    public string? Id { get; set; }
    public bool Name { get; set; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

public sealed class ShapeProbeWithIgnoredProperty
{
    public string? Id { get; set; }
    [IgnoreProperty] public string? Name { get; set; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

public sealed class ShapeProbeWithSortable
{
    public string? Id { get; set; }
    [Sortable] public string? Name { get; set; }
    public int Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

/// <summary>int widened to long — both map to dataType "number".</summary>
public sealed class ShapeProbeWithLong
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public long Age { get; set; }
    public List<string>? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

/// <summary>List&lt;string&gt; swapped for string[] — identical generated JSON.</summary>
public sealed class ShapeProbeWithArray
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int Age { get; set; }
    public string[]? Tags { get; set; }
    public ShapeProbeAddress? Home { get; set; }
}

public sealed class ShapeProbeWithReference
{
    public string? Id { get; set; }
    [Reference(typeof(ShapeProbeCompany))] public string? CompanyId { get; set; }
}

public sealed class ShapeProbeWithOtherReference
{
    public string? Id { get; set; }
    [Reference(typeof(ShapeProbeOtherCompany))] public string? CompanyId { get; set; }
}
