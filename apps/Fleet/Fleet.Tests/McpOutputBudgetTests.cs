using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Xunit.Abstractions;

namespace Fleet.Tests;

/// <summary>
/// Spike S9 (MCP PRD §6.1, D10; plan M7) on Fleet: the largest realistic call of every generic tool, on a production-sized
/// fleet, answers in less than 10k tokens with the tools' defaults. Tokens are estimated as characters / 4 (no Claude
/// tokenizer runs offline); the bound asserted is <see cref="Budget"/> characters of the whole answer text, fence and note
/// included. The sizes are written to the test output for docs/spark_mcp_spikes.md (S9).
/// </summary>
/// <remarks>
/// The data: 500 cars of one viewer (Fleet's row filter shows a viewer the cars they created), each with a description in
/// three languages and a manager, 200 managers, 50 companies; one car with a service history of 120 entries (a decade of
/// monthly services: the long tail of <c>get</c>, which returns detail rows inline).
/// </remarks>
public class McpOutputBudgetTests(ITestOutputHelper output) : SparkTestDriver
{
    /// <summary>10k tokens at 4 characters a token: Claude Code warns above 10k tokens of tool output (D10).</summary>
    private const int Budget = 40_000;

    [Fact]
    public async Task The_largest_realistic_call_of_every_generic_tool_fits_the_output_budget()
    {
        await using var host = new FleetBudgetHost(Store);
        var (userId, token) = await CreateViewerWithTokenAsync(host);
        await SeedProductionSizeAsync(userId);
        using (var warm = host.CreateClient())
        {
            foreach (var query in new[] { "cars", "people", "companies" })
                await CallAsync(warm, "fleet_query", new { query, take = 1 }, token);
        }
        await Store.WaitForIndexingAsync();

        var calls = new (string Label, string Tool, object Arguments)[]
        {
            ("discover (defaults)", "fleet_discover", new { }),
            ("describe Car", "fleet_describe", new { type = "Car" }),
            ("query cars (defaults: 25 rows)", "fleet_query", new { query = "cars" }),
            ("query cars (take 100, the maximum)", "fleet_query", new { query = "cars", take = 100 }),
            ("query people (defaults)", "fleet_query", new { query = "people" }),
            ("get Car with 120 service entries (defaults: rows counted)", "fleet_get", new { type = "Car", id = "cars/budget-000" }),
            ("get Car with 120 service entries, include ServiceEntries (rows as many as fit)", "fleet_get", new { type = "Car", id = "cars/budget-000", include = new[] { "ServiceEntries" } }),
        };

        var failures = new List<string>();
        foreach (var (label, tool, arguments) in calls)
        {
            using var client = host.CreateClient();
            var (text, isError) = await CallAsync(client, tool, arguments, token);
            output.WriteLine($"S9 Fleet | {label} | {text.Length} chars | ~{(text.Length + 3) / 4} tokens{(isError ? " | ERROR " + text : "")}");
            if (isError)
                failures.Add($"{label}: {text}");
            else if (text.Length >= Budget || text.Contains("(truncated", StringComparison.Ordinal))
                failures.Add($"{label}: {text.Length} characters");
        }

        failures.Should().BeEmpty("every default page stays under 10k tokens (S9)");

        // The history is counted by default and, on request, given as many rows as fit, never cut mid-row.
        using var check = host.CreateClient();
        var counted = Unfence((await CallAsync(check, "fleet_get", new { type = "Car", id = "cars/budget-000" }, token)).Text);
        counted["attributes"]!["ServiceEntries"]!["count"]!.GetValue<int>().Should().Be(120);
        var included = Unfence((await CallAsync(check, "fleet_get", new { type = "Car", id = "cars/budget-000", include = new[] { "ServiceEntries" } }, token)).Text);
        var shown = included["attributes"]!["ServiceEntries"]!["rows"]!.AsArray().Count;
        shown.Should().BeGreaterThan(50);
        (shown + included["attributes"]!["ServiceEntries"]!["omitted"]!.GetValue<int>()).Should().Be(120, "omitted says how many more there are");
        output.WriteLine($"S9 Fleet | include ServiceEntries | {shown} of 120 rows shown");
    }

    private static JsonNode Unfence(string text)
    {
        var start = text.IndexOf("<data-", StringComparison.Ordinal) + 1;
        var tag = text[start..text.IndexOf('>', start)];
        var open = text.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
        var close = text.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
        return JsonNode.Parse(text[open..close])!;
    }

