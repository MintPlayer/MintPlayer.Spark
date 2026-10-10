using System.Text;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Settings;
using Microsoft.Extensions.Options;
using SeoTester.Bot;
using SeoTester.Pages;
using SeoTester.Settings;

namespace SeoTester.Legal;

/// <summary>
/// The privacy policy and the terms of use (<c>docs/seo_analyzer_PRD.md</c> D47), server-rendered in English and Dutch
/// like <c>/bot</c>. The texts describe what the code does (the retention periods are the model's <c>expiresAfter</c>
/// values and the administrator's settings); the operator's identity comes from <see cref="SeoLegalOptions"/>.
/// </summary>
/// <remarks>
/// ⚠️ A draft written from the code for the owner to review, not legal advice (PRD D47). Raise <see cref="Version"/> and
/// <see cref="EffectiveDate"/> whenever a text changes in substance.
/// </remarks>
public static class LegalPages
{
    public const string Version = "1";
    public const string EffectiveDate = "2026-10-10";

    public const string MarkerEn = "[to be completed]";
    public const string MarkerNl = "[nog in te vullen]";

    /// <summary>The retention periods the policy states that are settings, not constants.</summary>
    public sealed record Facts(TimeSpan AuditLogRetention, TimeSpan TargetCoolDown);

    public static string RenderPrivacy(string language, SeoLegalOptions legal, Facts facts)
    {
        var w = new Writer(language == "nl", legal, facts);
        w.Begin("Privacy policy", "Privacyverklaring");

        w.H2("Who is responsible", "Wie is verantwoordelijk");
        w.P("SeoTester (https://seotester.mintplayer.com) is operated by {op}, {addr}, enterprise number {kbo}. The operator is the controller of the personal data described below. Questions and requests: {mail}.",
            "SeoTester (https://seotester.mintplayer.com) wordt uitgebaat door {op}, {addr}, ondernemingsnummer {kbo}. De uitbater is de verwerkingsverantwoordelijke voor de persoonsgegevens hieronder. Vragen en verzoeken: {mail}.");

        w.H2("What is stored, and for how long", "Wat er bewaard wordt, en hoe lang");
        w.List(
            ("Your account. When you sign in with Microsoft, GitHub, X or Google, SeoTester receives and keeps your name or user name, your e-mail address (when the provider gives one) and the provider's identifier for your account, so you can sign in again and link providers. A passkey you add is stored as its public key only. Kept until you delete your account.",
             "Je account. Wanneer je aanmeldt met Microsoft, GitHub, X of Google, ontvangt en bewaart SeoTester je naam of gebruikersnaam, je e-mailadres (als de provider het meegeeft) en de identificatie van je account bij die provider, zodat je opnieuw kan aanmelden en providers kan koppelen. Een passkey die je toevoegt, wordt enkel als publieke sleutel bewaard. Bewaard tot je je account verwijdert."),
            ("Organizations: their names, their members and roles. An invitation (with the invited e-mail address) expires after 14 days.",
             "Organisaties: hun namen, hun leden en rollen. Een uitnodiging (met het uitgenodigde e-mailadres) vervalt na 14 dagen."),
            ("Websites you add: their address, settings, monitoring schedule and ownership verification (the token and how it was proven).",
             "Websites die je toevoegt: hun adres, instellingen, opvolgingsplanning en eigendomsverificatie (de code en hoe ze bewezen werd)."),
            ("Audit summaries: scores, check results, up to five sample addresses per check, who started or joined the audit and when. Kept until the website, its organization or your personal organization is deleted.",
             "Samenvattingen van audits: scores, resultaten per controle, tot vijf voorbeeldadressen per controle, wie de audit startte of erbij kwam en wanneer. Bewaard tot de website, haar organisatie of je persoonlijke organisatie verwijderd wordt."),
            ("Page details of an audit (per page: address, status, title, description and findings): 7 days. Printed PDF reports: 7 days. The raw pages, screenshots and full PageSpeed answers are never stored.",
             "Paginadetails van een audit (per pagina: adres, status, titel, beschrijving en bevindingen): 7 dagen. Afgedrukte PDF-rapporten: 7 dagen. De ruwe pagina's, schermafbeeldingen en volledige PageSpeed-antwoorden worden nooit bewaard."),
            ("Notifications: 90 days. Your notification preferences: until you delete your account.",
             "Meldingen: 90 dagen. Je meldingsvoorkeuren: tot je je account verwijdert."),
            ("Push subscriptions, when you allow notifications in a browser: the address and keys that browser's push service issued, per device, until you switch push off or the push service reports the subscription gone.",
             "Pushabonnementen, wanneer je meldingen toestaat in een browser: het adres en de sleutels die de pushdienst van die browser uitgaf, per toestel, tot je push uitzet of de pushdienst meldt dat het abonnement niet meer bestaat."),
            ("The abuse log: who audited which website from which organization and when, and refused attempts (quota, cool-down): {log}.",
             "Het misbruiklogboek: wie welke website auditte vanuit welke organisatie en wanneer, en geweigerde pogingen (quotum, afkoelperiode): {log}."),
            ("Cool-downs per audited host (no personal data): {cool}. A record of each e-mail sent (recipient, subject, delivery status): 30 days.",
             "Afkoelperiodes per geaudite host (geen persoonsgegevens): {cool}. Een spoor van elke verzonden e-mail (ontvanger, onderwerp, afleverstatus): 30 dagen."));
        w.P("Why: your account, organizations, audits, notifications and the e-mail they need are the service you asked for (GDPR art. 6(1)(b)). The abuse log and the cool-downs protect the websites that are audited and the service itself (legitimate interest, art. 6(1)(f)). Push notifications need your browser's permission, which you can withdraw at any time (art. 6(1)(a)). Nothing is sold, and nothing is used for advertising.",
            "Waarom: je account, organisaties, audits, meldingen en de e-mail die ze nodig hebben, zijn de dienst waar je om vroeg (AVG art. 6(1)(b)). Het misbruiklogboek en de afkoelperiodes beschermen de geaudite websites en de dienst zelf (gerechtvaardigd belang, art. 6(1)(f)). Pushmeldingen vragen de toestemming van je browser, die je altijd kan intrekken (art. 6(1)(a)). Niets wordt verkocht of voor reclame gebruikt.");

        w.H2("The websites that are audited", "De websites die geaudit worden");
        w.P("The crawler reads publicly reachable pages of the websites users add, identifies itself and obeys robots.txt. Website owners: see {bot}.",
            "De crawler leest openbaar bereikbare pagina's van de websites die gebruikers toevoegen, maakt zich bekend en volgt robots.txt. Website-eigenaars: zie {bot}.");

        w.H2("Who else receives data", "Wie nog gegevens ontvangt");
        w.List(
            ("The sign-in provider you choose (Microsoft, GitHub, X or Google) learns that you signed in to SeoTester.",
             "De aanmeldprovider die je kiest (Microsoft, GitHub, X of Google) weet dat je aanmeldde bij SeoTester."),
            ("Google PageSpeed Insights receives the addresses of the sampled pages of an audited website, never who asked. Google may process them outside the European Union.",
             "Google PageSpeed Insights ontvangt de adressen van de gekozen pagina's van een geaudite website, nooit wie het vroeg. Google kan ze buiten de Europese Unie verwerken."),
            ("Your browser's push service (Google, Mozilla, Apple or Microsoft, depending on the browser) carries push notifications. Their content is encrypted for your browser only; the push service sees when a message is sent to which subscription.",
             "De pushdienst van je browser (Google, Mozilla, Apple of Microsoft, afhankelijk van de browser) bezorgt pushmeldingen. De inhoud is versleuteld voor enkel jouw browser; de pushdienst ziet wanneer een bericht naar welk abonnement gaat."),
            ("Domain registries (RDAP, found through IANA), hstspreload.org and DNS resolvers receive the domain names of audited websites, nothing about you.",
             "Domeinregisters (RDAP, gevonden via IANA), hstspreload.org en DNS-resolvers ontvangen de domeinnamen van geaudite websites, niets over jou."),
            ("E-mail is sent from SeoTester's own mail server to your mail provider. The server is hosted by {host}.",
             "E-mail wordt verstuurd vanaf de eigen mailserver van SeoTester naar je mailprovider. De server wordt gehost door {host}."));

        w.H2("Cookies and local storage", "Cookies en lokale opslag");
        w.P("SeoTester sets only what it needs to work: the sign-in cookie (.SparkAuth.SeoTester), antiforgery cookies that protect your actions against other sites, short-lived cookies during an external sign-in, and preferences (spark-lang for the language, bs-theme-mode for light or dark, spark-timezone for your time zone; the language and whether \"how to fix\" opens are also kept in your browser's local storage). No analytics, advertising or tracking cookies, and no third-party scripts.",
            "SeoTester zet enkel wat nodig is om te werken: de aanmeldcookie (.SparkAuth.SeoTester), antiforgerycookies die je acties beschermen tegen andere sites, kortlevende cookies tijdens een externe aanmelding, en voorkeuren (spark-lang voor de taal, bs-theme-mode voor licht of donker, spark-timezone voor je tijdzone; de taal en of \"hoe op te lossen\" openklapt, staan ook in de lokale opslag van je browser). Geen analyse-, reclame- of trackingcookies, en geen scripts van derden.");

        w.H2("Your rights", "Je rechten");
        w.P("You may ask for access to, correction, erasure, restriction or a copy of your data, object to its processing, and withdraw consent. Your account page does two of these at once: \"Download my data\" exports your data, and \"Delete account\" erases your account, your memberships and your personal organization with its websites and audits. For anything else, write to {mail}. You may also complain to the Belgian Data Protection Authority (Gegevensbeschermingsautoriteit / Autorité de protection des données, https://www.dataprotectionauthority.be).",
            "Je kan inzage, verbetering, wissing, beperking of een kopie van je gegevens vragen, bezwaar maken tegen de verwerking, en toestemming intrekken. Je accountpagina doet er twee meteen: \"Mijn gegevens downloaden\" exporteert je gegevens, en \"Account verwijderen\" wist je account, je lidmaatschappen en je persoonlijke organisatie met haar websites en audits. Voor al de rest: schrijf naar {mail}. Je kan ook klacht indienen bij de Gegevensbeschermingsautoriteit (https://www.gegevensbeschermingsautoriteit.be).");

        w.H2("Changes", "Wijzigingen");
        w.P("The version and effective date are at the top. A change in substance gets a new version, announced in the app.",
            "De versie en de datum van inwerkingtreding staan bovenaan. Een inhoudelijke wijziging krijgt een nieuwe versie, aangekondigd in de app.");
        w.P("See also the {terms}.", "Zie ook de {terms}.");
        return w.End();
    }

