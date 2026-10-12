using HR.Entities;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Xunit.Abstractions;

namespace HR.Tests.Mcp;

/// <summary>
/// Spike S9 (MCP PRD §6.1, D10; plan M7) on HR: the largest realistic call of every generic tool, on a production-sized
/// HR database, answers in less than 10k tokens with the tools' defaults. Tokens are estimated as characters / 4 (no
/// Claude tokenizer runs offline); the bound asserted is <see cref="Budget"/> characters of the whole answer text, fence
/// and note included. The sizes are written to the test output for docs/spark_mcp_spikes.md (S9).
/// </summary>
/// <remarks>
/// The data: 60 professions, 300 companies, 2,000 people with an address, 1–3 professions and 5–40 jobs each (a career of
/// forty contracts is the long tail), every text at the length the model allows or a realistic maximum.
/// </remarks>
public class McpOutputBudgetTests(ITestOutputHelper output) : SparkTestDriver
{
    /// <summary>10k tokens at 4 characters a token: Claude Code warns above 10k tokens of tool output (D10).</summary>
    private const int Budget = 40_000;

    [Fact]
    public async Task The_largest_realistic_call_of_every_generic_tool_fits_the_output_budget()
    {
        await using var host = new HrAgentHost(Store);
        _ = host.Services;
        await SeedProductionSizeAsync();
        await host.CreateUserAsync("budget@hr.test", HrAgentHost.Managers);
        var web = await host.SignInAsync("budget@hr.test");
        var token = await web.CreateTokenAsync();
        // Every list once (by alias, as discover names them), so the auto indexes exist and are current.
        foreach (var query in new[] { "people", "companies", "professions" })
            (await web.JsonAsync(HttpMethod.Post, "/spark/queries/execute", new { queryId = query, take = 1 })).Status.Should().Be(System.Net.HttpStatusCode.OK, query);
        await Store.WaitForIndexingAsync();

        var calls = new (string Label, string Tool, object Arguments)[]
        {
            ("discover (defaults)", "hr_discover", new { }),
            ("describe Person", "hr_describe", new { type = "Person" }),
            ("query people (defaults: 25 rows)", "hr_query", new { query = "people" }),
            ("query people (take 100, the maximum)", "hr_query", new { query = "people", take = 100 }),
            ("query companies (defaults)", "hr_query", new { query = "companies" }),
            ("get Person with 40 jobs (defaults: rows counted)", "hr_get", new { type = "Person", id = "People/budget-0000" }),
            ("get Person with 40 jobs, include Jobs", "hr_get", new { type = "Person", id = "People/budget-0000", include = new[] { "Jobs" } }),
            ("get Company", "hr_get", new { type = "Company", id = "Companies/budget-000" }),
            ("propose_changes, 50 updates (the maximum)", "hr_propose_changes", new
            {
                summary = "Rename fifty people",
                operations = Enumerable.Range(1, 50).Select(i => new
                {
                    kind = "update", type = "Person", id = $"People/budget-{i:D4}",
                    values = new { FirstName = new string('F', 50), LastName = new string('L', 50) },
                }).ToArray(),
            }),
        };

        var failures = new List<string>();
        foreach (var (label, tool, arguments) in calls)
        {
            var reply = await HrMcp.CallAsync(host.CreateClient(), tool, arguments, token);
            output.WriteLine($"S9 HR | {label} | {reply.Text.Length} chars | ~{(reply.Text.Length + 3) / 4} tokens{(reply.IsToolError ? " | ERROR " + reply.Text : "")}");
            if (reply.IsToolError || reply.Error is not null)
                failures.Add($"{label}: {reply.Error ?? reply.Text}");
            else if (reply.Text.Length >= Budget || reply.Text.Contains("(truncated", StringComparison.Ordinal))
                failures.Add($"{label}: {reply.Text.Length} characters");
        }

        failures.Should().BeEmpty("every default page stays under 10k tokens (S9)");
    }

    /// <summary>A production-sized HR database, written with one bulk insert.</summary>
    private async Task SeedProductionSizeAsync()
    {
        var random = new Random(9);
        string Text(int length) => new(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());

        await using var bulk = Store.BulkInsert();
        for (var i = 0; i < 60; i++)
            await bulk.StoreAsync(new Profession { Description = $"Profession {i:D2} {Text(40)}", Regime = i % 3 == 0 ? "Freelance" : "Full-time" }, $"Professions/budget-{i:D2}");
        for (var i = 0; i < 300; i++)
        {
            await bulk.StoreAsync(new Company
            {
                Name = $"Company {i:D3} {Text(60)}",
                Website = $"https://company-{i:D3}.example/{Text(30)}",
                EmployeeCount = 10 + i * 37,
                BrandColor = "#0a1b2c",
                AccentColor = "#d4e5f6",
                Sector = $"Professions/budget-{i % 60:D2}",
            }, $"Companies/budget-{i:D3}");
        }
        for (var i = 0; i < 2000; i++)
        {
            var jobs = i == 0 ? 40 : 5 + random.Next(36);
            await bulk.StoreAsync(new Person
            {
                FirstName = $"First{i:D4} {Text(30)}",
                LastName = $"Last{i:D4} {Text(30)}",
                Email = $"person{i:D4}.{Text(20)}@example.test",
                DateOfBirth = new DateOnly(1960 + i % 40, 1 + i % 12, 1 + i % 28),
                Company = $"Companies/budget-{i % 300:D3}",
                Professions = [.. Enumerable.Range(0, 1 + i % 3).Select(p => $"Professions/budget-{(i + p) % 60:D2}")],
                Address = new Address
                {
                    Street = $"Voorbeeldstraat {i % 200 + 1} {Text(40)}",
                    PostalCode = $"{1000 + i % 9000}",
                    City = $"Stad {Text(30)}",
                    Description = new AddressDescription { Title = Text(40), Notes = Text(200) },
                },
                Jobs = [.. Enumerable.Range(0, jobs).Select(j => new CarreerJob
                {
                    ProfessionId = $"Professions/budget-{(i + j) % 60:D2}",
                    ContractStart = new DateOnly(1990 + j % 30, 1 + j % 12, 1),
                    ContractEnd = j == jobs - 1 ? null : new DateOnly(1991 + j % 30, 1 + j % 12, 1),
                })],
            }, $"People/budget-{i:D4}");
        }
    }
}
