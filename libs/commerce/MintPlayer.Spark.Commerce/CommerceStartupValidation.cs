using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Commerce.Retention;

namespace MintPlayer.Spark.Commerce;

/// <summary>The outcome of <see cref="CommerceStartupValidation.Evaluate"/>: refusals stop the app, warnings are logged.</summary>
public sealed record CommerceValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// Startup validation of <c>Spark:Commerce</c> (§4.2). Runs from <c>Registry.AddMiddleware</c> (the Notifications
/// pattern): a refusal throws before the app serves a request, so a misconfigured scenario never sells.
/// </summary>
/// <remarks>
/// Legal citations are to the articles the gap analysis named; each is to be re-read against EUR-Lex before release
/// (D37). The geo-blocking check validates configuration only; it does not judge custom code.
/// </remarks>
public static class CommerceStartupValidation
{
    /// <summary>Country keys a buyer may not be refused, redirected or treated differently on (Geo-blocking Regulation
    /// (EU) 2018/302 Art. 3 (access, redirection), Art. 4 (conditions), Art. 5 (payment: card-issuing location)).</summary>
    private static readonly HashSet<CountryKey> ForbiddenKeys =
        [CountryKey.Nationality, CountryKey.Residence, CountryKey.CardIssuingCountry, CountryKey.IpCountry];

    /// <summary>Evaluates every rule; pure, for tests.</summary>
    /// <param name="options">The bound options.</param>
    /// <param name="payments">The payment providers' declared capabilities.</param>
    /// <param name="countryRules">Country-keyed rules declared by other packages.</param>
    /// <param name="floors">The retention floors in force.</param>
    /// <param name="unmetConsent">Consent requirements no consent primitive can meet (GA9).</param>
    /// <param name="hasKycSource">Whether any <see cref="ISellerKycSource"/> is registered.</param>
    public static CommerceValidationResult Evaluate(
        SparkCommerceOptions options,
        IReadOnlyList<ICommercePaymentCapabilities> payments,
        IEnumerable<CountryKeyedRule> countryRules,
        IReadOnlyDictionary<string, RetentionFloor> floors,
        IReadOnlyList<SparkConsentRequirement> unmetConsent,
        bool hasKycSource)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        const string root = "Spark:Commerce";

        if (!Money.IsKnownCurrency(options.DefaultCurrency))
            errors.Add($"{root}:DefaultCurrency '{options.DefaultCurrency}' is not an ISO 4217 currency.");

        // D21: an open platform would sell for strangers in its own name.
        if (options.Sellers.Registration == SellerRegistrationMode.Open && options.Merchant == MerchantMode.Platform
            && !options.Sellers.AcknowledgeOpenPlatformMerchant)
            errors.Add($"{root}:Sellers:Registration=Open with Merchant=Platform makes the platform the merchant of record for anyone who registers. "
                + $"Use ApprovalRequired or ConnectedSellers, or set {root}:Sellers:AcknowledgeOpenPlatformMerchant=true (D21).");

        // GA5: DSA Art. 30 trader traceability may only be switched off with the Art. 29 acknowledgement.
        if (!options.Sellers.TraderTraceability && !options.Sellers.AcknowledgeDsaArt29Exemption)
            errors.Add($"{root}:Sellers:TraderTraceability=false needs {root}:Sellers:AcknowledgeDsaArt29Exemption=true: "
                + "only micro and small enterprises are exempt (Regulation (EU) 2022/2065 Art. 29), and the host states it qualifies (GA5).");
        if (!options.Sellers.ComplaintHandling && !options.Sellers.AcknowledgeP2BSmallEnterprise)
            errors.Add($"{root}:Sellers:ComplaintHandling=false needs {root}:Sellers:AcknowledgeP2BSmallEnterprise=true "
                + "(Regulation (EU) 2019/1150 Art. 11(5) exempts small enterprises only) (GA5).");

        if (options.Merchant == MerchantMode.ConnectedSellers && !hasKycSource)
            warnings.Add($"{root}:Merchant=ConnectedSellers but no payments package reports seller KYC (ISellerKycSource): no seller can sell until one is added (D21).");

