using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.MailManager.Bounces;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.MailManager.Messaging;
using MintPlayer.Spark.MailManager.Templates;
using MintPlayer.Spark.MailManager.Transports;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Mail;

/// <summary>
/// #460 M8 — MailManager: the owner's multi-language template rules (fallback order, explicit culture
/// over the recipient's preference, the culture surviving a retry, an app file over an embedded
/// default), payload protection, suppression, the SMTP failure classification, VERP, List-Unsubscribe,
/// campaign fan-out, bounces and the startup refusals. Every type used here is local to this file.
/// </summary>
public class MailManagerTests : SparkTestDriver
{
    private string contentRoot = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        contentRoot = Directory.CreateTempSubdirectory("spark-mail-").FullName;
    }

    public override async Task DisposeAsync()
    {
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { }
        await base.DisposeAsync();
    }

    private void WriteTemplate(string path, string subject, string body = "Hello")
    {
        var full = Path.Combine(contentRoot, "Templates", "Mail", path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"<mjml><mj-head><mj-title>{subject}</mj-title></mj-head><mj-body><mj-section><mj-column><mj-text>{body}</mj-text></mj-column></mj-section></mj-body></mjml>");
    }

    private ServiceProvider Build(Action<IServiceCollection>? services = null, Dictionary<string, string?>? config = null, bool withAuthTemplates = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spark:Mail:From:Address"] = "noreply@app.example",
            ["Spark:Mail:From:Name"] = "App",
            ["Spark:Mail:PickupFolder"] = "pickup",
            ["Spark:Mail:DefaultCulture"] = "en",
            ["Spark:Mail:PublicBaseUrl"] = "https://app.example",
        }.Concat(config ?? []).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddSingleton<IHostEnvironment>(new TestHostEnvironment(contentRoot));
        collection.AddSingleton<Raven.Client.Documents.IDocumentStore>(Store);
        collection.AddDataProtection().UseEphemeralDataProtectionProvider();
        collection.AddSingleton<RecordingBus>();
        collection.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RecordingBus>());
        collection.AddSingleton(TimeProvider.System);
        new SparkBuilder(collection, configuration).AddMailManager();
        if (withAuthTemplates)
            collection.AddSparkMailTemplates(typeof(SparkUser).Assembly, "SparkMail/");
        services?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    // ---- templates: the owner's culture rules ---------------------------------------------------------

    [Fact]
    public void The_culture_chain_falls_back_from_region_to_language_to_neutral()
    {
        WriteTemplate("Welcome.mjml", "neutral");
        WriteTemplate("Welcome.nl.mjml", "nl");
        WriteTemplate("Welcome.nl-BE.mjml", "nl-BE");
        WriteTemplate("Other.mjml", "other neutral");
        using var sp = Build();
        var resolver = sp.GetRequiredService<ISparkMailTemplateResolver>();

        resolver.Resolve("Welcome", ".mjml", CultureInfo.GetCultureInfo("nl-BE"))!.Culture.Should().Be("nl-BE");
        resolver.Resolve("Welcome", ".mjml", CultureInfo.GetCultureInfo("nl-NL"))!.Culture.Should().Be("nl", "nl-NL has no file, its parent has");
        resolver.Resolve("Welcome", ".mjml", CultureInfo.GetCultureInfo("fr-BE"))!.Culture.Should().Be(string.Empty, "no French file: the neutral one");
        resolver.Resolve("Other", ".mjml", CultureInfo.GetCultureInfo("nl-BE"))!.Path.Should().Be("Other.mjml");
        resolver.TemplateNames.Should().Equal(["Other", "Welcome"]);
    }

    [Fact]
    public async Task The_subject_and_text_parts_follow_the_same_chain()
    {
        WriteTemplate("Welcome.mjml", "Welcome");
        WriteTemplate("Welcome.nl.mjml", "Welkom {{ name }}", "Hello {{ name }}");
        File.WriteAllText(Path.Combine(contentRoot, "Templates", "Mail", "Welcome.nl.txt"), "Tekst voor {{ name }}");
        using var sp = Build();

        var mail = await sp.GetRequiredService<SparkMailRenderer>().RenderAsync("Welcome", CultureInfo.GetCultureInfo("nl-BE"), JsonSerializer.SerializeToElement(new { name = "Ann & Bob" }));

        mail.Subject.Should().Be("Welkom Ann & Bob", "the subject is the rendered, decoded <mj-title>");
        mail.Text.Should().Be("Tekst voor Ann & Bob", "the .txt part gets the unescaped values");
        mail.Html.Should().Contain("Hello Ann &amp; Bob");
    }

    [Fact]
    public async Task An_app_file_overrides_the_embedded_default_with_the_same_name()
    {
        WriteTemplate("SparkAuth/PasswordReset.mjml", "App reset {{ link }}", "custom body");
        using var sp = Build(withAuthTemplates: true);
        var renderer = sp.GetRequiredService<SparkMailRenderer>();
        var data = SparkMailRenderer.WithAppName(JsonSerializer.Serialize(new { link = "L", valid_for_hours = 24 }), "Demo");

        var overridden = await renderer.RenderAsync(SparkAuthMailTemplates.PasswordReset, CultureInfo.GetCultureInfo("en"), data);
        var embeddedNl = await renderer.RenderAsync(SparkAuthMailTemplates.PasswordReset, CultureInfo.GetCultureInfo("nl"), data);

        overridden.Subject.Should().Be("App reset L");
        overridden.Template.Source.Should().Contain("folder");
        embeddedNl.Subject.Should().Be("Stel je wachtwoord opnieuw in voor Demo", "the app has no nl file, so the shipped nl one is used");
        embeddedNl.Template.Source.Should().Contain("MintPlayer.Spark.Authorization");
    }

    [Fact]
    public async Task Numbers_and_dates_are_formatted_in_the_mails_culture()
    {
        WriteTemplate("Invoice.mjml", "Invoice", "{{ amount | math.format 'N2' }} {{ due | date.to_string '%d %B %Y' }}");
        using var sp = Build();
        var renderer = sp.GetRequiredService<SparkMailRenderer>();
        var data = JsonSerializer.SerializeToElement(new { amount = 1234.5m, due = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc) });

        (await renderer.RenderAsync("Invoice", CultureInfo.GetCultureInfo("nl-BE"), data)).Html.Should().Contain("1.234,50 05 januari 2026");
        (await renderer.RenderAsync("Invoice", CultureInfo.GetCultureInfo("en-US"), data)).Html.Should().Contain("1,234.50 05 January 2026");
    }

    [Fact]
    public async Task An_explicit_culture_beats_the_recipients_preference_which_beats_the_default()
    {
        using var sp = Build(s => s.AddSingleton<ISparkMailRecipientCulture>(new FixedCulture("ann@app.example", "fr-BE")));
        using var scope = sp.CreateScope();
        var mailer = scope.ServiceProvider.GetRequiredService<SparkMailer>();

        (await mailer.ResolveCultureAsync("nl-BE", "ann@app.example", default)).Should().Be("nl-BE");
        (await mailer.ResolveCultureAsync(null, "ann@app.example", default)).Should().Be("fr-BE");
        (await mailer.ResolveCultureAsync(null, "bob@app.example", default)).Should().Be("en", "Spark:Mail:DefaultCulture");
    }

    [Fact]
    public async Task The_culture_resolved_at_queue_time_survives_a_retry()
    {
        WriteTemplate("Welcome.mjml", "Welcome");
        WriteTemplate("Welcome.fr.mjml", "Bienvenue");
        var preference = new FixedCulture("ann@app.example", "fr-BE");
        var transport = new FlakyTransport(failures: 1);
        using var sp = Build(s =>
        {
            s.AddSingleton<ISparkMailRecipientCulture>(preference);
            s.Replace(ServiceDescriptor.Singleton<ISparkMailTransport>(transport));
        });

        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISparkMailer>().SendAsync(new SparkMailRequest { Template = "Welcome", To = "ann@app.example" });
        var queued = (SparkMailMessage)sp.GetRequiredService<RecordingBus>().Published.Single().Message;
        queued.Culture.Should().Be("fr-BE", "resolved when queued and stored on the message");

        preference.Culture = "nl-BE"; // changed between attempts
        var handler = Handler(sp);
        var first = () => handler.HandleAsync(queued);
        await first.Should().ThrowAsync<IOException>("the first attempt fails and is retried by messaging");
        await handler.HandleAsync(queued);

        transport.Sent.Single().Subject.Should().Be("Bienvenue", "the retry renders the language stored on the message");
    }

    [Fact]
    public void Startup_refuses_a_template_without_a_neutral_file_and_a_template_that_does_not_parse()
    {
        WriteTemplate("OnlyDutch.nl.mjml", "Hallo");
        using var sp = Build();

        var problems = SparkMailStartup.ValidateTemplates(sp.GetRequiredService<ISparkMailTemplateResolver>(), sp.GetRequiredService<SparkMailRenderer>()).ToList();
        problems.Should().ContainSingle().Which.Should().Contain("OnlyDutch");

        WriteTemplate("Broken.mjml", "Broken", "{{ if }}");
        using var broken = Build();
        var act = () => SparkMailStartup.ValidateTemplates(broken.GetRequiredService<ISparkMailTemplateResolver>(), broken.GetRequiredService<SparkMailRenderer>()).ToList();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Broken.mjml*");
    }

    [Fact]
    public void Startup_refuses_no_transport_two_transports_no_sender_and_a_short_bounce_secret()
    {
        SparkMailStartup.Problems(new SparkMailOptions(), customTransport: false, messaging: true)
            .Should().Contain(p => p.Contains("No transport")).And.Contain(p => p.Contains("From:Address"));
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" }, PickupFolder = "p", Smtp = { Host = "h" } }, false, true)
            .Should().ContainSingle().Which.Should().Contain("choose one");
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" }, PickupFolder = "p", Bounces = { Endpoint = { Enabled = true, Secret = "short" } } }, false, true)
            .Should().ContainSingle().Which.Should().Contain("32 characters");
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" } }, customTransport: true, messaging: false)
            .Should().ContainSingle().Which.Should().Contain("AddMessaging");
    }

    [Fact]
    public async Task The_render_helper_renders_every_shipped_auth_template_in_English_and_Dutch()
    {
        using var sp = Build(withAuthTemplates: true);

        var rendered = await SparkMailTemplateTester.RenderAllAsync(sp, template => new
        {
            link = "https://app.example/x?a=1&b=2",
            changed_email = (string?)null,
            valid_for_hours = 24,
            valid_for_minutes = 60,
            provider = "GitHub",
            provider_identity = "octo",
        }, "en", "nl");

        rendered.Select(r => r.Subject).Should().Contain("Reset your password").And.Contain("Stel je wachtwoord opnieuw in");
        rendered.Should().HaveCount(6);
    }

    // ---- queueing, protection, suppression -----------------------------------------------------------

    [Fact]
    public async Task A_sensitive_mail_is_queued_encrypted_expiring_and_scrubbed_on_terminal()
    {
        using var sp = Build();
        var expires = DateTime.UtcNow.AddHours(23);
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISparkMailer>().SendAsync(new SparkMailRequest
            {
                Template = "SparkAuth/PasswordReset",
                To = "ann@app.example",
                Data = new { link = "https://app.example/reset?code=SECRET-TOKEN" },
                Sensitive = true,
                ExpiresAtUtc = expires,
            });

        var (message, options) = sp.GetRequiredService<RecordingBus>().Published.Single();
        var mail = (SparkMailMessage)message;
        mail.Data.Should().StartWith("sdp1:");
        mail.Data.Should().NotContain("SECRET-TOKEN");
        options.ScrubPayloadOnTerminal.Should().BeTrue();
        options.ExpiresAtUtc.Should().Be(expires);
        options.Queue.Should().Be(SparkMailQueues.Transactional);
        sp.GetRequiredService<SparkMailPayloadProtector>().Unprotect(mail.Data).Should().Contain("SECRET-TOKEN");
    }

    [Fact]
    public async Task A_suppressed_recipient_is_not_queued_and_one_suppressed_while_queued_is_not_sent()
    {
        WriteTemplate("News.mjml", "News");
        var transport = new FlakyTransport(failures: 0);
        using var sp = Build(s => s.Replace(ServiceDescriptor.Singleton<ISparkMailTransport>(transport)));
        var suppressions = sp.GetRequiredService<ISparkMailSuppressions>();
        await suppressions.SuppressAsync("Gone@App.Example ", SparkMailSuppressionReason.Bounce);

        SparkMailReceipt receipt;
        using (var scope = sp.CreateScope())
            receipt = await scope.ServiceProvider.GetRequiredService<ISparkMailer>().SendAsync(new SparkMailRequest { Template = "News", To = "gone@app.example" });
        receipt.Suppressed.Should().BeTrue("the check is on the normalized address");
        sp.GetRequiredService<RecordingBus>().Published.Should().BeEmpty();

        var queued = new SparkMailMessage { DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "late@app.example", Culture = "en", Stream = "News" };
        await suppressions.SuppressAsync("late@app.example", SparkMailSuppressionReason.Unsubscribe, "News");
        await Handler(sp).HandleAsync(queued);

        transport.Sent.Should().BeEmpty();
        (await sp.GetRequiredService<SparkMailDeliveryStore>().LoadAsync(queued.DeliveryId, default))!.Status.Should().Be(SparkMailDeliveryStatus.Suppressed);
        (await suppressions.IsSuppressedAsync("late@app.example", "Other")).Should().BeFalse("an unsubscribe is per stream");
    }

    [Fact]
    public async Task A_campaign_fans_out_one_deduplicated_bulk_mail_per_recipient()
    {
        using var sp = Build();
        using var scope = sp.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<SparkMailCampaignHandler>(scope.ServiceProvider);
        var campaign = new SparkMailCampaignMessage
        {
            CampaignId = "spring",
            Template = "News",
            Stream = "news",
            Data = """{"season":"spring","name":"everyone"}""",
            Recipients = [new() { Email = "a@app.example", Data = """{"name":"A"}""" }, new() { Email = "b@app.example", Culture = "nl" }],
        };

        await handler.HandleAsync(campaign);
        await handler.HandleAsync(campaign);

        var published = sp.GetRequiredService<RecordingBus>().Published;
        published.Should().HaveCount(4);
        published.Select(p => p.Options.DeduplicationKey).Distinct().Should().Equal(["spring:a@app.example", "spring:b@app.example"]);
        published.Should().OnlyContain(p => p.Options.Queue == SparkMailQueues.Bulk);
        var first = (SparkMailMessage)published[0].Message;
        first.Data.Should().Contain("\"name\":\"A\"").And.Contain("spring");
        first.ListUnsubscribe.Should().BeTrue();
        first.DeliveryId.Should().Be(((SparkMailMessage)published[2].Message).DeliveryId, "the delivery id is derived, so a re-run names the same delivery");
        ((SparkMailMessage)published[1].Message).Culture.Should().Be("nl");
    }

    // ---- SMTP, VERP, headers ---------------------------------------------------------------------------

    [Fact]
    public async Task SMTP_sends_with_the_VERP_envelope_a_delivery_Message_ID_and_one_click_unsubscribe()
    {
        await using var relay = new FakeSmtpServer(advertiseStartTls: true);
        WriteTemplate("News.mjml", "News for {{ name }}");
        using var sp = Build(config: new()
        {
            ["Spark:Mail:PickupFolder"] = null,
            ["Spark:Mail:Smtp:Host"] = "127.0.0.1",
            ["Spark:Mail:Smtp:Port"] = relay.Port.ToString(CultureInfo.InvariantCulture),
            ["Spark:Mail:Smtp:Security"] = "None",
            ["Spark:Mail:Bounces:VerpDomain"] = "bounce.app.example",
        });
        var message = new SparkMailMessage { DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "ann@app.example", Culture = "en", Stream = "news", ListUnsubscribe = true, Data = """{"name":"Ann"}""" };

        await Handler(sp).HandleAsync(message);

        relay.Commands.Should().NotContain("STARTTLS");
        relay.Commands.Should().Contain(c => c.StartsWith($"MAIL FROM:<bounces+{message.DeliveryId}@bounce.app.example>", StringComparison.Ordinal));
        var data = relay.Messages.Single();
        data.Should().Contain($"Message-Id: <{message.DeliveryId}@app.example>");
        data.Should().Contain("List-Unsubscribe:").And.Contain("<https://app.example/spark/mail/unsubscribe?t=", "MimeKit folds the long header onto the next line");
        data.Should().Contain("List-Unsubscribe-Post: List-Unsubscribe=One-Click");
        data.Should().Contain("Subject: News for Ann");
        (await sp.GetRequiredService<SparkMailDeliveryStore>().LoadAsync(message.DeliveryId, default))!.Status.Should().Be(SparkMailDeliveryStatus.Sent);
    }

    [Theory]
    [InlineData("553 5.7.1 Sender address rejected", true)]
    [InlineData("451 4.3.0 Try again later", false)]
    public async Task A_5xx_refusal_is_not_retried_and_a_4xx_is(string reply, bool permanent)
    {
        await using var relay = new FakeSmtpServer(advertiseStartTls: false) { MailFromReply = reply };
        WriteTemplate("News.mjml", "News");
        using var sp = Build(config: new()
        {
            ["Spark:Mail:PickupFolder"] = null,
            ["Spark:Mail:Smtp:Host"] = "127.0.0.1",
            ["Spark:Mail:Smtp:Port"] = relay.Port.ToString(CultureInfo.InvariantCulture),
            ["Spark:Mail:Smtp:Security"] = "None",
        });
        var message = new SparkMailMessage { DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "ann@app.example", Culture = "en", Stream = "news" };

        Exception? failure = null;
        try { await Handler(sp).HandleAsync(message); }
        catch (Exception ex) { failure = ex; }

        failure.Should().NotBeNull();
        (failure is NonRetryableException).Should().Be(permanent);
    }

    [Fact]
    public async Task The_pickup_folder_gets_one_eml_per_mail_and_RedirectTo_rewrites_the_recipient()
    {
        WriteTemplate("News.mjml", "News");
        using var sp = Build(config: new() { ["Spark:Mail:Development:RedirectTo"] = "dev@app.example" });
        var message = new SparkMailMessage { DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "ann@app.example", Culture = "en", Stream = "news" };

        await Handler(sp).HandleAsync(message);

        var eml = Path.Combine(contentRoot, "pickup", $"{message.DeliveryId}.eml");
        File.Exists(eml).Should().BeTrue();
        var loaded = await MimeMessage.LoadAsync(eml);
        loaded.To.Mailboxes.Single().Address.Should().Be("dev@app.example");
        loaded.Headers["X-Spark-Original-To"].Should().Be("ann@app.example");
    }

    // ---- bounces and unsubscribe -------------------------------------------------------------------------

    [Fact]
    public async Task A_permanent_DSN_for_a_VERP_delivery_suppresses_the_address_it_was_sent_to()
    {
        using var sp = Build();
        var deliveryId = SparkMailer.NewDeliveryId();
        await sp.GetRequiredService<SparkMailDeliveryStore>().RecordAsync(deliveryId, d => { d.Email = "ann@app.example"; d.Status = SparkMailDeliveryStatus.Sent; }, default);
        var dsn = Dsn($"bounces+{deliveryId}@bounce.app.example", "failed", "5.1.1");
        var transient = Dsn($"bounces+{SparkMailer.NewDeliveryId()}@bounce.app.example", "delayed", "4.4.1");

        var parser = sp.GetRequiredService<ISparkMailBounceParser>();
        var bounces = await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(dsn)), null, default);
        using var scope = sp.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<SparkMailBounceProcessor>();
        (await processor.ApplyAsync(bounces, default)).Should().Be(1);
        (await processor.ApplyAsync(await parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(transient)), null, default), default)).Should().Be(0);

        bounces.Single().Should().Be(new SparkMailBounce(deliveryId, "ann@app.example", "5.1.1", true, "smtp; 550 5.1.1 User unknown"));
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("ann@app.example", "anything")).Should().BeTrue();
        (await sp.GetRequiredService<SparkMailDeliveryStore>().LoadAsync(deliveryId, default))!.Status.Should().Be(SparkMailDeliveryStatus.Bounced);
    }

    [Fact]
    public void The_bounce_secret_is_a_bearer_token_compared_whole()
    {
        var secret = new string('s', 40);
        ReceiveBounce.IsAuthorized($"Bearer {secret}", secret).Should().BeTrue();
        ReceiveBounce.IsAuthorized($"Bearer {secret}x", secret).Should().BeFalse();
        ReceiveBounce.IsAuthorized(secret, secret).Should().BeFalse("the scheme is required");
        ReceiveBounce.IsAuthorized("Bearer ", null).Should().BeFalse();
    }

    [Fact]
    public void An_unsubscribe_token_names_its_address_and_stream_and_nothing_forged_reads()
    {
        using var sp = Build();
        var tokens = sp.GetRequiredService<SparkMailUnsubscribeTokens>();

        var link = tokens.Link("ann@app.example", "news")!;
        var token = Uri.UnescapeDataString(link[(link.IndexOf("?t=", StringComparison.Ordinal) + 3)..]);

        tokens.Read(token).Should().Be(("ann@app.example", "news"));
        tokens.Read(token + "x").Should().BeNull();
        tokens.Read("ann@app.example\nnews").Should().BeNull();
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static SparkMailHandler Handler(IServiceProvider sp) => ActivatorUtilities.CreateInstance<SparkMailHandler>(sp);

    private static string Dsn(string to, string action, string status) =>
        $"From: MAILER-DAEMON@relay.example\r\nTo: {to}\r\nSubject: Undelivered Mail\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/report; report-type=delivery-status; boundary=\"b\"\r\n\r\n" +
        "--b\r\nContent-Type: text/plain\r\n\r\nThe mail could not be delivered.\r\n" +
        "--b\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; relay.example\r\n\r\n" +
        $"Final-Recipient: rfc822; ann@app.example\r\nAction: {action}\r\nStatus: {status}\r\nDiagnostic-Code: smtp; 550 5.1.1 User unknown\r\n\r\n" +
        "--b--\r\n";

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "MailTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FixedCulture(string email, string culture) : ISparkMailRecipientCulture
    {
        public string Culture { get; set; } = culture;
        public ValueTask<string?> GetCultureAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(string.Equals(address, email, StringComparison.OrdinalIgnoreCase) ? Culture : null);
    }

    private sealed class FlakyTransport(int failures) : ISparkMailTransport
    {
        private int remaining = failures;
        public List<MimeMessage> Sent { get; } = [];
        public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken)
        {
            if (remaining-- > 0)
                throw new IOException("relay down");
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingBus : IMessageBus
    {
        public List<(object Message, BroadcastOptions Options)> Published { get; } = [];
        public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
        {
            Published.Add((message!, options));
            return Task.CompletedTask;
        }
    }
}
