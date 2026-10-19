using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MimeKit;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #521 item 3 — MailManager attachments (inline bytes and RavenDB attachments read at send) and custom headers
/// (the consent primitive's List-Unsubscribe pair, header-injection refusal). Every type used here is local to this file.
/// </summary>
public class MailAttachmentTests : SparkTestDriver
{
    private string contentRoot = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        contentRoot = Directory.CreateTempSubdirectory("spark-mail-att-").FullName;
        var full = Path.Combine(contentRoot, "Templates", "Mail", "News.mjml");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "<mjml><mj-head><mj-title>News</mj-title></mj-head><mj-body><mj-section><mj-column><mj-text>Hello</mj-text></mj-column></mj-section></mj-body></mjml>");
    }

    public override async Task DisposeAsync()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private ServiceProvider Build(Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Mail:From:Address"] = "noreply@app.example",
            ["Spark:Mail:PickupFolder"] = "pickup",
            ["Spark:Mail:DefaultCulture"] = "en",
            ["Spark:Mail:PublicBaseUrl"] = "https://app.example",
        }.Concat(config ?? []).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddSingleton<IHostEnvironment>(new HostEnvironment(contentRoot));
        collection.AddSingleton<Raven.Client.Documents.IDocumentStore>(Store);
        collection.AddDataProtection().UseEphemeralDataProtectionProvider();
        collection.AddSingleton<Bus>();
        collection.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<Bus>());
        collection.AddSingleton(TimeProvider.System);
        new SparkBuilder(collection, configuration).AddMailManager();
        return collection.BuildServiceProvider();
    }

    private static SparkMailHandler Handler(IServiceProvider sp) => ActivatorUtilities.CreateInstance<SparkMailHandler>(sp);

    private static SparkMailMessage Message(params SparkMailMessageAttachment[] attachments) => new()
    {
        DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "ann@app.example", Culture = "en", Stream = "news",
        Attachments = attachments.Length == 0 ? null : [.. attachments],
    };

    // ---- queue time --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_inline_attachment_is_queued_and_protected_when_the_mail_is_sensitive()
    {
        using var sp = Build();
        using var scope = sp.CreateScope();
        var mailer = scope.ServiceProvider.GetRequiredService<SparkMailer>();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 receipt");

        await mailer.SendAsync(new SparkMailRequest { Template = "News", To = "ann@app.example", Attachments = [SparkMailAttachment.FromBytes("receipt.pdf", "application/pdf", bytes)] });
        await mailer.SendAsync(new SparkMailRequest { Template = "News", To = "ann@app.example", Sensitive = true, Attachments = [SparkMailAttachment.FromBytes("invoice.pdf", "application/pdf", bytes)] });

        var published = sp.GetRequiredService<Bus>().Published.Cast<SparkMailMessage>().ToList();
        published[0].Attachments!.Single().Content.Should().Be(Convert.ToBase64String(bytes));
        published[1].Attachments!.Single().Content.Should().StartWith("sdp1:", "a sensitive mail's files are protected like its data");
        published[1].Attachments!.Single().Content.Should().NotContain(Convert.ToBase64String(bytes));
    }

    [Theory]
    [InlineData("Subject", "x", "set by MailManager")]
    [InlineData("Bcc", "eve@evil.example", "set by MailManager")]
    [InlineData("X-Tag", "a\r\nBcc: eve@evil.example", "line break")]
    [InlineData("Bad Name", "x", "not a header field name")]
    [InlineData("X:Colon", "x", "not a header field name")]
    public async Task A_reserved_or_injected_header_is_refused_at_queue_time(string name, string value, string reason)
    {
        using var sp = Build();
        using var scope = sp.CreateScope();
        var mailer = scope.ServiceProvider.GetRequiredService<SparkMailer>();

        var act = () => mailer.SendAsync(new SparkMailRequest { Template = "News", To = "ann@app.example", Headers = new Dictionary<string, string> { [name] = value } });

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(reason);
        sp.GetRequiredService<Bus>().Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_attachment_needs_exactly_one_source_a_plain_name_and_fits_the_inline_cap()
    {
        using var sp = Build(new() { ["Spark:Mail:Attachments:MaxInlineBytes"] = "10" });
        using var scope = sp.CreateScope();
        var mailer = scope.ServiceProvider.GetRequiredService<SparkMailer>();
        Task Send(SparkMailAttachment a) => mailer.SendAsync(new SparkMailRequest { Template = "News", To = "ann@app.example", Attachments = [a] });

        await ((Func<Task>)(() => Send(new SparkMailAttachment { FileName = "x.pdf" }))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => Send(new SparkMailAttachment { FileName = "x.pdf", Content = [1], DocumentId = "Docs/1", AttachmentName = "a" }))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => Send(SparkMailAttachment.FromBytes("../etc/x.pdf", "application/pdf", [1])))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => Send(SparkMailAttachment.FromBytes("big.pdf", "application/pdf", new byte[11])))).Should().ThrowAsync<ArgumentException>();
        await Send(SparkMailAttachment.FromBytes("ok.pdf", "application/pdf", new byte[10]));
        sp.GetRequiredService<Bus>().Published.Should().HaveCount(1);
    }

    // ---- send time ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inline_and_stored_attachments_are_both_on_the_sent_mail()
    {
        using (var session = Store.OpenAsyncSession())
        {
            var doc = new StoredDoc { Id = "StoredDocs/1" };
            await session.StoreAsync(doc);
            session.Advanced.Attachments.Store(doc, "document.pdf", new MemoryStream(Encoding.ASCII.GetBytes("%PDF stored")), "application/pdf");
            await session.SaveChangesAsync();
        }
        using var sp = Build();
        var message = Message(
            new SparkMailMessageAttachment { FileName = "terms-v3.pdf", ContentType = "application/pdf", DocumentId = "StoredDocs/1", AttachmentName = "document.pdf" },
            new SparkMailMessageAttachment { FileName = "receipt.pdf", ContentType = "application/pdf", Content = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF inline")) });

        var mime = await Handler(sp).BuildAsync(message, default);

        var files = mime.Attachments.OfType<MimePart>().ToList();
        files.Select(f => f.FileName).Should().Equal(["terms-v3.pdf", "receipt.pdf"]);
        files.Should().OnlyContain(f => f.ContentType.MimeType == "application/pdf");
        Read(files[0]).Should().Be("%PDF stored");
        Read(files[1]).Should().Be("%PDF inline");
    }

    [Fact]
    public async Task A_missing_required_stored_attachment_fails_the_attempt_so_it_is_retried_and_an_optional_one_is_left_out()
    {
        using var sp = Build();
        var required = Message(new SparkMailMessageAttachment { FileName = "terms.pdf", DocumentId = "StoredDocs/none", AttachmentName = "document.pdf" });
        var optional = Message(new SparkMailMessageAttachment { FileName = "terms.pdf", DocumentId = "StoredDocs/none", AttachmentName = "document.pdf", Optional = true });

        var act = () => Handler(sp).BuildAsync(required, default);
        // InvalidOperationException, not NonRetryableException: the queue retries it.
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Handler(sp).BuildAsync(optional, default)).Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mail_over_the_total_cap_is_failed_without_retry()
    {
        using var sp = Build(new() { ["Spark:Mail:Attachments:MaxTotalBytes"] = "4" });
        var message = Message(new SparkMailMessageAttachment { FileName = "a.pdf", Content = Convert.ToBase64String(new byte[5]) });

        var act = () => Handler(sp).HandleAsync(message);

        await act.Should().ThrowAsync<NonRetryableException>();
    }

    [Fact]
    public async Task A_custom_List_Unsubscribe_pair_replaces_the_stream_one()
    {
        using var sp = Build();
        var message = Message();
        message.ListUnsubscribe = true;
        message.Headers = new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = "<https://app.example/spark/consent/unsubscribe/one-click?token=abc>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            ["X-Campaign-Kind"] = "reminder",
        };

        var mime = await Handler(sp).BuildAsync(message, default);

        mime.Headers.Where(h => h.Id == HeaderId.ListUnsubscribe).Should().ContainSingle();
        mime.Headers[HeaderId.ListUnsubscribe].Should().Contain("/spark/consent/unsubscribe/one-click?token=abc");
        mime.Headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        mime.Headers["X-Campaign-Kind"].Should().Be("reminder");
    }

    [Fact]
    public async Task Without_custom_headers_the_stream_List_Unsubscribe_stays()
    {
        using var sp = Build();
        var message = Message();
        message.ListUnsubscribe = true;

        var mime = await Handler(sp).BuildAsync(message, default);

        mime.Headers[HeaderId.ListUnsubscribe].Should().Contain("/spark/mail/unsubscribe?t=");
    }

    private static string Read(MimePart part)
    {
        using var buffer = new MemoryStream();
        part.Content.DecodeTo(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private sealed class StoredDoc
    {
        public string? Id { get; set; }
    }

    private sealed class HostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "MailAttachmentTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Bus : IMessageBus
    {
        public List<object> Published { get; } = [];
        public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
        {
            Published.Add(message!);
            return Task.CompletedTask;
        }
    }
}
