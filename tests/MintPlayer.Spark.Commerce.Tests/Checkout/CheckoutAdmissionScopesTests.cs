using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>A gate the test drives: owns one scope prefix, accepts one token, records every question.</summary>
internal sealed class RecordingAdmissionGate(string prefix, string acceptedToken) : ICheckoutAdmissionGate
{
    public ConcurrentQueue<AdmissionCheck> Checks { get; } = new();

    public bool Handles(string scope) => scope.StartsWith(prefix, StringComparison.Ordinal);

    public Task<AdmissionDecision> CheckAsync(AdmissionCheck check, CancellationToken cancellationToken)
    {
        Checks.Enqueue(check);
        return Task.FromResult(check.Token == acceptedToken ? AdmissionDecision.Admit : AdmissionDecision.Refuse(prefix + "refused"));
    }
}

/// <summary>
/// Combined admission scopes (item 4 of the #521 follow-ups): an item gated by a waiting room AND a presale code needs a
/// token for each, checkout consumes none until every scope admits, and a scope no gate owns is refused (fail closed).
/// </summary>
public class CheckoutAdmissionScopesTests : SparkTestDriver
{
    private const string Room = "waiting-room:phase-1";
    private const string Presale = "promotions:presale-1";

    private (CheckoutHost Host, RecordingAdmissionGate Queue, RecordingAdmissionGate Codes) Build()
    {
        var queue = new RecordingAdmissionGate("waiting-room:", "queue-token");
        var codes = new RecordingAdmissionGate("promotions:", "CODE");
        var host = CheckoutHost.Build(Store, configure: (services, _, _) =>
        {
            services.AddSingleton<ICheckoutAdmissionGate>(queue);
            services.AddSingleton<ICheckoutAdmissionGate>(codes);
        });
        return (host, queue, codes);
    }

    private static async Task<CheckoutException> Refused(Func<Task> action)
    {
        try { await action(); }
        catch (CheckoutException ex) { return ex; }
        throw new InvalidOperationException("Expected a checkout refusal.");
    }

    private static Dictionary<string, string> Tokens(string? queue, string? code)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queue is not null) tokens[Room] = queue;
        if (code is not null) tokens[Presale] = code;
        return tokens;
    }

    [Fact]
    public async Task A_queued_presale_item_is_held_with_both_tokens_and_both_are_consumed()
    {
        var (host, queue, codes) = Build();
        host.Buyer.UserId = "u1";
        await host.ItemAsync("tickets/presale", "tickets", 20m, admissionScopes: [Room, Presale]);

        var basket = await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/presale", 2, AdmissionTokens: Tokens("queue-token", "CODE")));

        basket.Lines.Should().ContainSingle();
        queue.Checks.Select(c => c.Consume).Should().Equal(false, true);
        codes.Checks.Select(c => c.Consume).Should().Equal(false, true);
        queue.Checks.Should().OnlyContain(c => c.Scope == Room && c.UserId == "u1" && c.Quantity == 2);
        codes.Checks.Should().OnlyContain(c => c.Scope == Presale && c.Token == "CODE");
    }

    [Fact]
    public async Task A_missing_presale_code_refuses_before_any_gate_consumes()
    {
        var (host, queue, codes) = Build();
        await host.ItemAsync("tickets/presale", "tickets", 20m, admissionScopes: [Room, Presale]);

        var refusal = await Refused(() => host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/presale", 1, AdmissionTokens: Tokens("queue-token", null))));

        refusal.Code.Should().Be(CheckoutRefusals.AdmissionRequired);
        queue.Checks.Should().BeEmpty();
        codes.Checks.Should().BeEmpty();
        (await host.AvailableAsync("tickets/presale")).Should().Be(10);
    }

    [Fact]
    public async Task A_refused_presale_code_does_not_spend_the_waiting_room_token()
    {
        var (host, queue, codes) = Build();
        await host.ItemAsync("tickets/presale", "tickets", 20m, admissionScopes: [Room, Presale]);

        var refusal = await Refused(() => host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/presale", 1, AdmissionTokens: Tokens("queue-token", "WRONG"))));

        refusal.Code.Should().Be(CheckoutRefusals.AdmissionRequired);
        queue.Checks.Should().OnlyContain(c => !c.Consume);
        codes.Checks.Should().OnlyContain(c => !c.Consume);
        (await host.AvailableAsync("tickets/presale")).Should().Be(10);
    }

    [Fact]
    public async Task A_scope_no_gate_owns_is_refused()
    {
        var (host, _, _) = Build();
        await host.ItemAsync("tickets/other", "tickets", 20m, admissionScopes: ["elsewhere:x"]);

        (await Refused(() => host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/other", 1,
            AdmissionTokens: new Dictionary<string, string> { ["elsewhere:x"] = "t" })))).Code.Should().Be(CheckoutRefusals.AdmissionRequired);
    }

    [Fact]
    public async Task Tokens_of_other_scopes_are_not_kept_on_the_line()
    {
        var (host, _, _) = Build();
        await host.ItemAsync("tickets/queued", "tickets", 20m, admissionScopes: [Room]);

        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/queued", 1, AdmissionTokens: Tokens("queue-token", "CODE")));

        using var session = Store.OpenAsyncSession();
        var basket = await session.Query<CheckoutBasket>().Customize(c => c.WaitForNonStaleResults()).SingleAsync();
        basket.Lines.Single().AdmissionTokens.Keys.Should().Equal(Room);
    }
}
