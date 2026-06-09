using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services.Breadcrumb;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Unit coverage for <see cref="EmbeddedBreadcrumbRenderer"/> — the in-place renderer for an
/// embedded AsDetail row's own breadcrumb. Pins every token branch (literal, scalar, single
/// reference, reference array) plus the no-template and missing-id fallbacks.
/// </summary>
public class EmbeddedBreadcrumbRendererTests
{
    private sealed class Member
    {
        public string? PersonId { get; set; }
        public string? Role { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    private static EntityAttributeDefinition Scalar(string name) => new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };
    private static EntityAttributeDefinition Ref(string name, bool isArray = false) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = "Demo.X", IsArray = isArray };

    private static EntityTypeDefinition Def(string breadcrumb, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = "Member", ClrType = typeof(Member).FullName!, Breadcrumb = breadcrumb, Attributes = attrs };

    private static BreadcrumbResult Result(params (string id, string crumb)[] entries) =>
        new(entries.ToDictionary(e => e.id, e => e.crumb, StringComparer.Ordinal));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_template_returns_null(string? template)
    {
        var def = Def(template!, Scalar("Role"));
        EmbeddedBreadcrumbRenderer.Render(new Member { Role = "Singer" }, def, BreadcrumbResult.Empty, ", ")
            .Should().BeNull();
    }

    [Fact]
    public void Literal_and_scalar_tokens_render_from_the_entity()
    {
        var def = Def("Role: {Role}", Scalar("Role"));
        EmbeddedBreadcrumbRenderer.Render(new Member { Role = "Singer" }, def, BreadcrumbResult.Empty, ", ")
            .Should().Be("Role: Singer");
    }

    [Fact]
    public void Single_reference_token_renders_the_resolved_breadcrumb()
    {
        var def = Def("{PersonId}", Ref("PersonId"));
        var result = Result(("People/1", "Freddie Mercury"));
        EmbeddedBreadcrumbRenderer.Render(new Member { PersonId = "People/1" }, def, result, ", ")
            .Should().Be("Freddie Mercury");
    }

    [Fact]
    public void Reference_token_with_id_absent_from_the_result_renders_empty()
    {
        var def = Def("{PersonId}", Ref("PersonId"));
        EmbeddedBreadcrumbRenderer.Render(new Member { PersonId = "People/1" }, def, BreadcrumbResult.Empty, ", ")
            .Should().Be("");
    }

    [Fact]
    public void Reference_array_token_joins_each_resolved_breadcrumb_with_the_separator()
    {
        var def = Def("{Tags}", Ref("Tags", isArray: true));
        var result = Result(("Tags/1", "Rock"), ("Tags/2", "Pop"));
        EmbeddedBreadcrumbRenderer.Render(new Member { Tags = ["Tags/1", "Tags/2"] }, def, result, " | ")
            .Should().Be("Rock | Pop");
    }
}
