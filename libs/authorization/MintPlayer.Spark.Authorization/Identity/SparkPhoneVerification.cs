using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Sms;
using Raven.Client;
using Raven.Client.Documents;
using MintPlayer.Spark.Claims;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Phone verification (<c>docs/commerce_PRD.md</c> X13, D29): a one-time code is sent by SMS
/// (<see cref="ISparkSmsSender"/>) to the number the user gives, and entering it marks the number verified
/// on the user (<see cref="SparkUser.PhoneNumber"/>, <see cref="SparkUser.PhoneNumberConfirmed"/>). Opt-in:
/// registered by <c>spark.AddPhoneVerification()</c>, which also maps <c>/spark/auth/manage/phone*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Limits</b> (<see cref="SparkPhoneVerificationOptions"/>): a resend interval and a cap per account, a cap
/// per number shared by every account, a lifetime and an attempt cap per code. Each send and each guess is
/// written before it takes effect, under optimistic concurrency, so parallel requests cannot slip past a cap.
/// </para>
/// <para>
/// <b>Codes are stored hashed</b>: HMAC-SHA256 under a random per-code salt, over the user id, the number and
/// the code, so a code is good only for the account and number it was sent for. A database read yields no code.
/// </para>
/// </remarks>
public interface ISparkPhoneVerification
{
    /// <summary>
    /// Sends a code to <paramref name="phoneNumber"/> (normalized to E.164) for <paramref name="userId"/> to
    /// enter. A new code replaces the pending one.
    /// </summary>
    Task<SparkPhoneVerificationResult> SendCodeAsync(string userId, string phoneNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks <paramref name="code"/> against <paramref name="userId"/>'s pending code; when it matches, the number
    /// becomes the user's verified phone number and the code is used up.
    /// </summary>
    Task<SparkPhoneVerificationResult> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default);

    /// <summary>The user's verified number and the pending one, for the account page.</summary>
    Task<SparkPhoneVerificationStatusInfo> GetStatusAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>What a send or a verify answered.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="PhoneNumber">The E.164 number the outcome is about, when there is one.</param>
/// <param name="ExpiresAtUtc">When a sent code stops being valid.</param>
/// <param name="RetryAfter">With <see cref="SparkPhoneVerificationStatus.RateLimited"/>: how long until the next send is allowed.</param>
/// <param name="AttemptsLeft">With <see cref="SparkPhoneVerificationStatus.WrongCode"/>: guesses the code survives.</param>
public sealed record SparkPhoneVerificationResult(
    SparkPhoneVerificationStatus Status,
    string? PhoneNumber = null,
    DateTime? ExpiresAtUtc = null,
    TimeSpan? RetryAfter = null,
    int? AttemptsLeft = null);

/// <summary>The account page's view: the verified number, and the number a code is pending for.</summary>
public sealed record SparkPhoneVerificationStatusInfo(string? PhoneNumber, bool PhoneNumberVerified, string? PendingPhoneNumber, DateTime? PendingExpiresAtUtc);

/// <summary>The outcome of a send or a verify.</summary>
public enum SparkPhoneVerificationStatus
{
    /// <summary>A code was handed to the SMS sender.</summary>
    CodeSent,
    /// <summary>The code matched: the number is now the user's verified phone number.</summary>
    Verified,
    /// <summary>The number is already the user's verified number; nothing was sent.</summary>
    AlreadyVerified,
    /// <summary>Not a phone number (<see cref="SparkPhoneNumbers.TryNormalize"/>).</summary>
    InvalidNumber,
    /// <summary>The number's country is not in <see cref="SparkPhoneVerificationOptions.AllowedCountryCallingCodes"/>.</summary>
    CountryNotAllowed,
    /// <summary>A send limit was reached; see <see cref="SparkPhoneVerificationResult.RetryAfter"/>.</summary>
    RateLimited,
    /// <summary>The SMS vendor refused the message. The send still counts toward the limits.</summary>
    NotSent,
    /// <summary>No code is pending: none was sent, or the last one was used or voided.</summary>
    NoPendingCode,
    /// <summary>The pending code expired; it is voided.</summary>
    Expired,
    /// <summary>The code did not match; see <see cref="SparkPhoneVerificationResult.AttemptsLeft"/>.</summary>
    WrongCode,
    /// <summary>Too many wrong guesses: the code is voided and a new one must be sent.</summary>
    TooManyAttempts,
    /// <summary>Another account verified this number already (<see cref="SparkPhoneVerificationOptions.RequireUniqueNumber"/>).</summary>
    NumberInUse,
    /// <summary>No such user.</summary>
    UnknownUser,
}

/// <summary>
/// One account's pending code and its send history (<see cref="SparkPhoneVerification{TUser}"/>):
/// <c>SparkPhoneVerifications/{userId}</c>. Carries <c>@expires</c> one send window after the last send.
/// </summary>
public sealed class SparkPhoneVerificationState
{
    /// <summary>The id prefix.</summary>
    public const string IdPrefix = "SparkPhoneVerifications/";

