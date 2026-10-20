using System.Net;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Peppol.Billit;
using MintPlayer.Spark.Commerce.Invoicing.Peppol.Storecove;
using MintPlayer.Spark.Commerce.Invoicing.Peppol.Test;
using MintPlayer.Spark.Commerce.Tests.Payments;

namespace MintPlayer.Spark.Commerce.Tests.Invoicing;

/// <summary>
/// Spike S14, fixture-based (round 4): each access point against doc-derived answers (Invoicing/Fixtures). No network: a
/// fake handler serves the fixtures and records what the adapter sent.
/// </summary>
public class PeppolAdapterTests
{
    private static PeppolSendRequest Request(string issuer = "platform")
        => new("Invoices/invoice-abc", "F2026-000001", UblWriter.InvoiceDocumentType,
            UblWriter.Write(InvoicingRulesTests.BusinessInvoice("S", 2100)),
            new PeppolParticipant("0208", "0123456749"), new PeppolParticipant("0208", "0987654321"), issuer);

    [Fact]
    public async Task Storecove_submits_the_raw_UBL_routed_to_the_receiver_with_a_stable_idempotency_guid()
    {
        var handler = new FakeProviderHandler().On("POST", "/api/v2/document_submissions", InvoicingFixtures.Text("Storecove", "document_submissions.200.json"));
        var adapter = new StorecoveAccessPoint(new FakeHttpClientFactory(handler),
            new StaticOptionsMonitor<StorecoveOptions>(new StorecoveOptions { ApiKey = "test-key", LegalEntityId = 111, LegalEntities = { ["Organizations/42"] = 222 } }));

        var result = await adapter.SendAsync(Request(), CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.ProviderReference.Should().Be("c65d43d1-6a38-4c6a-8f62-1c1b2d7bd5e0");
        var sent = handler.Requests.Single();
        sent.Headers["Authorization"].Should().Be("Bearer test-key");
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("legalEntityId").GetInt64().Should().Be(111);
        body.RootElement.GetProperty("routing").GetProperty("eIdentifiers")[0].GetProperty("scheme").GetString().Should().Be("BE:EN");
        body.RootElement.GetProperty("routing").GetProperty("eIdentifiers")[0].GetProperty("id").GetString().Should().Be("0987654321");
        var raw = body.RootElement.GetProperty("document").GetProperty("rawDocumentData");
        raw.GetProperty("parseStrategy").GetString().Should().Be("ubl");
        raw.GetProperty("processId").GetString().Should().Be(UblWriter.ProfileId);
        Encoding.UTF8.GetString(Convert.FromBase64String(raw.GetProperty("document").GetString()!)).Should().Contain("F2026-000001");
        body.RootElement.GetProperty("idempotencyGuid").GetString().Should().Be(StorecoveAccessPoint.IdempotencyGuid("Invoices/invoice-abc").ToString());

        await adapter.SendAsync(Request("Organizations/42"), CancellationToken.None);
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        second.RootElement.GetProperty("legalEntityId").GetInt64().Should().Be(222);
    }

    [Fact]
    public async Task Storecove_reports_a_refusal_as_final_and_an_outage_as_retryable()
    {
        var refused = new FakeProviderHandler().On("POST", "/api/v2/document_submissions", InvoicingFixtures.Text("Storecove", "document_submissions.422.json"), (HttpStatusCode)422);
        var adapter = new StorecoveAccessPoint(new FakeHttpClientFactory(refused), new StaticOptionsMonitor<StorecoveOptions>(new StorecoveOptions { ApiKey = "k", LegalEntityId = 1 }));
        var result = await adapter.SendAsync(Request(), CancellationToken.None);
        result.Accepted.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("BR-CO-15");

        var down = new FakeProviderHandler().On("POST", "/api/v2/document_submissions", "", HttpStatusCode.ServiceUnavailable);
        var outage = await new StorecoveAccessPoint(new FakeHttpClientFactory(down), new StaticOptionsMonitor<StorecoveOptions>(new StorecoveOptions { ApiKey = "k", LegalEntityId = 1 }))
            .SendAsync(Request(), CancellationToken.None);
        outage.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Storecove_discovery_asks_for_the_receiver_in_its_own_scheme()
    {
        var handler = new FakeProviderHandler().On("POST", "/api/v2/discovery/receives", InvoicingFixtures.Text("Storecove", "discovery_receives.ok.json"));
        var adapter = new StorecoveAccessPoint(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<StorecoveOptions>(new StorecoveOptions { ApiKey = "k" }));
        (await adapter.LookupAsync(new PeppolParticipant("9925", "be0123456749"), UblWriter.InvoiceDocumentType, CancellationToken.None)).CanReceive.Should().BeTrue();
        using var body = JsonDocument.Parse(handler.Requests.Single().Body!);
        body.RootElement.GetProperty("scheme").GetString().Should().Be("BE:VAT");
        body.RootElement.GetProperty("network").GetString().Should().Be("peppol");
    }

    [Fact]
    public async Task Storecove_never_sends_the_key_over_plain_HTTP()
    {
        var adapter = new StorecoveAccessPoint(new FakeHttpClientFactory(new FakeProviderHandler()),
            new StaticOptionsMonitor<StorecoveOptions>(new StorecoveOptions { ApiKey = "k", LegalEntityId = 1, ApiBaseUrl = "http://api.storecove.com/api/v2/" }));
        await ((Func<Task>)(() => adapter.SendAsync(Request(), CancellationToken.None))).Should().ThrowAsync<PeppolException>();
    }

    [Fact]
    public async Task Billit_posts_the_UBL_as_the_XML_field_with_the_party_of_the_issuer()
    {
        var handler = new FakeProviderHandler().On("POST", "/v1/peppol/sendxml", InvoicingFixtures.Text("Billit", "sendxml.200.json"));
        var adapter = new BillitAccessPoint(new FakeHttpClientFactory(handler),
            new StaticOptionsMonitor<BillitOptions>(new BillitOptions { ApiKey = "billit-key", PartyId = "100", Parties = { ["Organizations/42"] = "200" }, ApiBaseUrl = "https://api.sandbox.billit.be/" }));

        var result = await adapter.SendAsync(Request("Organizations/42"), CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.ProviderReference.Should().Be("9876543");
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be("api.sandbox.billit.be");
        sent.Headers["PartyID"].Should().Be("200");
        sent.Headers["ApiKey"].Should().Be("billit-key");
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("XML").GetString().Should().StartWith("<?xml");
    }

    [Fact]
    public void Billit_reads_the_inbox_item_id_and_the_participant_answer_in_either_shape()
    {
        BillitAccessPoint.InboxItemId("{\"InboxItemID\":42}").Should().Be("42");
        BillitAccessPoint.InboxItemId("42").Should().Be("42");
        BillitAccessPoint.ParticipantRegistered("{\"Registered\":false}").Should().BeFalse();
        BillitAccessPoint.ParticipantRegistered("{\"Name\":\"Klant BV\"}").Should().BeTrue();
        BillitAccessPoint.ParticipantRegistered("").Should().BeFalse();
    }

    [Fact]
    public async Task The_test_access_point_records_once_per_document_and_simulates_outages_and_refusals()
    {
        var options = new TestPeppolOptions { OutagesRemaining = 1 };
        options.Refuse.Add("0208:0999999999");
        var adapter = new TestPeppolAccessPoint(options, new ManualTimeProvider(DateTimeOffset.Parse("2026-10-19T10:00:00Z")));

        (await adapter.SendAsync(Request(), CancellationToken.None)).Retryable.Should().BeTrue();
        (await adapter.SendAsync(Request(), CancellationToken.None)).Accepted.Should().BeTrue();
        (await adapter.SendAsync(Request(), CancellationToken.None)).Accepted.Should().BeTrue();
        adapter.Submissions.Should().HaveCount(1);

        var refused = Request() with { InvoiceId = "Invoices/other", Receiver = new PeppolParticipant("0208", "0999999999") };
        var answer = await adapter.SendAsync(refused, CancellationToken.None);
        answer.Accepted.Should().BeFalse();
        answer.Retryable.Should().BeFalse();
    }
}
