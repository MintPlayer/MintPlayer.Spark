using MintPlayer.Spark.PageMeta;

namespace MintPlayer.Spark.Tests.PageMeta;

/// <summary>The <c>index.html</c> rewrite of X6 on its own: which template tags go, what is inserted, and the encoding.</summary>
public class SparkPageMetaHtmlTests
{
    private static readonly SparkPageMeta Meta = new("Rock & Roll <live>", "Say \"hi\" & 'bye'");
    private static readonly Uri Canonical = new("https://shop.example/events/rock");

    [Fact]
    public void The_tags_go_right_after_the_head_element()
    {
        var html = SparkPageMetaHtml.Rewrite("<html><HEAD lang=\"en\"><meta charset=\"utf-8\"></HEAD><body></body></html>", Meta, Canonical, null)!;

        html.Should().StartWith("<html><HEAD lang=\"en\">\n<title data-spark-meta>");
        html.Should().Contain("<meta charset=\"utf-8\"></HEAD>");
    }

    [Fact]
    public void Every_value_is_encoded()
    {
        var html = SparkPageMetaHtml.Rewrite("<head></head>", Meta, Canonical, null)!;

        html.Should().Contain("<title data-spark-meta>Rock &amp; Roll &lt;live&gt;</title>");
        html.Should().Contain("<meta name=\"description\" content=\"Say &quot;hi&quot; &amp; &#x27;bye&#x27;\" data-spark-meta>");
        html.Should().NotContain("<live>");
    }

    [Fact]
    public void Letters_of_other_scripts_stay_readable()
    {
        var html = SparkPageMetaHtml.Rewrite("<head></head>", Meta with { Title = "Fête de la musique – Liège" }, Canonical, null)!;

        html.Should().Contain("<title data-spark-meta>Fête de la musique – Liège</title>");
    }

    [Fact]
    public void The_templates_own_title_description_cards_and_canonical_are_removed()
    {
        const string template = """
            <head>
            <title>App</title>
            <meta name=description content=Template>
            <meta property="og:image" content="/x.png" />
            <meta content="summary" name="twitter:card">
            <link rel="canonical" href="https://template.example/">
            <meta name="theme-color" content="#fff">
            </head>
            <body><svg><title>Icon</title></svg></body>
            """;

        var html = SparkPageMetaHtml.Rewrite(template, Meta, Canonical, null)!;

        html.Should().NotContain("<title>App</title>");
        html.Should().NotContain("Template");
        html.Should().NotContain("/x.png");
        html.Should().NotContain("content=\"summary\"");
        html.Should().NotContain("template.example");
        html.Should().Contain("<meta name=\"theme-color\" content=\"#fff\">");
        html.Should().Contain("<svg><title>Icon</title></svg>", "only the head's own title is the page title");
    }

    [Fact]
    public void Without_an_origin_the_url_tags_are_left_out()
    {
        var html = SparkPageMetaHtml.Rewrite("<head></head>", Meta, null, null)!;

        html.Should().NotContain("og:url");
        html.Should().NotContain("canonical");
        html.Should().NotContain("og:image");
        html.Should().Contain("og:title");
    }

    [Fact]
    public void A_page_without_a_head_is_not_rewritten()
        => SparkPageMetaHtml.Rewrite("<html><body>Hi</body></html>", Meta, Canonical, null).Should().BeNull();
}
