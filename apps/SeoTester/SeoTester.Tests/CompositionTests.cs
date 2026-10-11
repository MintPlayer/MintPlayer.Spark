using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Quotas;
using MintPlayer.Spark.Sharing;
using SeoTester.Messaging;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// The skeleton's composition (<c>docs/seo_analyzer_PRD.md</c> §4.1, M7). That the host boots at all is the first
/// assertion: Spark validates the model hashes, every library slot binding, the row-policy declarations, the mail
/// transport, ConfirmByEmail's sender and the reachability of sign-in before it serves a request.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class CompositionTests(SeoTesterHostFixture host)
{
    [Fact]
    public void Sign_in_is_external_only_and_a_second_provider_links_by_mailed_confirmation()
    {
        var auth = host.Factory.Services.GetRequiredService<IOptions<SparkAuthenticationOptions>>().Value;

        auth.LocalCredentials.Should().Be(SparkLocalCredentials.Disabled, "D2: no passwords");
        auth.ExternalLoginLinking.Should().Be(SparkExternalLoginLinking.ConfirmByEmail, "D7");
    }

    [Fact]
    public async Task The_sign_in_page_offers_exactly_Microsoft_GitHub_X_and_Google_and_no_password_form()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync("/spark/auth/capabilities");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("localCredentials").GetString().Should().Be("Disabled");
        body.GetProperty("externalProviders").EnumerateArray().Select(p => p.GetProperty("scheme").GetString()).OrderBy(s => s)
            .Should().Equal("GitHub", "Google", "Microsoft", "Twitter");
    }

    [Fact]
    public void Every_gate_and_limit_of_the_PRD_is_a_declared_setting_and_the_run_limits_are_quotas()
    {
        var catalog = host.Factory.Services.GetRequiredService<SparkSettingsCatalog>();
        var keys = catalog.All.Select(s => s.Key).ToHashSet();

        var declared = typeof(SeoSettings).GetFields().Select(f => ((ISparkSetting)f.GetValue(null)!).Key).ToList();
        declared.Should().NotBeEmpty();
        string[] expected =
        [
            .. declared,
            "SeoTester:Features:Monitoring", "SeoTester:Features:IntrusiveProbes", "SeoTester:Features:DeepCrawl", "SeoTester:Features:Sharing",
            "SeoTester:Quotas:Audits:Limit", "SeoTester:Quotas:VerifiedAudits:Limit", "SeoTester:Quotas:FullCrawls:Window",
        ];
        expected.Where(k => !keys.Contains(k)).Should().BeEmpty("every key is declared through AddSettings / AddQuotas");
        host.Factory.Services.GetRequiredService<ISparkQuotas>().Should().NotBeNull();
    }

    [Fact]
    public void The_crawl_queues_are_declared_with_a_handler_timeout_each()
    {
        var messaging = host.Factory.Services.GetRequiredService<IOptions<SparkMessagingOptions>>().Value;

        foreach (var queue in SeoQueues.All)
        {
            messaging.Queues.Should().ContainKey(queue);
            messaging.HandlerTimeoutFor(queue).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
        }
        messaging.Queues[SeoQueues.Render].MaxConcurrency.Should().Be(1, "one Chromium render at a time (§6)");
    }

    [Fact]
    public void Organizations_notifications_and_the_sharing_switch_are_composed()
    {
        using var scope = host.Factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISparkOrganizations>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ISparkNotifier>().Should().NotBeNull();
        scope.ServiceProvider.GetServices<ISparkShareGate>().Should().ContainSingle(g => g is SharingSwitch);
    }
}