    /// <summary>A viewer, and a personal access token of theirs for everything Fleet exposes, read only, as the account page makes one.</summary>
    private async Task<(string UserId, string Token)> CreateViewerWithTokenAsync(FleetBudgetHost host)
    {
        string userId, stamp;
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
            var user = new SparkUser { UserName = "budget", Email = "budget@fleet.test", EmailConfirmed = true };
            (await users.CreateAsync(user, "Correct-horse-battery-1")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, "Viewers")).Succeeded.Should().BeTrue();
            userId = user.Id!;
            stamp = (await users.FindByIdAsync(userId))!.SecurityStamp!;
        }

        var tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var token = $"spat_{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(userId))}.{tokenId}.{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new AgentGrant
        {
            UserId = userId,
            Kind = EAgentKind.PersonalAccessToken,
            ClientId = tokenId,
            Name = "budget",
            Permissions = EAgentPermission.Read,
            Types = null,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            SecurityStamp = stamp,
        }, AgentGrantIds.PersonalAccessToken(userId, tokenId));
        await session.SaveChangesAsync();
        return (userId, token);
    }

    private async Task SeedProductionSizeAsync(string userId)
    {
        var random = new Random(9);
        string Text(int length) => new(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());

        await using var bulk = Store.BulkInsert();
        for (var i = 0; i < 50; i++)
            await bulk.StoreAsync(new Fleet.Replicated.Company { Name = $"Company {i:D2} {Text(60)}", Website = $"https://company-{i:D2}.example", EmployeeCount = 100 + i }, $"Companies/budget-{i:D2}");
        for (var i = 0; i < 200; i++)
            await bulk.StoreAsync(new Fleet.Replicated.Person { FirstName = $"First{i:D3} {Text(30)}", LastName = $"Last{i:D3} {Text(30)}", Email = $"manager{i:D3}@example.test" }, $"People/budget-{i:D3}");
        for (var i = 0; i < 500; i++)
        {
            await bulk.StoreAsync(new Car
            {
                LicensePlate = $"1-BGT-{i:D3}",
                Model = $"Model {Text(40)}",
                Year = 2000 + i % 26,
                RegisteredAt = new DateTimeOffset(2020, 1 + i % 12, 1, 9, 0, 0, TimeSpan.FromHours(1)),
                PromoVideoUrl = $"https://video.example/{Text(60)}",
                PoliceReportNumber = $"PR-{Text(20)}",
                Manager = $"People/budget-{i % 200:D3}",
                Description = TranslatedString.Of(("en", Text(300)), ("fr", Text(300)), ("nl", Text(300))),
                CreatedBy = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                ServiceEntries = i == 0
                    ? [.. Enumerable.Range(0, 120).Select(s => new ServiceEntry
                    {
                        Description = $"Service {s:D3}: {Text(200)}",
                        PerformedOn = new DateOnly(2016 + s / 12, 1 + s % 12, 15),
                        Odometer = 1000 * (s + 1),
                        Cost = 120.50m + s,
                        IsInvoiced = s % 2 == 0,
                    })]
                    : [],
            }, $"cars/budget-{i:D3}");
        }
    }

    private static async Task<(string Text, bool IsError)> CallAsync(HttpClient client, string tool, object arguments, string token)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..];
        var node = json is null ? null : JsonNode.Parse(json);
        var result = node?["result"];
        return (result?["content"]?[0]?["text"]?.GetValue<string>() ?? node?["error"]?.ToJsonString() ?? text,
            result is null || result["isError"]?.GetValue<bool>() == true);
    }

    /// <summary>Fleet's real <c>Program</c> with personal access tokens only (no issuer: no embedded server), no background services.</summary>
    private sealed class FleetBudgetHost(IDocumentStore store) : WebApplicationFactory<FleetContext>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not Development: that would start the Angular dev server (UseAngularCliServer).
            builder.UseEnvironment("FleetBudgetTest");
            builder.UseContentRoot(AppDirectory());
            var settings = new Dictionary<string, string?>
            {
                ["Spark:RavenDb:Urls:0"] = store.Urls[0],
                ["Spark:RavenDb:Database"] = store.Database,
                ["Spark:DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), $"fleet-budget-tests-dataprotection-{Environment.ProcessId}"),
                ["Spark:HttpsRedirection"] = "false",
                ["Spark:Replication:ModuleUrl"] = "http://localhost",
                ["Spark:Replication:SparkModulesUrls:0"] = store.Urls[0],
                ["Spark:Replication:SparkModulesDatabase"] = store.Database + "-modules",
                ["Spark:Mcp:CanonicalUrl"] = "https://fleet.example/mcp",
            };
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name != "GenericWebHostService").ToList())
                    services.Remove(descriptor);
            });
        }

        private static string AppDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No nx.json above the test output."), "apps", "Fleet", "Fleet");
        }
    }
}
