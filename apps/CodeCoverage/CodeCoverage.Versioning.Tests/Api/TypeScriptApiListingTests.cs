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

    /// <summary>
    /// #576: <c>@mintplayer/ui-core</c> compiles each stylesheet into an exported constant, whose declaration carries the
    /// whole CSS. A styles-only fix changed the hash and released a minor.
    /// </summary>
    [Fact]
    public void A_constant_lists_by_its_type_so_its_value_does_not_change_the_hash()
    {
        var before = Hash(("tooltip.scoped.styles.d.ts", "export declare const tooltipScopedStyles = \"@layer mp.components {\\n.tooltip { top: 0; }\\n}\";\nexport default tooltipScopedStyles;\n"));
        var after = Hash(("tooltip.scoped.styles.d.ts", "export declare const tooltipScopedStyles = \"@layer mp.components {\\n.tooltip { top: 0; inset: auto; }\\n}\";\nexport default tooltipScopedStyles;\n"));

        after.Should().Be(before);
        TypeScriptApiListing.List([("s.d.ts", "export declare const styles = \"a { b: c; }\";\n")])
            .Should().Equal("s.d.ts: export declare const styles:string;");
    }

    /// <summary>#576 S1: every initializer TypeScript 6.0 writes into a declaration, as it writes them.</summary>
    [Fact]
    public void Each_literal_the_compiler_emits_becomes_its_type()
    {
        var lines = TypeScriptApiListing.List([("c.d.ts", """
            export declare const quoted = "it's \"q\"";
            export declare const single = 'x';
            export declare const negative = -1500;
            export declare const small = 1e-7;
            export declare const big = 10n;
            export declare const flag = true;
            declare const hidden = "kept-by-export-default";
            export declare const a = 1, b = "two";
            export declare class Widget {
                static readonly tag = "mp-widget";
                readonly size = 3;
            }
            """)]);

        lines.Should().Equal(
            "c.d.ts: export declare const quoted:string;",
            "c.d.ts: export declare const single:string;",
            "c.d.ts: export declare const negative:number;",
            "c.d.ts: export declare const small:number;",
            "c.d.ts: export declare const big:bigint;",
            "c.d.ts: export declare const flag:boolean;",
            "c.d.ts: declare const hidden:string;",
            "c.d.ts: export declare const a:number,b:string;",
            "c.d.ts: export declare class Widget{",
            "c.d.ts: static readonly tag:string;",
            "c.d.ts: readonly size:number;",
            "c.d.ts: }");
    }

    [Fact]
    public void Declared_literal_types_type_aliases_enum_members_and_text_inside_strings_stay()
    {
        var lines = TypeScriptApiListing.List([("k.d.ts", """
            export declare const asConst: "x";
            export type Mode = "light" | "dark";
            export declare enum Kind {
                A = "a",
                B = 2
            }
            export declare const code = "declare const inner = 1; readonly r = 2;";
            """)]);

        lines.Should().Equal(
            "k.d.ts: export declare const asConst:\"x\";",
            "k.d.ts: export type Mode=\"light\"|\"dark\";",
            "k.d.ts: export declare enum Kind{",
            "k.d.ts: A=\"a\",B=2}",
            "k.d.ts: export declare const code:string;");
    }

    [Fact]
    public void The_hash_names_listing_version_2()
        => Hash(("a.d.ts", "export declare const a: number;\n")).Should().StartWith("v2:sha256:");

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
