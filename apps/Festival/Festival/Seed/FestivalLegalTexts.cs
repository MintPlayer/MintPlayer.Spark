namespace Festival.Seed;

/// <summary>
/// The demo's legal texts (version 1, platform-wide). Placeholders that show where a host's own texts go: they are
/// **not** legal advice and not a model for a real shop. A real host supplies texts written with counsel (D37).
/// </summary>
internal static class FestivalLegalTexts
{
    public const string Terms = """
        # Terms of sale (demo)

        This is a demonstration shop. No order placed here is real, no payment is taken and nothing is shipped.

        - Tickets are sold by the organiser named on the event page (the trader page shows its details).
        - Merchandise can be withdrawn from within 14 days after delivery: use the "Withdraw from contract here" button on
          your order page.
        - Tickets for a leisure event on a specific date carry no right of withdrawal (Directive 2011/83/EU Art. 16(l)).
        """;

    public const string Privacy = """
        # Privacy policy (demo)

        The demo stores the account, order and ticket data needed to run a sale. Each seller is the controller of its own
        buyers' data; the platform processes it on the seller's behalf. Export or erase your data from your account page.
        """;

    public const string Withdrawal = """
        # Right of withdrawal (demo)

        You may withdraw from a purchase of goods within 14 days without giving a reason. The period ends 14 days after the
        day you (or a third party you named) received the last item. Use the "Withdraw from contract here" button on your
        order page, or send the model withdrawal form. We refund every payment received, including the standard delivery
        cost, within 14 days, and may withhold it until we have received the goods back.
        """;

    public const string Cookies = """
        # Cookies (demo)

        Strictly necessary cookies keep your session, your basket and your consent choice. Anything else (third-party
        challenges, analytics, marketing mail) is used only after you agree, and you can change your choice at any time
        on the consent settings page.
        """;

    public const string Accessibility = """
        # Accessibility statement (demo)

        This storefront aims to meet WCAG 2.1 level AA (European Accessibility Act, Directive (EU) 2019/882, Annex V).
        Automated axe checks run on the storefront, basket, checkout, waiting room, seat picker, withdrawal function and
        consent banner.

        **Time limits.** A ticket reservation is held for a limited time. The basket shows the deadline as text, announces
        it politely, and offers "More time" at least 20 seconds before it ends, up to the seller's maximum. Beyond that
        maximum the hold is a real-time limit that protects other buyers' access to scarce tickets (the "real-time
        exception" of WCAG 2.2.1; to be confirmed against W3C's Understanding 2.2.1).

        Report a problem: accessibility@festival.example.
        """;
}
