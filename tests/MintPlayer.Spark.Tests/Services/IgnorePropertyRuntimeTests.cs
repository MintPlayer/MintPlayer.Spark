using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #254 — the runtime paths that reflect over CLR properties directly instead of reading the
/// generated model. Each of these would still surface an <c>[IgnoreProperty]</c> property even
/// after model synchronization excludes it, so each needs the check of its own.
/// </summary>
public class IgnorePropertyRuntimeTests
{
    private static ReferenceResolver CreateResolver()
    {
        var actionsResolver = Substitute.For<IActionsResolver>();
        // ResolveForType returns object, which NSubstitute leaves null — give it a bare instance
        // so GetDefaultIncludes finds no method and contributes no paths.
        actionsResolver.ResolveForType(Arg.Any<Type>()).Returns(new object());
        return new ReferenceResolver(actionsResolver, null);
    }

    [Fact]
    public void Ignored_reference_property_is_not_an_include_path()
    {
        // Beyond the wasted load, including it would pull the referenced document into the
        // session for a field the client must never see.
        var paths = CreateResolver().ResolveIncludePaths(typeof(IP_Book), typeof(IP_Book));

        paths.Should().Contain("AuthorId");
        paths.Should().NotContain("SecretAuthorId");
    }

    [Fact]
    public void Ignored_reference_property_is_not_returned_as_a_reference_property()
    {
        var refs = CreateResolver().GetReferenceProperties(typeof(IP_Book));

        refs.Select(r => r.Property.Name).Should().BeEquivalentTo(["AuthorId"]);
    }

    [Fact]
    public void Projection_fallback_respects_an_ignore_on_the_projection_side()
    {
        // The projection declares the property without [Reference]; the entity carries the
        // attribute. The pairing must still honour the projection's own ignore.
        var refs = CreateResolver().GetReferenceProperties(typeof(IP_BookProjection), typeof(IP_Book));

        refs.Select(r => r.Property.Name).Should().NotContain("SecretAuthorId");
    }

    public class IP_Author
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class IP_Book
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;

        [Reference(typeof(IP_Author), "GetAuthors")]
        public string? AuthorId { get; set; }

        [IgnoreProperty]
        [Reference(typeof(IP_Author), "GetAuthors")]
        public string? SecretAuthorId { get; set; }
    }

    public class IP_BookProjection
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? AuthorId { get; set; }

        [IgnoreProperty]
        public string? SecretAuthorId { get; set; }
    }
}