    /// <summary><c>SparkPhoneVerifications/{userId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The E.164 number the pending code was sent to.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Base64 HMAC-SHA256 of the pending code; null when none is pending.</summary>
    public string? CodeHash { get; set; }

    /// <summary>Base64 random key of <see cref="CodeHash"/>, new per code.</summary>
    public string? CodeSalt { get; set; }

    /// <summary>When the pending code stops being valid.</summary>
    public DateTime? CodeExpiresAtUtc { get; set; }

    /// <summary>Wrong guesses at the pending code.</summary>
    public int Attempts { get; set; }

    /// <summary>When codes were sent for this account, within the send window.</summary>
    public List<DateTime> SentAtUtc { get; set; } = [];

    /// <summary>The id of <paramref name="userId"/>'s state.</summary>
    public static string IdFor(string userId) => IdPrefix + userId;
}

/// <summary>
/// One number's send history, shared by every account (<see cref="SparkPhoneVerificationOptions.MaxSendsPerNumber"/>):
/// <c>SparkPhoneNumberSends/{SHA-256 of the number}</c>. The number itself is not stored. Carries <c>@expires</c>.
/// </summary>
public sealed class SparkPhoneNumberSends
{
    /// <summary>The id prefix.</summary>
    public const string IdPrefix = "SparkPhoneNumberSends/";

    /// <summary><c>SparkPhoneNumberSends/{hex SHA-256 of the E.164 number}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>When codes were sent to this number, within the send window.</summary>
    public List<DateTime> SentAtUtc { get; set; } = [];

    /// <summary>The id for <paramref name="e164"/>.</summary>
    public static string IdFor(string e164) => IdPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(e164)));
}

