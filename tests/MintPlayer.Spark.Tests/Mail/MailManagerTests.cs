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
using MintPlayer.Spark.Abstractions.Builder;
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

    private ServiceProvider Build(Action<IServiceCollection>? services = null, Dictionary<string, string?>? config = null, bool withAuthTemplates = false,
        Action<ISparkBuilder>? spark = null, string environment = "Production", bool validateScopes = false)
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
        collection.AddSingleton<IHostEnvironment>(new TestHostEnvironment(contentRoot) { EnvironmentName = environment });
        collection.AddSingleton<Raven.Client.Documents.IDocumentStore>(Store);
        collection.AddDataProtection().UseEphemeralDataProtectionProvider();
        collection.AddSingleton<RecordingBus>();
        collection.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<RecordingBus>());
        collection.AddSingleton(TimeProvider.System);
        var builder = new SparkBuilder(collection, configuration);
        builder.AddMailManager();
        spark?.Invoke(builder);
        if (withAuthTemplates)
            collection.AddSparkMailTemplates(typeof(SparkAuthMailTemplates).Assembly, "SparkMail/");
        services?.Invoke(collection);
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });
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
        SparkMailStartup.Problems(new SparkMailOptions(), [], messaging: true)
            .Should().Contain(p => p.Contains("No transport")).And.Contain(p => p.Contains("From:Address"));
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" }, PickupFolder = "p", Smtp = { Host = "h" } }, [], true)
            .Should().ContainSingle().Which.Should().Contain("choose one");
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" }, PickupFolder = "p", Bounces = { Endpoint = { Enabled = true, Secret = "short" } } }, [], true)
            .Should().ContainSingle().Which.Should().Contain("32 characters");
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" } }, ["AddMailTransport<X>"], messaging: false)
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

    [Fact]
    public void Startup_refuses_RedirectTo_in_Production_but_other_environments_keep_redirecting()
    {
        var redirect = new SparkMailOptions { From = { Address = "a@b.example" }, PickupFolder = "p", Development = { RedirectTo = "dev@app.example" } };
        SparkMailStartup.Problems(redirect, [], true, production: true)
            .Should().ContainSingle().Which.Should().Contain("RedirectTo");
        SparkMailStartup.Problems(redirect, [], true, production: false).Should().BeEmpty();

        var config = new Dictionary<string, string?> { ["Spark:Mail:Development:RedirectTo"] = "dev@app.example" };
        using (var production = Build(config: config))
        {
            var act = () => SparkMailStartup.Validate(production);
            act.Should().Throw<InvalidOperationException>().WithMessage("*RedirectTo*Production*");
        }
        foreach (var name in new[] { "Staging", "E2E", "Development" })
        {
            using var other = Build(config: config, environment: name);
            var act = () => SparkMailStartup.Validate(other);
            act.Should().NotThrow(name);
        }
    }

    // ---- transports: explicit registration, the config fallback, Development fails closed ---------------

    private static readonly Dictionary<string, string?> RemoteSmtp = new() { ["Spark:Mail:PickupFolder"] = "", ["Spark:Mail:Smtp:Host"] = "relay.example" };

    [Fact]
    public void Each_Use_registration_resolves_its_transport_and_explicit_registration_wins_over_the_config()
    {
        // The default test config has a PickupFolder, so the fallback alone would pick the pickup folder.
        using (var smtp = Build(config: RemoteSmtp, spark: b => b.UseSmtpTransport()))
            (smtp.GetRequiredService<ISparkMailTransport>() is SmtpMailTransport).Should().BeTrue();
        using (var mailpit = Build(spark: b => b.UseMailpitTransport()))
            (mailpit.GetRequiredService<ISparkMailTransport>() is MailpitMailTransport).Should().BeTrue("explicit wins over Spark:Mail:PickupFolder");
        using (var pickup = Build(config: new() { ["Spark:Mail:PickupFolder"] = "" }, spark: b => b.UsePickupFolderTransport("drop")))
        {
            (pickup.GetRequiredService<ISparkMailTransport>() is PickupFolderMailTransport).Should().BeTrue();
            pickup.GetRequiredService<IOptions<SparkMailOptions>>().Value.PickupFolder.Should().Be("drop");
        }
        using (var custom = Build(spark: b => b.AddMailTransport<RealTransport>()))
            (custom.GetRequiredService<ISparkMailTransport>() is RealTransport).Should().BeTrue();
        using (var callback = Build(config: RemoteSmtp, spark: b => b.UseSmtpTransport(s => s.Port = 2525)))
            callback.GetRequiredService<IOptions<SparkMailOptions>>().Value.Smtp.Port.Should().Be(2525, "the callback runs after binding");
    }

    [Fact]
    public void Without_a_registration_the_configuration_still_picks_the_transport()
    {
        using (var smtp = Build(config: RemoteSmtp))
            (smtp.GetRequiredService<ISparkMailTransport>() is SmtpMailTransport).Should().BeTrue();
        using (var pickup = Build())
            (pickup.GetRequiredService<ISparkMailTransport>() is PickupFolderMailTransport).Should().BeTrue();
    }

    [Fact]
    public void Two_registered_transports_refuse_startup()
    {
        using var sp = Build(spark: b => b.UseMailpitTransport().AddMailTransport<RealTransport>());
        var act = () => SparkMailStartup.Validate(sp);
        act.Should().Throw<InvalidOperationException>().WithMessage("*2 mail transports*UseMailpitTransport*AddMailTransport<RealTransport>*");
        SparkMailStartup.Problems(new SparkMailOptions { From = { Address = "a@b.example" } }, ["UseSmtpTransport"], true)
            .Should().ContainSingle().Which.Should().Contain("Smtp:Host");
    }

    [Fact]
    public async Task Mailpit_gets_plain_SMTP_on_the_configured_port_with_X_Tags()
    {
        await using var mailpit = new FakeSmtpServer(advertiseStartTls: true);
        WriteTemplate("News.mjml", "News");
        using var sp = Build(
            config: new()
            {
                ["Spark:Mail:Mailpit:Host"] = "127.0.0.1",
                ["Spark:Mail:Mailpit:Port"] = mailpit.Port.ToString(CultureInfo.InvariantCulture),
                ["Spark:Mail:Mailpit:Tags:0"] = "demo",
            },
            spark: b => b.UseMailpitTransport(m => m.Tags.Add("local run")));
        var message = new SparkMailMessage { DeliveryId = SparkMailer.NewDeliveryId(), Template = "News", To = "ann@app.example", Culture = "en", Stream = "news", Queue = SparkMailQueues.Bulk };

        await Handler(sp).HandleAsync(message);

        mailpit.Commands.Should().NotContain("STARTTLS", "Mailpit gets no TLS even when offered");
        mailpit.Commands.Should().NotContain(c => c.StartsWith("AUTH", StringComparison.Ordinal));
        mailpit.Commands.Should().Contain(c => c.StartsWith("RCPT TO:<ann@app.example>", StringComparison.Ordinal));
        var data = mailpit.Messages.Single();
        data.Should().Contain("X-Tags: News, bulk, demo, local run");
        data.Should().Contain($"Message-Id: <{message.DeliveryId}@app.example>", "the MIME is the one a relay would get");
        MailpitMailTransport.Tag("SparkAuth/ConfirmEmail").Should().Be("SparkAuth-ConfirmEmail");
    }

    [Fact]
    public void Startup_accepts_a_scoped_message_bus_under_Development_scope_validation()
    {
        // AddSparkMessaging registers IMessageBus scoped, and a Development host validates scopes, so
        // resolving the bus from the root provider threw "Cannot resolve scoped service" at startup.
        using var sp = Build(
            services: c => c.Replace(ServiceDescriptor.Scoped<IMessageBus>(p => p.GetRequiredService<RecordingBus>())),
            environment: "Development",
            validateScopes: true);
        var act = () => SparkMailStartup.Validate(sp);
        act.Should().NotThrow();

        using var withoutBus = Build(services: c => c.RemoveAll<IMessageBus>(), environment: "Development", validateScopes: true);
        var refused = () => SparkMailStartup.Validate(withoutBus);
        refused.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Development_fails_closed_for_a_transport_that_delivers_to_real_recipients()
    {
        void Starts(string because, Dictionary<string, string?>? config = null, Action<ISparkBuilder>? spark = null)
        {
            using var sp = Build(config: config, spark: spark, environment: "Development");
            var act = () => SparkMailStartup.Validate(sp);
            act.Should().NotThrow(because);
        }
        void Refuses(string because, Dictionary<string, string?>? config = null, Action<ISparkBuilder>? spark = null)
        {
            using var sp = Build(config: config, spark: spark, environment: "Development");
            var act = () => SparkMailStartup.Validate(sp);
            act.Should().Throw<InvalidOperationException>().WithMessage("*RedirectTo*UseMailpitTransport*");
        }

        Refuses("remote SMTP, no redirect", RemoteSmtp);
        Refuses("remote SMTP through UseSmtpTransport, no redirect", RemoteSmtp, b => b.UseSmtpTransport());
        Starts("remote SMTP with a redirect", new(RemoteSmtp) { ["Spark:Mail:Development:RedirectTo"] = "dev@app.example" });
        Starts("loopback SMTP is a local catcher", new() { ["Spark:Mail:PickupFolder"] = "", ["Spark:Mail:Smtp:Host"] = "localhost" });
        Starts("Mailpit", spark: b => b.UseMailpitTransport());
        Starts("the pickup folder");
        Starts("a custom transport that says it catches", spark: b => b.AddMailTransport<CatchingTransport>());
        Refuses("a custom transport keeps the safe default", spark: b => b.AddMailTransport<RealTransport>());

        using var staging = Build(config: RemoteSmtp, environment: "Staging");
        var outside = () => SparkMailStartup.Validate(staging);
        outside.Should().NotThrow("only Development fails closed");

        foreach (var loopback in new[] { "localhost", "LOCALHOST", "127.0.0.1", "127.0.0.2", "::1", "[::1]" })
            SmtpMailTransport.IsLoopback(loopback).Should().BeTrue(loopback);
        SmtpMailTransport.IsLoopback("relay.example").Should().BeFalse();
        SmtpMailTransport.IsLoopback("10.0.0.1").Should().BeFalse();
    }

    private sealed class RealTransport : ISparkMailTransport
    {
        public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CatchingTransport : ISparkMailTransport
    {
        public bool DeliversToRealRecipients => false;
        public Task SendAsync(MimeMessage message, MailboxAddress envelopeFrom, MailboxAddress recipient, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ---- bounces and unsubscribe -------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_bounce_endpoint_answers_503_so_the_relay_keeps_the_report()
    {
        // The relay's pipe drops a report on 400/404/413/422 and defers it on anything else: disabled
        // must be temporary, or every bounce sent before the endpoint is enabled is lost.
        using var sp = Build();
        using var scope = sp.CreateScope();
        var endpoint = ActivatorUtilities.CreateInstance<ReceiveBounce>(scope.ServiceProvider);
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers.Authorization = $"Bearer {new string('s', 40)}";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Dsn("bounces+x@bounce.app.example", "failed", "5.1.1")));

        var result = await endpoint.HandleAsync(httpContext);

        (result is Microsoft.AspNetCore.Http.IStatusCodeHttpResult).Should().BeTrue();
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.Should().Be(503, "503 is temporary: Postfix keeps the report");
    }

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

    // ---- complaints (ARF, RFC 5965) — M16 --------------------------------------------------------------

    [Fact]
    public async Task An_abuse_report_suppresses_the_delivery_address_for_every_stream_as_a_complaint()
    {
        using var sp = Build(config: ReportEndpoint());
        var deliveryId = await RecordDeliveryAsync(sp, "ann@app.example");

        var (status, reports) = await PostReportAsync(sp, Arf("abuse", originalMailFrom: $"<bounces+{deliveryId}@bounce.app.example>"));

        status.Should().Be(204);
        reports.Single().FeedbackType.Should().Be("abuse");
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("ann@app.example", "news")).Should().BeTrue();
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<SparkMailSuppression>(SparkMailSuppression.IdFor("ann@app.example")))!.Reason.Should().Be(SparkMailSuppressionReason.Complaint);
        var delivery = (await sp.GetRequiredService<SparkMailDeliveryStore>().LoadAsync(deliveryId, default))!;
        delivery.Status.Should().Be(SparkMailDeliveryStatus.Complained);
        delivery.Detail.Should().Be("feedback-report: abuse");
    }

    [Fact]
    public async Task A_complaint_is_matched_by_the_original_Message_ID_when_the_loop_redacted_the_addresses()
    {
        using var sp = Build(config: ReportEndpoint());
        var deliveryId = await RecordDeliveryAsync(sp, "bob@app.example");

        // text/rfc822-headers third part, no Original-Mail-From, recipient redacted — the common shape.
        var (status, reports) = await PostReportAsync(sp, Arf("abuse", originalMailFrom: null, messageId: $"<{deliveryId}@app.example>", headersOnly: true, rcptTo: "redacted@example.net"));

        status.Should().Be(204);
        reports.Single().DeliveryId.Should().Be(deliveryId);
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("bob@app.example")).Should().BeTrue("the delivery record names the address, not the report");
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("redacted@example.net")).Should().BeFalse();
    }

    [Fact]
    public async Task A_not_spam_report_is_recorded_and_suppresses_nothing()
    {
        using var sp = Build(config: ReportEndpoint());
        var deliveryId = await RecordDeliveryAsync(sp, "cleo@app.example");

        var (status, _) = await PostReportAsync(sp, Arf("not-spam", originalMailFrom: $"<bounces+{deliveryId}@bounce.app.example>"));

        status.Should().Be(204);
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("cleo@app.example")).Should().BeFalse();
        (await sp.GetRequiredService<SparkMailDeliveryStore>().LoadAsync(deliveryId, default))!.Status.Should().Be(SparkMailDeliveryStatus.Complained);
    }

    [Fact]
    public async Task An_unmatched_complaint_is_accepted_with_204_so_the_relay_drops_it_and_suppresses_nothing()
    {
        using var sp = Build(config: ReportEndpoint());

        var (status, reports) = await PostReportAsync(sp, Arf("abuse", originalMailFrom: "<someone@example.net>", messageId: "<not-a-delivery@example.net>", rcptTo: "dana@app.example"));

        status.Should().Be(204);
        reports.Single().DeliveryId.Should().BeNull();
        (await sp.GetRequiredService<ISparkMailSuppressions>().IsSuppressedAsync("dana@app.example")).Should().BeFalse();
    }

    [Fact]
    public async Task A_malformed_feedback_report_is_refused_with_400_which_the_relay_drops()
    {
        using var sp = Build(config: ReportEndpoint());
        var noFeedbackPart = Arf("abuse", originalMailFrom: null).Replace("Content-Type: message/feedback-report", "Content-Type: text/plain");
        var noFeedbackType = Arf("abuse", originalMailFrom: null).Replace("Feedback-Type: abuse\r\n", "");

        (await PostReportAsync(sp, noFeedbackPart)).Status.Should().Be(400);
        (await PostReportAsync(sp, noFeedbackType)).Status.Should().Be(400);
    }

    [Fact]
    public void Account_mail_is_high_priority_and_campaign_mail_low_by_default()
    {
        using var sp = Build(config: ReportEndpoint());
        var messaging = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MintPlayer.Spark.Messaging.SparkMessagingOptions>>().Value;

        messaging.PriorityFor(SparkMailQueues.Transactional).Should().Be(MintPlayer.Spark.Messaging.SparkQueuePriority.High);
        messaging.PriorityFor(SparkMailQueues.Bulk).Should().Be(MintPlayer.Spark.Messaging.SparkQueuePriority.Low);
    }

    private static async Task<string> RecordDeliveryAsync(IServiceProvider sp, string email)
    {
        var deliveryId = SparkMailer.NewDeliveryId();
        await sp.GetRequiredService<SparkMailDeliveryStore>().RecordAsync(deliveryId, d => { d.Email = email; d.Status = SparkMailDeliveryStatus.Sent; }, default);
        return deliveryId;
    }

    /// <summary>POSTs a report to an enabled endpoint; returns the status and what the parser made of it (when it parses).</summary>
    private static Dictionary<string, string?> ReportEndpoint() => new()
    {
        ["Spark:Mail:Bounces:Endpoint:Enabled"] = "true",
        ["Spark:Mail:Bounces:Endpoint:Secret"] = ReportSecret,
    };

    private static readonly string ReportSecret = new('s', 40);

    private static async Task<(int Status, IReadOnlyList<SparkMailBounce> Reports)> PostReportAsync(ServiceProvider sp, string report)
    {
        using var scope = sp.CreateScope();
        var endpoint = ActivatorUtilities.CreateInstance<ReceiveBounce>(scope.ServiceProvider);
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers.Authorization = $"Bearer {ReportSecret}";
        // ?recipient= is bound by the route table (MailEndpointQueryTests); called directly, the endpoint gets it set.
        endpoint.Recipient = "fbl@bounce.app.example";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(report));

        var result = await endpoint.HandleAsync(httpContext);
        var status = ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode ?? 200;
        IReadOnlyList<SparkMailBounce> parsed = [];
        if (status != 400)
            parsed = await sp.GetRequiredService<ISparkMailBounceParser>().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(report)), "fbl@bounce.app.example", default);
        return (status, parsed);
    }

    /// <summary>
    /// An RFC 5965 feedback report, shaped on the RFC's Appendix B example (addresses on the reserved
    /// example domains): a human-readable part, the <c>message/feedback-report</c> part and the original
    /// message (<c>message/rfc822</c>) or its headers (<c>text/rfc822-headers</c>).
    /// </summary>
    private static string Arf(string feedbackType, string? originalMailFrom, string? messageId = null, bool headersOnly = false, string rcptTo = "ann@app.example")
    {
        var fields = $"Feedback-Type: {feedbackType}\r\nUser-Agent: SomeGenerator/1.0\r\nVersion: 1\r\n" +
            (originalMailFrom is null ? "" : $"Original-Mail-From: {originalMailFrom}\r\n") +
            $"Original-Rcpt-To: <{rcptTo}>\r\nArrival-Date: Thu, 8 Mar 2005 14:00:00 EDT\r\nReporting-MTA: dns; mail.example.net\r\n" +
            "Source-IP: 192.0.2.1\r\nAuthentication-Results: mail.example.net; spf=fail smtp.mail=app.example\r\nReported-Domain: app.example\r\n";
        var original = $"Received: from mailserver.app.example (mailserver.app.example [192.0.2.1]) by mail.example.net\r\n" +
            $"From: <noreply@app.example>\r\nTo: <{rcptTo}>\r\nSubject: Spring news\r\nMIME-Version: 1.0\r\n" +
            (messageId is null ? "" : $"Message-ID: {messageId}\r\n") +
            "Date: Thu, 8 Mar 2005 17:40:36 EDT\r\n";
        return "From: <abusedesk@example.net>\r\nDate: Thu, 8 Mar 2005 17:40:36 EDT\r\nSubject: FW: Spring news\r\n" +
            "To: <fbl@bounce.app.example>\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/report; report-type=feedback-report; boundary=\"part1_13d.2e68ed54_boundary\"\r\n\r\n" +
            "--part1_13d.2e68ed54_boundary\r\nContent-Type: text/plain; charset=\"US-ASCII\"\r\nContent-Transfer-Encoding: 7bit\r\n\r\n" +
            "This is an email abuse report for an email message received from IP 192.0.2.1 on Thu, 8 Mar 2005 14:00:00 EDT.\r\n" +
            "For more information about this format please see http://www.mipassoc.org/arf/.\r\n\r\n" +
            "--part1_13d.2e68ed54_boundary\r\nContent-Type: message/feedback-report\r\n\r\n" + fields + "\r\n" +
            "--part1_13d.2e68ed54_boundary\r\n" +
            (headersOnly ? "Content-Type: text/rfc822-headers\r\n\r\n" + original + "\r\n"
                         : "Content-Type: message/rfc822\r\nContent-Disposition: inline\r\n\r\n" + original + "\r\nSpring is here.\r\n") +
            "\r\n--part1_13d.2e68ed54_boundary--\r\n";
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
