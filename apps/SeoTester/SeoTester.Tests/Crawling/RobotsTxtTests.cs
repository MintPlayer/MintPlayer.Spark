using SeoTester.Crawling;

namespace SeoTester.Tests.Crawling;

/// <summary>robots.txt per RFC 9309 for <see cref="SeoTesterBot.RobotsToken"/> (D19: the only opt-out; Q-M8e: checked by the probe).</summary>
public class RobotsTxtTests
{
    private static RobotsTxt Parse(string text) => RobotsTxt.Parse(text, SeoTesterBot.RobotsToken);

    [Fact]
    public void Our_own_group_wins_over_the_star_group_and_is_matched_case_insensitively_by_product_token()
    {
        var robots = Parse("User-agent: *\nDisallow: /\n\nUser-agent: mintplayerseotester/1.0\nDisallow: /private\n");

        robots.HasOwnGroup.Should().BeTrue();
        robots.IsAllowed("/").Should().BeTrue();
        robots.IsAllowed("/private/x").Should().BeFalse();
        robots.DisallowsAllForEveryone.Should().BeTrue("the * group still blocks everyone else");
        robots.OptedOut.Should().BeFalse();
    }

    [Fact]
    public void A_site_opts_out_by_naming_the_crawler()
    {
        var robots = Parse("User-agent: Googlebot\nAllow: /\n\nUser-agent: MintPlayerSeoTester\nDisallow: /\n");

        robots.OptedOut.Should().BeTrue();
        robots.DisallowsAll.Should().BeTrue();
        robots.DisallowsAllForEveryone.Should().BeFalse();
    }

    [Fact]
    public void The_star_group_applies_when_no_group_names_the_crawler()
    {
        var robots = Parse("User-agent: *\nDisallow: /admin\n");

        robots.HasOwnGroup.Should().BeFalse();
        robots.IsAllowed("/admin/users").Should().BeFalse();
        robots.IsAllowed("/about").Should().BeTrue();
    }

    [Fact]
    public void The_longest_match_wins_and_allow_wins_a_tie()
    {
        var robots = Parse("User-agent: *\nDisallow: /shop\nAllow: /shop/public\nDisallow: /a\nAllow: /a\n");

        robots.IsAllowed("/shop/cart").Should().BeFalse();
        robots.IsAllowed("/shop/public/item").Should().BeTrue();
        robots.IsAllowed("/a/b").Should().BeTrue("allow wins a tie of equal length");
    }

    [Fact]
    public void Wildcards_and_the_end_anchor_match_as_the_rfc_says()
    {
        var robots = Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /search*q=\n");

        robots.IsAllowed("/files/a.pdf").Should().BeFalse();
        robots.IsAllowed("/files/a.pdf?x=1").Should().BeTrue("$ anchors the end");
        robots.IsAllowed("/search?page=2&q=shoes").Should().BeFalse();
        robots.IsAllowed("/search?page=2").Should().BeTrue();
    }

    [Fact]
    public void Groups_naming_the_crawler_twice_are_merged_and_lines_before_any_group_are_ignored()
    {
        var robots = Parse("Disallow: /orphan\nUser-agent: MintPlayerSeoTester\nUser-agent: other\nDisallow: /one\n\nUser-agent: MintPlayerSeoTester\nDisallow: /two\nCrawl-delay: 2.5\n");

        robots.IsAllowed("/orphan").Should().BeTrue();
        robots.IsAllowed("/one").Should().BeFalse();
        robots.IsAllowed("/two").Should().BeFalse();
        robots.CrawlDelaySeconds.Should().Be(2.5);
    }

    [Fact]
    public void Sitemaps_comments_and_unknown_lines_are_read()
    {
        var robots = Parse("# hello\nSitemap: https://example.com/sitemap.xml\nUser-agent: *\nDissallow: /typo # a typo\nDisallow:\n");

        robots.Sitemaps.Should().Equal("https://example.com/sitemap.xml");
        robots.UnknownLines.Should().Be(1);
        robots.IsAllowed("/typo").Should().BeTrue("an empty Disallow allows everything and the typo is no rule");
    }

    [Fact]
    public void Percent_encodings_compare_case_insensitively_and_robots_txt_itself_is_always_allowed()
    {
        var robots = Parse("User-agent: *\nDisallow: /caf%c3%a9\nDisallow: /\nAllow: /$\n");

        robots.IsAllowed("/caf%C3%A9/menu").Should().BeFalse();
        robots.IsAllowed("/robots.txt").Should().BeTrue();
        robots.IsAllowed("/").Should().BeTrue();
        robots.IsAllowed("/x").Should().BeFalse();
    }

    [Fact]
    public void Missing_and_unreachable_robots_txt_follow_rfc_9309()
    {
        RobotsTxt.AllowAll.IsAllowed("/anything").Should().BeTrue();
        RobotsTxt.DisallowAll.DisallowsAll.Should().BeTrue();
        RobotsTxt.DisallowAll.Availability.Should().Be(RobotsAvailability.Unreachable);
    }

    [Fact]
    public void A_hostile_pattern_cannot_make_matching_slow()
    {
        var robots = Parse("User-agent: *\nDisallow: /" + string.Concat(Enumerable.Repeat("*a", 2000)) + "b\n");
        var path = "/" + new string('a', 20_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        robots.IsAllowed(path).Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Googlebot_is_judged_by_its_own_group_else_by_star()
    {
        Parse("User-agent: Googlebot\nDisallow: /\n").DisallowsAllForGooglebot.Should().BeTrue();
        Parse("User-agent: *\nDisallow: /\n").DisallowsAllForGooglebot.Should().BeTrue();
        Parse("User-agent: *\nDisallow: /\nUser-agent: Googlebot\nAllow: /\n").DisallowsAllForGooglebot.Should().BeFalse();
    }
}