    public static string RenderTerms(string language, SeoLegalOptions legal)
    {
        var w = new Writer(language == "nl", legal, null);
        w.Begin("Terms of use", "Gebruiksvoorwaarden");

        w.H2("The service", "De dienst");
        w.P("SeoTester (https://seotester.mintplayer.com) checks websites for SEO, performance, security, accessibility and social previews. It is operated by {op}, {addr}, enterprise number {kbo}; contact: {mail}. By signing in you accept these terms.",
            "SeoTester (https://seotester.mintplayer.com) controleert websites op SEO, prestaties, beveiliging, toegankelijkheid en sociale previews. De dienst wordt uitgebaat door {op}, {addr}, ondernemingsnummer {kbo}; contact: {mail}. Door aan te melden aanvaard je deze voorwaarden.");

        w.H2("Acceptable use", "Aanvaardbaar gebruik");
        w.List(
            ("Audit only websites you own or are allowed to test.", "Audit enkel websites die van jou zijn of die je mag testen."),
            ("Do not use SeoTester to overload, attack or probe websites, and do not try to get around its quotas, cool-downs or limits (for example with several accounts).",
             "Gebruik SeoTester niet om websites te overbelasten, aan te vallen of af te tasten, en probeer de quota, afkoelperiodes of limieten niet te omzeilen (bijvoorbeeld met meerdere accounts)."),
            ("Do not scrape SeoTester itself or put unlawful content in names and descriptions.", "Schraap SeoTester zelf niet en zet geen onwettige inhoud in namen en beschrijvingen."));

        w.H2("Ownership verification", "Eigendomsverificatie");
        w.P("Proving that your organization controls a website (DNS record, file or meta tag) unlocks deeper crawls, checks for exposed files and scheduled monitoring. Verify only websites you control. The proof is checked again regularly; when it disappears, the verification is withdrawn. A website verified by another organization is handled by asking that organization's owners.",
            "Bewijzen dat je organisatie een website beheert (DNS-record, bestand of metatag) geeft diepere crawls, controles op blootgestelde bestanden en geplande opvolging. Verifieer enkel websites die je beheert. Het bewijs wordt geregeld opnieuw gecontroleerd; verdwijnt het, dan vervalt de verificatie. Een website die een andere organisatie verifieerde, verloopt via een verzoek aan de eigenaars van die organisatie.");

        w.H2("The crawler", "De crawler");
        w.P("The crawler identifies itself, obeys robots.txt and keeps to the limits on {bot}, where website owners also find how to opt out. An audit can be refused or stopped, for example when a website opts out or a limit is reached.",
            "De crawler maakt zich bekend, volgt robots.txt en houdt zich aan de limieten op {bot}, waar website-eigenaars ook lezen hoe ze zich afmelden. Een audit kan geweigerd of gestopt worden, bijvoorbeeld wanneer een website zich afmeldt of een limiet bereikt is.");

        w.H2("Quotas and availability", "Quota en beschikbaarheid");
        w.P("The number of audits per user, the limits per audit and the features available to unverified websites are set by the administrator and may change. The service may be interrupted for maintenance or otherwise, without notice.",
            "Het aantal audits per gebruiker, de limieten per audit en de functies voor niet-geverifieerde websites worden door de beheerder bepaald en kunnen wijzigen. De dienst kan onderbroken worden voor onderhoud of anderszins, zonder aankondiging.");

        w.H2("Results and sharing", "Resultaten en delen");
        w.P("Results are produced automatically and may be incomplete or wrong; they are not professional advice. A share link or badge shows its report to anyone who has the link, until you stop sharing.",
            "Resultaten worden automatisch gemaakt en kunnen onvolledig of fout zijn; ze zijn geen professioneel advies. Een deellink of badge toont het rapport aan iedereen die de link heeft, tot je stopt met delen.");

        w.H2("No warranty, and liability", "Geen garantie, en aansprakelijkheid");
        w.P("SeoTester is provided as is, without any warranty. To the extent the law allows, the operator is not liable for indirect damage, lost profit or lost data from using the service or its results. Nothing in these terms limits liability for intent or gross negligence, or where the law does not allow it.",
            "SeoTester wordt aangeboden zoals hij is, zonder enige garantie. Voor zover de wet het toelaat, is de uitbater niet aansprakelijk voor onrechtstreekse schade, gederfde winst of verloren gegevens door het gebruik van de dienst of zijn resultaten. Niets in deze voorwaarden beperkt de aansprakelijkheid voor opzet of grove fout, of waar de wet dat niet toelaat.");

        w.H2("Ending", "Beëindiging");
        w.P("You can delete your account at any time on your account page. An account that breaks these terms may be suspended or closed.",
            "Je kan je account altijd verwijderen op je accountpagina. Een account dat deze voorwaarden schendt, kan geschorst of gesloten worden.");

        w.H2("Changes, law and courts", "Wijzigingen, recht en rechtbanken");
        w.P("A change in substance gets a new version and effective date, announced in the app. These terms are governed by Belgian law; disputes go to {court}, without prejudice to the mandatory protection consumers have where they live.",
            "Een inhoudelijke wijziging krijgt een nieuwe versie en datum van inwerkingtreding, aangekondigd in de app. Op deze voorwaarden is het Belgisch recht van toepassing; geschillen gaan naar {court}, onverminderd de dwingende bescherming die consumenten hebben waar ze wonen.");
        w.P("How personal data is handled: the {privacy}.", "Hoe persoonsgegevens behandeld worden: de {privacy}.");
        return w.End();
    }

