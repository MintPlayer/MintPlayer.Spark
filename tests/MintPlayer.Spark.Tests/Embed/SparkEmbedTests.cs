using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Embed;
using MintPlayer.Spark.Embed;

namespace MintPlayer.Spark.Tests.Embed;

/// <summary>
/// Embed mode (commerce PRD X23, GB8): the framing policy per route, the origin rules, and the hand-over token that
/// carries an embed's state to a top-level window without cookies.
/// </summary>
public sealed class SparkEmbedTests
{
    private static SparkEmbedOptions Options(params string[] origins) => new()
    {
        Enabled = true,
        AllowedOrigins = [.. origins],
        PublicOrigin = "https://tickets.example",
    };

    [Theory]
    [InlineData("https://organiser.example", true)]
    [InlineData("https://*.organiser.example", true)]
    [InlineData("https://organiser.example:8443", true)]
    [InlineData("http://localhost:4200", true)]
    [InlineData("http://organiser.example", false)]
    [InlineData("https://organiser.example/", false)]
    [InlineData("https://organiser.example; script-src *", false)]
    [InlineData("*", false)]
    [InlineData("'unsafe-inline'", false)]
    [InlineData("https://*", false)]
    public void Only_plain_origins_are_accepted(string origin, bool valid)
        => SparkEmbed.IsValidOrigin(origin).Should().Be(valid);

    [Fact]
    public void A_wildcard_matches_subdomains_only()
    {
        string[] allowed = ["https://*.organiser.example", "https://partner.example"];

        SparkEmbed.IsAllowedOrigin("https://shop.organiser.example", allowed).Should().BeTrue();
        SparkEmbed.IsAllowedOrigin("https://organiser.example", allowed).Should().BeFalse();
        SparkEmbed.IsAllowedOrigin("https://evilorganiser.example", allowed).Should().BeFalse();
        SparkEmbed.IsAllowedOrigin("https://partner.example.evil.test", allowed).Should().BeFalse();
        SparkEmbed.IsAllowedOrigin("http://partner.example", allowed).Should().BeFalse();
    }

    [Fact]
    public void Embed_routes_list_the_allowed_origins_and_every_other_page_only_self()
    {
        var options = Options("https://organiser.example", "https://*.partner.example");

        SparkEmbed.FrameAncestors(options, "/embed/events/x").Should().Be("frame-ancestors 'self' https://organiser.example https://*.partner.example");
        SparkEmbed.FrameAncestors(options, "/embedded").Should().Be("frame-ancestors 'self'");
        SparkEmbed.FrameAncestors(options, "/checkout").Should().Be("frame-ancestors 'self'");
        SparkEmbed.FrameAncestors(new SparkEmbedOptions(), "/embed/x").Should().BeNull();
    }

    [Fact]
    public void Startup_refuses_a_malformed_origin()
    {
        var options = Options("https://organiser.example", "https://x.example; frame-ancestors *");
        SparkEmbed.Validate(options).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_html_response_of_an_embed_route_gets_the_policy_and_loses_x_frame_options()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    SparkEmbed.AddSparkEmbed(services);
                    services.Configure<SparkEmbedOptions>(o => { o.Enabled = true; o.AllowedOrigins.Add("https://organiser.example"); });
                })
                .Configure(app => app.Run(async context =>
                {
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync("<html></html>");
                })))
            .StartAsync();
        var client = host.GetTestClient();

        var embed = await client.GetAsync("/embed/events/x");
        embed.Headers.GetValues("Content-Security-Policy").Should().BeEquivalentTo(["frame-ancestors 'self' https://organiser.example"]);
        embed.Headers.Contains("X-Frame-Options").Should().BeFalse();

        var other = await client.GetAsync("/checkout");
        other.Headers.GetValues("Content-Security-Policy").Should().BeEquivalentTo(["frame-ancestors 'self'"]);
        other.Headers.Contains("X-Frame-Options").Should().BeTrue();
    }

    private static SparkEmbedHandoverService Handover(SparkEmbedOptions options, IDataProtectionProvider? protection = null)
        => new(Microsoft.Extensions.Options.Options.Create(options), protection ?? new EphemeralDataProtectionProvider(),
            new HttpContextAccessor(), new ServiceCollection().BuildServiceProvider());

    [Fact]
    public void A_hand_over_carries_its_state_in_a_signed_token()
    {
        var protection = new EphemeralDataProtectionProvider();
        var service = Handover(Options("https://organiser.example"), protection);
        using var payload = JsonDocument.Parse("""{"offers":[{"id":"o1","quantity":2}]}""");

        var handover = service.Create(new SparkEmbedHandoverRequest("/checkout", payload.RootElement, "https://organiser.example/tickets"), out var error);

        error.Should().BeNull();
        handover!.Url.Should().StartWith("https://tickets.example/checkout?spark-handover=");
        var token = Uri.UnescapeDataString(handover.Url[(handover.Url.IndexOf('=') + 1)..]);
        var state = service.Redeem(token)!;
        state.Path.Should().Be("/checkout");
        state.ReturnUrl.Should().Be("https://organiser.example/tickets");
        state.Payload!.Value.GetProperty("offers")[0].GetProperty("quantity").GetInt32().Should().Be(2);

        service.Redeem(token[..^2] + "AA").Should().BeNull();
        Handover(Options("https://organiser.example")).Redeem(token).Should().BeNull(); // another key ring
    }

    [Theory]
    [InlineData("https://evil.example/checkout", null)]
    [InlineData("//evil.example/checkout", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("/checkout", "https://evil.example/")]
    [InlineData("/checkout", "javascript:alert(1)")]
    public void A_hand_over_never_leaves_the_platform_nor_returns_to_an_unlisted_site(string path, string? returnUrl)
    {
        var handover = Handover(Options("https://organiser.example")).Create(new SparkEmbedHandoverRequest(path, null, returnUrl), out var error);

        handover.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void A_hand_over_refuses_a_large_payload_and_is_off_without_embed_mode()
    {
        using var big = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', 3000)));
        Handover(Options()).Create(new SparkEmbedHandoverRequest("/checkout", big.RootElement), out _).Should().BeNull();
        Handover(new SparkEmbedOptions { PublicOrigin = "https://tickets.example" }).Create(new SparkEmbedHandoverRequest("/checkout"), out _).Should().BeNull();
    }

    [Fact]
    public void The_loader_accepts_messages_only_from_its_iframe_and_the_platform()
    {
        var script = SparkEmbedLoaderScript.Source;
        script.Should().Contain("event.source !== frame.contentWindow || event.origin !== platform");
        script.Should().Contain("if (target.origin === platform) window.location.assign(target.href)");
        script.Should().Contain("'spark-embed:resize'");
        script.Should().Contain("'spark-embed:navigate'");
    }
}
