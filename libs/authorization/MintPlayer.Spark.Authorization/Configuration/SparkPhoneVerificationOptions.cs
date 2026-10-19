using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Phone verification (<c>docs/commerce_PRD.md</c> X13, D29), bound from <c>Spark:Auth:PhoneVerification</c>,
/// then code. Opt-in: nothing of it exists until <c>spark.AddPhoneVerification()</c>.
/// </summary>
/// <remarks>
/// Every limit counts <b>sent</b> messages, because a sent message is what costs money and what an SMS-pumping
/// attack is after. The per-number limit is shared by every account, so many accounts cannot point one number
/// (or one premium-rate range) at the sender either.
/// </remarks>
public sealed class SparkPhoneVerificationOptions : ISparkConfigurationSection
{
    /// <summary>The configuration section the options are bound from.</summary>
    public static string SectionName => "Spark:Auth:PhoneVerification";

    /// <summary>Digits in a code, 4 to 10. Default 6.</summary>
    public int CodeLength { get; set; } = 6;

    /// <summary>How long a code stays valid. Default 10 minutes.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Wrong guesses a code survives; the next wrong one voids it. Default 5.</summary>
    public int MaxAttemptsPerCode { get; set; } = 5;

    /// <summary>The least time between two codes sent for the same account. Default 60 seconds.</summary>
    public TimeSpan ResendInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The window <see cref="MaxSendsPerUser"/> and <see cref="MaxSendsPerNumber"/> count over. Default 24 hours.</summary>
    public TimeSpan SendWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Codes one account may have sent per <see cref="SendWindow"/>, to any numbers. Default 5.</summary>
    public int MaxSendsPerUser { get; set; } = 5;

    /// <summary>Codes one number may receive per <see cref="SendWindow"/>, from all accounts together. Default 5.</summary>
    public int MaxSendsPerNumber { get; set; } = 5;

    /// <summary>
    /// The country calling code (digits only, <c>32</c> for Belgium) a national number written with a leading
    /// trunk <c>0</c> is read in: <c>0470 12 34 56</c> becomes <c>+32470123456</c>. Null (the default): only
    /// international numbers (<c>+…</c> or <c>00…</c>) are accepted.
    /// </summary>
    public string? DefaultCountryCallingCode { get; set; }

    /// <summary>
    /// The country calling codes (digits only) a code may be sent to. Empty (the default): every country. A
    /// short list is the strongest guard against SMS pumping to premium-rate destinations.
    /// </summary>
    public List<string> AllowedCountryCallingCodes { get; set; } = [];

    /// <summary>
    /// Whether a number verifies one account only. Default true: a verified phone number is the anti-bot signal
    /// (D29), and it means nothing if one number can vouch for any number of accounts.
    /// </summary>
    public bool RequireUniqueNumber { get; set; } = true;

    /// <summary>
    /// The message text. <c>{code}</c> is replaced with the code and <c>{minutes}</c> with
    /// <see cref="CodeLifetime"/> in whole minutes.
    /// </summary>
    public string MessageTemplate { get; set; } = "Your verification code is {code}. It expires in {minutes} minutes.";
}