/// <inheritdoc cref="ISparkPhoneVerification" />
internal sealed class SparkPhoneVerification<TUser>(
    IDocumentStore store,
    UserManager<TUser> userManager,
    IUserStore<TUser> userStore,
    ISparkSmsSender sms,
    IOptions<SparkPhoneVerificationOptions> options,
    TimeProvider? timeProvider = null) : ISparkPhoneVerification
    where TUser : SparkUser, new()
{
    internal const string Purpose = "phone-verification";

    private SparkPhoneVerificationOptions Options => options.Value;
    private DateTime Now => (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task<SparkPhoneVerificationResult> SendCodeAsync(string userId, string phoneNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var o = Options;
        if (!SparkPhoneNumbers.TryNormalize(phoneNumber, o.DefaultCountryCallingCode, out var e164))
            return new(SparkPhoneVerificationStatus.InvalidNumber);
        if (!SparkPhoneNumbers.IsInCountries(e164, o.AllowedCountryCallingCodes))
            return new(SparkPhoneVerificationStatus.CountryNotAllowed, e164);

        if (await userManager.FindByIdAsync(userId) is not { } user)
            return new(SparkPhoneVerificationStatus.UnknownUser);
        if (user.PhoneNumberConfirmed && string.Equals(user.PhoneNumber, e164, StringComparison.Ordinal))
            return new(SparkPhoneVerificationStatus.AlreadyVerified, e164);

        var now = Now;
        var windowStart = now - o.SendWindow;
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;

        var stateId = SparkPhoneVerificationState.IdFor(userId);
        var numberId = SparkPhoneNumberSends.IdFor(e164);
        var storedState = await session.LoadAsync<SparkPhoneVerificationState>(stateId, cancellationToken);
        var storedNumber = await session.LoadAsync<SparkPhoneNumberSends>(numberId, cancellationToken);
        var state = storedState ?? new SparkPhoneVerificationState { Id = stateId };
        var number = storedNumber ?? new SparkPhoneNumberSends { Id = numberId };
        state.SentAtUtc.RemoveAll(t => t <= windowStart);
        number.SentAtUtc.RemoveAll(t => t <= windowStart);

        if (state.SentAtUtc.Count > 0)
        {
            var nextAllowed = state.SentAtUtc.Max() + o.ResendInterval;
            if (nextAllowed > now)
                return RateLimited(e164, nextAllowed - now);
        }
        if (state.SentAtUtc.Count >= o.MaxSendsPerUser)
            return RateLimited(e164, state.SentAtUtc.Min() + o.SendWindow - now);
        if (number.SentAtUtc.Count >= o.MaxSendsPerNumber)
            return RateLimited(e164, number.SentAtUtc.Min() + o.SendWindow - now);

        var code = NewCode(o.CodeLength);
        var salt = RandomNumberGenerator.GetBytes(32);
        state.PhoneNumber = e164;
        state.CodeSalt = Convert.ToBase64String(salt);
        state.CodeHash = Hash(salt, userId, e164, code);
        state.CodeExpiresAtUtc = now + o.CodeLifetime;
        state.Attempts = 0;
        state.SentAtUtc.Add(now);
        number.SentAtUtc.Add(now);

        // A new document is stored with an empty change vector: "must not exist yet", so two first sends race
        // on it like two later ones race on the loaded change vector.
        if (storedState is null)
            await session.StoreAsync(state, string.Empty, stateId, cancellationToken);
        if (storedNumber is null)
            await session.StoreAsync(number, string.Empty, numberId, cancellationToken);
        var expires = (now + o.SendWindow > state.CodeExpiresAtUtc.Value ? now + o.SendWindow : state.CodeExpiresAtUtc.Value).ToString("O", CultureInfo.InvariantCulture);
        session.Advanced.GetMetadataFor(state)[Constants.Documents.Metadata.Expires] = expires;
        session.Advanced.GetMetadataFor(number)[Constants.Documents.Metadata.Expires] = (now + o.SendWindow).ToString("O", CultureInfo.InvariantCulture);

        // The send is recorded before the message goes out: a crash or a vendor error after this point has
        // still used a slot, so retries cannot multiply messages past the caps.
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            // Another send for this account or number committed in between: it is the one that counts.
            return RateLimited(e164, o.ResendInterval);
        }

        var body = o.MessageTemplate
            .Replace("{code}", code, StringComparison.Ordinal)
            .Replace("{minutes}", ((int)Math.Ceiling(o.CodeLifetime.TotalMinutes)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var receipt = await sms.SendAsync(new SparkSmsMessage(e164, body, Purpose), cancellationToken);
        return receipt.Accepted
            ? new(SparkPhoneVerificationStatus.CodeSent, e164, state.CodeExpiresAtUtc)
            : new(SparkPhoneVerificationStatus.NotSent, e164);
    }

    public async Task<SparkPhoneVerificationResult> VerifyAsync(string userId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var o = Options;
        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;

        var state = await session.LoadAsync<SparkPhoneVerificationState>(SparkPhoneVerificationState.IdFor(userId), cancellationToken);
        if (state is not { CodeHash: { } hash, CodeSalt: { } salt, PhoneNumber: { } e164, CodeExpiresAtUtc: { } expiresAt })
            return new(SparkPhoneVerificationStatus.NoPendingCode);

        if (Now >= expiresAt)
        {
            Void(state);
            await TrySaveAsync(session, cancellationToken);
            return new(SparkPhoneVerificationStatus.Expired, e164);
        }

        var entered = new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        var matches = entered.Length == o.CodeLength
            && entered.All(char.IsAsciiDigit)
            && CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(hash),
                Convert.FromBase64String(Hash(Convert.FromBase64String(salt), userId, e164, entered)));

        if (!matches)
        {
            // The guess is counted under optimistic concurrency: a burst of parallel guesses commits one at a
            // time, and the losers are refused without being judged.
            state.Attempts++;
            var left = o.MaxAttemptsPerCode - state.Attempts;
            if (left <= 0)
                Void(state);
            if (!await TrySaveAsync(session, cancellationToken))
                return new(SparkPhoneVerificationStatus.RateLimited, e164, RetryAfter: TimeSpan.FromSeconds(1));
            return left <= 0
                ? new(SparkPhoneVerificationStatus.TooManyAttempts, e164)
                : new(SparkPhoneVerificationStatus.WrongCode, e164, AttemptsLeft: left);
        }

        // Single use: the code is consumed before the user changes, so a replay finds nothing pending.
        Void(state);
        if (!await TrySaveAsync(session, cancellationToken))
            return new(SparkPhoneVerificationStatus.NoPendingCode, e164);

        if (await userManager.FindByIdAsync(userId) is not { } user)
            return new(SparkPhoneVerificationStatus.UnknownUser);

        if (o.RequireUniqueNumber && !await ReserveNumberAsync(e164, userId, cancellationToken))
            return new(SparkPhoneVerificationStatus.NumberInUse, e164);

        var previous = user.PhoneNumberConfirmed ? user.PhoneNumber : null;
        var phones = userStore as IUserPhoneNumberStore<TUser>
            ?? throw new InvalidOperationException($"{userStore.GetType().Name} does not store phone numbers (IUserPhoneNumberStore<{typeof(TUser).Name}>).");
        await phones.SetPhoneNumberAsync(user, e164, cancellationToken);
        await phones.SetPhoneNumberConfirmedAsync(user, true, cancellationToken);
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("The verified phone number could not be saved: " + string.Join("; ", result.Errors.Select(e => e.Code)));

        if (o.RequireUniqueNumber && previous is { Length: > 0 } && !string.Equals(previous, e164, StringComparison.Ordinal))
            await ReleaseNumberAsync(previous, userId, cancellationToken);
        return new(SparkPhoneVerificationStatus.Verified, e164);
    }

    /// <summary>The compare-exchange key prefix of the phone-number claims (commerce X12/X13).</summary>
    internal const string ReservationKeyPrefix = "spark-phone/";

    internal static string ReservationKey(string e164) => ReservationKeyPrefix + e164;

    private SparkClaims? claims;
    private SparkClaimKind<string>? numberKind;

    /// <summary>Spark's claims primitive (commerce X12), on the store's database.</summary>
    private SparkClaims Claims => claims ??= new SparkClaims(store, timeProvider);

    /// <summary>
    /// Verified numbers as a claim family (X12): the value is the user id and never expires. A claim whose account no
    /// longer holds the number as its verified number is abandoned (the account was deleted or changed number) and is
    /// taken over, guarded by the compare-exchange index just read. No index query, so nothing waits on staleness.
    /// </summary>
    private SparkClaimKind<string> NumberKind => numberKind ??= new SparkClaimKind<string>(ReservationKeyPrefix, owner => owner)
    {
        IsAbandonedAsync = async (claim, _) => !await IsVerifiedNumberOfAsync(claim.Value, claim.Key.Value[ReservationKeyPrefix.Length..]),
    };

    /// <summary>
    /// Reserves <paramref name="e164"/> for <paramref name="userId"/> through an X12 claim, so two accounts verifying
    /// one number at the same instant cannot both win.
    /// </summary>
    private async Task<bool> ReserveNumberAsync(string e164, string userId, CancellationToken cancellationToken)
        => (await Claims.ReserveAsync(NumberKind, NumberKind.Key(e164), userId, _ => userId, cancellationToken: cancellationToken)).Holds;

    /// <summary>Releases <paramref name="e164"/>'s claim, but only while it is <paramref name="userId"/>'s.</summary>
    private Task ReleaseNumberAsync(string e164, string userId, CancellationToken cancellationToken)
        => Claims.ReleaseAsync(NumberKind, NumberKind.Key(e164), userId, cancellationToken);

    private async Task<bool> IsVerifiedNumberOfAsync(string? holderId, string e164)
        => !string.IsNullOrEmpty(holderId)
            && await userManager.FindByIdAsync(holderId) is { PhoneNumberConfirmed: true } holder
            && string.Equals(holder.PhoneNumber, e164, StringComparison.Ordinal);

    public async Task<SparkPhoneVerificationStatusInfo> GetStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var user = await userManager.FindByIdAsync(userId);
        using var session = store.OpenAsyncSession();
        var state = await session.LoadAsync<SparkPhoneVerificationState>(SparkPhoneVerificationState.IdFor(userId), cancellationToken);
        var pending = state is { CodeHash: not null, CodeExpiresAtUtc: { } expiresAt } && expiresAt > Now ? state : null;
        return new SparkPhoneVerificationStatusInfo(
            user?.PhoneNumber,
            user is { PhoneNumberConfirmed: true, PhoneNumber.Length: > 0 },
            pending?.PhoneNumber,
            pending?.CodeExpiresAtUtc);
    }

    private static SparkPhoneVerificationResult RateLimited(string e164, TimeSpan retryAfter)
        => new(SparkPhoneVerificationStatus.RateLimited, e164, RetryAfter: retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);

    private static void Void(SparkPhoneVerificationState state)
    {
        state.CodeHash = null;
        state.CodeSalt = null;
        state.CodeExpiresAtUtc = null;
        state.Attempts = 0;
    }

    private static async Task<bool> TrySaveAsync(IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>A uniformly random code of <paramref name="length"/> digits (4 to 10).</summary>
    internal static string NewCode(int length)
    {
        length = Math.Clamp(length, 4, 10);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        return builder.ToString();
    }

    /// <summary>HMAC-SHA256 under <paramref name="salt"/> over the user, the number and the code.</summary>
    internal static string Hash(byte[] salt, string userId, string e164, string code)
        => Convert.ToBase64String(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(userId + "\n" + e164 + "\n" + code)));
}