    /// <summary>Builds one page in one language; every text is encoded, then its placeholders become values or the marker.</summary>
    private sealed class Writer(bool nl, SeoLegalOptions legal, Facts? facts)
    {
        private StringBuilder sb = new();

        public void Begin(string titleEn, string titleNl)
        {
            var title = nl ? titleNl : titleEn;
            sb = ServerPage.Start(nl, title + " · SeoTester");
            sb.Append("<h1>").Append(ServerPage.E(title)).Append("</h1>\n<p><small data-version>")
              .Append(ServerPage.E(nl ? $"Versie {Version} · van kracht sinds {EffectiveDate}" : $"Version {Version} · Effective {EffectiveDate}"))
              .Append("</small></p>\n");
        }

        public void H2(string en, string nlText) => sb.Append("<h2>").Append(ServerPage.E(nl ? nlText : en)).Append("</h2>\n");

        public void P(string en, string nlText) => sb.Append("<p>").Append(Fill(nl ? nlText : en)).Append("</p>\n");

        public void List(params (string En, string Nl)[] items)
        {
            sb.Append("<ul>\n");
            foreach (var (en, nlText) in items)
                sb.Append("<li>").Append(Fill(nl ? nlText : en)).Append("</li>\n");
            sb.Append("</ul>\n");
        }