        // D40: one payment across sellers needs a provider that splits.
        if (options.Basket.MixSellers)
        {
            if (options.Merchant != MerchantMode.ConnectedSellers)
                warnings.Add($"{root}:Basket:MixSellers only applies with Merchant=ConnectedSellers; it is ignored.");
            else if (payments.Count == 0)
                warnings.Add($"{root}:Basket:MixSellers=true but no payment provider is registered yet; it is checked again when one is (D40).");
            else
                foreach (var provider in payments.Where(p => !p.SupportsSplit))
                    errors.Add($"{root}:Basket:MixSellers=true, but the payment provider '{provider.Provider}' cannot split one payment across sellers (D40).");
        }

        if (options.Channels.Count == 0)
            warnings.Add($"{root}:Channels is empty: nothing can be sold.");

        foreach (var (name, channel) in options.Channels)
        {
            var path = $"{root}:Channels:{name}";
            if (channel.Allocation != AllocationMode.OnPayment)
            {
                if (!channel.Hold.IsValid)
                    errors.Add($"{path}:Hold must satisfy 0 ≤ Min ≤ Default ≤ Max.");
                if (channel.MaxPaymentWindow <= TimeSpan.Zero)
                    errors.Add($"{path}:MaxPaymentWindow must be positive (D3).");
            }
            if (!channel.Refunds.DaysBefore.IsValid)
                errors.Add($"{path}:Refunds:DaysBefore must satisfy Min ≤ Default ≤ Max.");
            if (channel.Refunds.MaxFeeBasisPoints is < 0 or > 10_000)
                errors.Add($"{path}:Refunds:MaxFeeBasisPoints must be between 0 and 10000.");

            // D10: one entry per identity needs a signed-in identity.
            var needsSignIn = channel.WaitingRoom.Modes.Concat(channel.WaitingRoom.Default is { } d ? [d] : [])
                .Where(m => m is WaitingRoomMode.RandomizedPreQueue or WaitingRoomMode.Ballot).Distinct().ToArray();
            if (needsSignIn.Length > 0 && !channel.RequireSignIn && !channel.WaitingRoom.AllowWithoutSignIn)
                errors.Add($"{path}: waiting-room mode(s) {string.Join(", ", needsSignIn)} need RequireSignIn=true (one entry per identity), "
                    + $"or {path}:WaitingRoom:AllowWithoutSignIn=true (D10).");
            if (channel.WaitingRoom.Default is { } def && channel.WaitingRoom.Modes.Count > 0 && !channel.WaitingRoom.Modes.Contains(def))
                errors.Add($"{path}:WaitingRoom:Default '{def}' is not one of its Modes.");

            // D29: a deceptive measure never runs against every buyer.
            foreach (var (measure, setting) in channel.DeceptiveMeasures)
                if (setting.Enabled && string.IsNullOrWhiteSpace(setting.Trigger))
                    errors.Add($"{path}:DeceptiveMeasures:{measure} is enabled without a Trigger (D29).");

            // D4/D16: a per-buyer limit is weak when guests can buy (a new visitor per browser).
            if (channel.PerBuyerLimit is not null && !channel.RequireSignIn)
                warnings.Add($"{path}:PerBuyerLimit is set but guests may buy (RequireSignIn=false): the limit is per visitor, e-mail and address, which a determined buyer can vary (D4).");

            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fee in channel.Fees)
            {
                if (string.IsNullOrWhiteSpace(fee.Code))
                    errors.Add($"{path}:Fees: every fee needs a Code.");
                else if (!codes.Add(fee.Code))
                    errors.Add($"{path}:Fees: the code '{fee.Code}' is used twice.");
                if (fee.PercentBasisPoints < 0 || fee.Fixed.Values.Any(v => v < 0))
                    errors.Add($"{path}:Fees:{fee.Code} is negative.");
                foreach (var currency in fee.Fixed.Keys.Where(c => !Money.IsKnownCurrency(c)))
                    errors.Add($"{path}:Fees:{fee.Code}:Fixed uses an unknown currency '{currency}'.");
                if (fee.PercentBasisPoints == 0 && fee.Fixed.Values.All(v => v == 0))
                    warnings.Add($"{path}:Fees:{fee.Code} charges nothing (no fixed amount and no percentage).");
            }

