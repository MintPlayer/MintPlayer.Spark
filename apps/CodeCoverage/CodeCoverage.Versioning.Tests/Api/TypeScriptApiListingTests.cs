using CodeCoverage.Versioning.Api;

namespace CodeCoverage.Versioning.Tests.Api;

/// <summary>#153 M7 (D6, S3): the npm half of the API hash, over <c>.d.ts</c> text.</summary>
public class TypeScriptApiListingTests
{
    private static string Hash(params (string Path, string Text)[] files) => TypeScriptApiListing.Hash(TypeScriptApiListing.List(files));

    [Fact]
    public void Comments_and_formatting_do_not_change_the_hash()
    {
        var plain = Hash(("index.d.ts", "export declare function render(size: number): string;\n"));
        var reformatted = Hash(("index.d.ts", "/** Renders. */\nexport declare function render(\n    size : number // the size\n) : string ;\n"));

        reformatted.Should().Be(plain);
    }

    [Fact]
    public void Private_members_are_left_out_and_a_private_constructor_stays()
    {
        var lines = TypeScriptApiListing.List([("w.d.ts", "export declare class Widget {\n    private secret;\n    private static cache;\n    private constructor();\n    size: number;\n}\n")]);

        lines.Should().NotContain(l => l.Contains("secret") || l.Contains("cache"));
        lines.Should().Contain(l => l.Contains("private constructor()"));
        lines.Should().Contain(l => l.Contains("size:number;"));
    }

    [Fact]
    public void A_literal_type_holding_comment_markers_is_kept_as_written()
    {
        var lines = TypeScriptApiListing.List([("u.d.ts", "export type Url = 'https://example.invalid/*';\n")]);

        lines.Should().ContainSingle().Which.Should().Be("u.d.ts: export type Url='https://example.invalid/*';");
    }

    [Fact]
    public void A_visible_change_or_a_moved_file_changes_the_hash_and_file_order_does_not()
    {
        var a = ("a.d.ts", "export declare const a: number;\n");
        var b = ("b.d.ts", "export declare const b: number;\n");

        Hash(a, b).Should().Be(Hash(b, a));
        Hash(a, ("b.d.ts", "export declare const b: string;\n")).Should().NotBe(Hash(a, b));
        Hash(a, ("c.d.ts", b.Item2)).Should().NotBe(Hash(a, b));
    }
}