        public string End() => ServerPage.End(sb);

        private string Fill(string text)
        {
            var html = ServerPage.E(text);
            return html
                .Replace("{op}", Value(legal.OperatorName))
                .Replace("{addr}", Value(legal.Address))
                .Replace("{kbo}", Value(legal.EnterpriseNumber))
                .Replace("{court}", Value(legal.Court))
                .Replace("{host}", Value(legal.HostingProvider))
                .Replace("{mail}", string.IsNullOrWhiteSpace(legal.ContactEmail) ? Marker()
                    : $"<a href=\"mailto:{ServerPage.E(legal.ContactEmail)}\">{ServerPage.E(legal.ContactEmail)}</a>")
                .Replace("{log}", facts is null ? "" : ServerPage.E(BotPage.Duration(facts.AuditLogRetention, nl)))
                .Replace("{cool}", facts is null ? "" : ServerPage.E(BotPage.Duration(facts.TargetCoolDown, nl)))
                .Replace("{bot}", "<a href=\"/bot\">/bot</a>")
                .Replace("{terms}", $"<a href=\"/terms\">{ServerPage.E(nl ? "gebruiksvoorwaarden" : "terms of use")}</a>")
                .Replace("{privacy}", $"<a href=\"/privacy\">{ServerPage.E(nl ? "privacyverklaring" : "privacy policy")}</a>");
        }