            CheckCountryRules(channel.CountryRules.Select(r => ToRule($"{path}:CountryRules:{r.Name}", r)), errors, warnings);
        }

        // GA11: Geo-blocking Regulation (EU) 2018/302.
        CheckCountryRules(options.CountryRules.Select(r => ToRule($"{root}:CountryRules:{r.Name}", r)).Concat(countryRules), errors, warnings);

        // GA9: consent-requiring features without the consent primitive.
        if (unmetConsent.Count > 0)
            errors.Add("These features need the visitor's consent but the consent primitive is not registered (spark.AddConsent()) or the purpose is not declared: "
                + string.Join("; ", unmetConsent.Select(r => $"{r.RequiredBy} needs '{r.Purpose}'")) + " (GA9).");

        // §4.15: retention never below the legal floor.
        foreach (var (type, period) in options.Retention.Periods)
        {
            if (floors.TryGetValue(type, out var floor) && period < floor.Floor)
                errors.Add($"{root}:Retention:Periods:{type} = {period.TotalDays:0} days is below the legal floor of {floor.Floor.TotalDays:0} days "
                    + $"({floor.LegalBasis}{(floor.Confirmed ? "" : "; to be confirmed per country")}).");
            if (string.Equals(type, CommerceDocumentTypes.Dac7Records, StringComparison.OrdinalIgnoreCase) && period > TimeSpan.FromDays(3653))
                warnings.Add($"{root}:Retention:Periods:{type} is longer than the 10 years DAC7 allows records to be kept.");
        }
        foreach (var floor in floors.Values.Where(f => f.Confirmed))
            warnings.Add($"Retention floor for '{floor.DocumentType}' overridden by the host: {floor.Floor.TotalDays:0} days.");

        return new CommerceValidationResult(errors, warnings);
    }

    private static CountryKeyedRule ToRule(string source, CountryRuleOptions r) => new(source, r.Key, r.Effect, r.Countries);

    private static void CheckCountryRules(IEnumerable<CountryKeyedRule> rules, List<string> errors, List<string> warnings)
    {
        foreach (var rule in rules)
        {
            if (!ForbiddenKeys.Contains(rule.Key))
                continue;
            if (rule.Key == CountryKey.IpCountry && rule.Effect == CountryRuleEffect.RiskSignal)
            {
                warnings.Add($"{rule.Source}: an IP-country risk signal may only add a check (a challenge), never refuse or change conditions "
                    + "(Regulation (EU) 2018/302 Art. 3) (GA11).");
                continue;
            }
            errors.Add($"{rule.Source}: a rule keyed on the buyer's {Describe(rule.Key)} that does '{rule.Effect}' is forbidden by the Geo-blocking Regulation "
                + $"(EU) 2018/302 ({Article(rule.Key)}). Delivery zones keyed on the delivery address stay allowed (GA11).");
        }
    }

    private static string Describe(CountryKey key) => key switch
    {
        CountryKey.Nationality => "nationality",
        CountryKey.Residence => "place of residence",
        CountryKey.CardIssuingCountry => "card-issuing country",
        CountryKey.IpCountry => "IP-address country",
        _ => key.ToString(),
    };

    private static string Article(CountryKey key) => key switch
    {
        CountryKey.CardIssuingCountry => "Art. 5(1)",
        CountryKey.IpCountry => "Art. 3",
        _ => "Art. 3–5",
    };

    /// <summary>Runs <see cref="Evaluate"/> against the app's services: logs warnings, throws on refusals.</summary>
    internal static void Validate(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkCommerceOptions>>().Value;
        var payments = services.GetServices<ICommercePaymentCapabilities>().ToArray();
        var rules = services.GetServices<ICountryKeyedRuleSource>().SelectMany(s => s.GetRules()).ToArray();
        var floors = CommerceRetention.Build(options, services.GetServices<IRetentionFloorSource>());
        var paymentConsent = payments
            .Where(p => !string.IsNullOrWhiteSpace(p.RequiresConsentPurpose))
            .Select(p => new SparkConsentRequirement(p.RequiresConsentPurpose!, $"payment provider '{p.Provider}'"));
        var unmet = SparkConsentRegistration.FindUnmet(services, paymentConsent);
        bool hasKyc;
        using (var scope = services.CreateScope())
            hasKyc = scope.ServiceProvider.GetServices<ISellerKycSource>().Any();

        var result = Evaluate(options, payments, rules, floors, unmet, hasKyc);
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Commerce");
        foreach (var warning in result.Warnings)
            logger?.LogWarning("Commerce configuration: {Warning}", warning);
        if (result.Errors.Count > 0)
            throw new InvalidOperationException("Spark:Commerce is misconfigured:" + Environment.NewLine
                + string.Join(Environment.NewLine, result.Errors.Select(e => " - " + e)));
    }
}