        private string Value(string? value) => string.IsNullOrWhiteSpace(value) ? Marker() : ServerPage.E(value);

        private string Marker() => "<mark data-to-be-completed>" + ServerPage.E(nl ? MarkerNl : MarkerEn) + "</mark>";
    }
}

/// <summary><c>GET /privacy</c>: the privacy policy (D47). Anonymous, no script; English or Dutch as <c>/bot</c>.</summary>
internal sealed partial class PrivacyPageEndpoint : IGetEndpoint
{
    public static string Path => "/privacy";

    [Inject] private readonly ISparkSettings settings;
    [Inject] private readonly IOptions<SeoLegalOptions> legal;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var facts = new LegalPages.Facts(
            await settings.GetAsync(SeoSettings.AuditLogRetention, ct),
            await settings.GetAsync(SeoSettings.TargetCoolDown, ct));
        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Content(LegalPages.RenderPrivacy(ServerPage.LanguageFor(httpContext.Request), legal.Value, facts), "text/html; charset=utf-8", Encoding.UTF8);
    }
}

/// <summary><c>GET /terms</c>: the terms of use (D47). Anonymous, no script; English or Dutch as <c>/bot</c>.</summary>
internal sealed partial class TermsPageEndpoint : IGetEndpoint
{
    public static string Path => "/terms";

    [Inject] private readonly IOptions<SeoLegalOptions> legal;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Task.FromResult(Results.Content(LegalPages.RenderTerms(ServerPage.LanguageFor(httpContext.Request), legal.Value), "text/html; charset=utf-8", Encoding.UTF8));
    }
}
