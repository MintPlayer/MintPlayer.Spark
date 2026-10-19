using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Visitor;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Consent;

/// <summary>
/// The consent primitive (commerce PRD X21, GA9): records per visitor (X1), account or e-mail address; the staleness
/// rules (policy version, validity); unsubscribe tokens; GDPR export and erasure. Scoped.
/// </summary>
internal sealed class SparkConsentService(
    IDocumentStore store,
    IOptions<SparkConsentOptions> options,
    IOptions<SparkVisitorOptions> visitorOptions,
    IEnumerable<SparkConsentPurpose> declaredPurposes,
    IDataProtectionProvider dataProtection,
    IHttpContextAccessor accessor,
    IServiceProvider services) : ISparkConsent, ISparkConsentLinks, ISparkConsentPersonalData
{
    /// <summary>The Data Protection purpose of unsubscribe tokens. Changing it invalidates every link already mailed.</summary>
    internal const string UnsubscribeProtectorPurpose = "MintPlayer.Spark.Consent.Unsubscribe.v1";

    private const int WriteAttempts = 3;

    /// <summary>The declared purposes by name: the host's or a package's declaration beats a built-in one, else the last wins.</summary>
    private readonly IReadOnlyDictionary<string, SparkConsentPurpose> purposes = declaredPurposes
        .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            g => g.Key,
            g => g.LastOrDefault(p => !Extensions.SparkBuilderConsentExtensions.BuiltInPurposes.Contains(p, ReferenceEqualityComparer.Instance)) ?? g.Last(),
            StringComparer.OrdinalIgnoreCase);

    private TimeProvider Time => services.GetService<TimeProvider>() ?? TimeProvider.System;

    // ---- ISparkConsent -------------------------------------------------------------------------------------------

    public Task<bool> HasAsync(string purpose, CancellationToken cancellationToken = default)
        => CurrentSubject() is { } subject
            ? HasAsync(subject, purpose, cancellationToken)
            : Task.FromResult(IsNecessary(purpose));

    public async Task<bool> HasAsync(SparkConsentSubject subject, string purpose, CancellationToken cancellationToken = default)
    {
        if (IsNecessary(purpose))
            return true;
        if (!purposes.ContainsKey(purpose))
            return false;

        using var session = store.OpenAsyncSession();
        var record = await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(subject), cancellationToken);
        return record is not null && record.Decisions.TryGetValue(purpose, out var decision) && InForce(decision) == true;
    }

    public async Task<SparkConsentState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        SparkConsentRecord? record = null;
        if (CurrentSubject() is { } subject)
        {
            using var session = store.OpenAsyncSession();
            record = await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(subject), cancellationToken);
        }

        return StateOf(record);
    }

    public async Task<SparkConsentState> RecordAsync(SparkConsentChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var context = accessor.HttpContext
            ?? throw new InvalidOperationException("RecordAsync(choice) records the current request's decisions; outside a request, pass a subject.");
        var visitor = SparkVisitorFeature.For(context);
        var subject = visitor.UserId is { Length: > 0 } userId
            ? SparkConsentSubject.Account(userId)
            // The visitor cookie is strictly necessary here: it holds the choice itself (ePrivacy Art. 5(3) exemption).
            : SparkConsentSubject.Visitor(await visitor.EnsureAsync());

        var language = choice.Language ?? services.GetService<Services.IRequestCultureResolver>()?.GetCurrentCulture();
        var record = await WriteAsync(subject, choice with { Language = language }, cancellationToken);
        return StateOf(record);
    }

    public async Task RecordAsync(SparkConsentSubject subject, SparkConsentChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        await WriteAsync(subject, choice, cancellationToken);
    }

    // ---- ISparkConsentLinks --------------------------------------------------------------------------------------

    public string CreateUnsubscribeToken(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return Protector().Protect($"{(int)subject.Kind}\n{subject.Id}\n{purpose}");
    }

    public Uri CreateUnsubscribeUrl(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail)
    {
        var page = "/" + options.Value.UnsubscribePagePath.TrimStart('/');
        return new Uri(Origin() + page + "?token=" + Uri.EscapeDataString(CreateUnsubscribeToken(subject, purpose)));
    }

    public IReadOnlyDictionary<string, string> CreateListUnsubscribeHeaders(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail)
    {
        // RFC 8058 §3.1: an HTTPS URI in List-Unsubscribe plus List-Unsubscribe-Post: List-Unsubscribe=One-Click; the POST
        // carries no cookies, so the token in the URI is the whole credential.
        var oneClick = Origin() + Endpoints.SparkGroup.Prefix + "/consent/unsubscribe/one-click?token="
            + Uri.EscapeDataString(CreateUnsubscribeToken(subject, purpose));
        return new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = "<" + oneClick + ">",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };
    }

    /// <summary>The subject and purpose of a token, or <see langword="null"/> when it is forged, tampered or malformed.</summary>
    internal (SparkConsentSubject Subject, string Purpose)? ReadUnsubscribeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var parts = Protector().Unprotect(token).Split('\n');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var kind) || !Enum.IsDefined((SparkConsentSubjectKind)kind))
                return null;
            var subject = (SparkConsentSubjectKind)kind switch
            {
                SparkConsentSubjectKind.Account => SparkConsentSubject.Account(parts[1]),
                SparkConsentSubjectKind.Visitor => SparkConsentSubject.Visitor(parts[1]),
                _ => SparkConsentSubject.Email(parts[1]),
            };
            return (subject, parts[2]);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>What the unsubscribe page shows for <paramref name="token"/>; <see langword="null"/> for a bad token.</summary>
    internal async Task<SparkConsentUnsubscribeInfo?> DescribeUnsubscribeAsync(string? token, CancellationToken cancellationToken)
    {
        if (ReadUnsubscribeToken(token) is not { } read)
            return null;
        var granted = await HasAsync(read.Subject, read.Purpose, cancellationToken);
        var recipient = read.Subject.Kind == SparkConsentSubjectKind.Email ? Mask(read.Subject.Id) : "account";
        return new SparkConsentUnsubscribeInfo(read.Purpose, recipient, granted);
    }

    /// <summary>Withdraws the token's purpose (GDPR Art. 21(3)); <see langword="false"/> for a bad token. Idempotent.</summary>
    internal async Task<bool> UnsubscribeAsync(string? token, CancellationToken cancellationToken)
    {
        if (ReadUnsubscribeToken(token) is not { } read)
            return false;
        await WriteAsync(read.Subject, new SparkConsentChoice(new() { [read.Purpose] = false }, SparkConsentSources.Unsubscribe), cancellationToken, acceptUndeclared: true);
        return true;
    }

    // ---- ISparkConsentPersonalData -------------------------------------------------------------------------------

    public async Task<object?> ExportAsync(string userId, string? email, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var account = await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(SparkConsentSubject.Account(userId)), cancellationToken);
        var byEmail = email is { Length: > 0 }
            ? await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(SparkConsentSubject.Email(email)), cancellationToken)
            : null;
        if (account is null && byEmail is null)
            return null;
        return new { account = Export(account), email = Export(byEmail) };

        static object? Export(SparkConsentRecord? record) => record is null ? null : new
        {
            subject = record.SubjectKind.ToString(),
            address = record.Email,
            decisions = record.Decisions.Values,
            history = record.History,
        };
    }

    public async Task EraseAsync(string userId, string? email, CancellationToken cancellationToken)
    {
        var ids = new List<string> { SparkConsentRecord.IdFor(SparkConsentSubject.Account(userId)) };
        if (email is { Length: > 0 })
            ids.Add(SparkConsentRecord.IdFor(SparkConsentSubject.Email(email)));

        using var session = OpenWriteSession();
        foreach (var id in ids)
            if (await session.LoadAsync<SparkConsentRecord>(id, cancellationToken) is { } record)
                session.Delete(record);
        await session.SaveChangesAsync(cancellationToken);
    }

    // ---- shared --------------------------------------------------------------------------------------------------

    /// <summary>The current request's subject: the account when signed in, else the visitor, else none.</summary>
    private SparkConsentSubject? CurrentSubject()
    {
        if (accessor.HttpContext is not { } context)
            return null;
        var visitor = SparkVisitorFeature.For(context);
        if (visitor.UserId is { Length: > 0 } userId)
            return SparkConsentSubject.Account(userId);
        return visitor.VisitorId is { } visitorId ? SparkConsentSubject.Visitor(visitorId) : null;
    }

    private static bool IsNecessary(string purpose)
        => string.Equals(purpose, SparkConsentPurposes.Necessary, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The decision as it stands now: its <see cref="SparkConsentDecision.Granted"/>, or <see langword="null"/> when it is
    /// stale (taken under an older policy version, or older than its validity), so it must be asked again.
    /// </summary>
    internal bool? InForce(SparkConsentDecision decision)
    {
        var o = options.Value;
        if (o.NonExpiringPurposes.Contains(decision.Purpose, StringComparer.OrdinalIgnoreCase))
            return decision.Granted;
        if (!string.Equals(decision.PolicyVersion, o.PolicyVersion, StringComparison.Ordinal))
            return null;
        var validity = decision.Granted ? o.ConsentValidity : o.RefusalValidity;
        return Time.GetUtcNow() - decision.AtUtc > validity ? null : decision.Granted;
    }

    internal SparkConsentState StateOf(SparkConsentRecord? record)
    {
        var states = purposes.Values
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                SparkConsentDecision? decision = null;
                var granted = record is not null && record.Decisions.TryGetValue(p.Name, out decision) ? InForce(decision) : null;
                return new SparkConsentPurposeState(p.Name, p.Title, p.Description, p.Vendors ?? [], false, granted,
                    granted is null ? null : decision!.AtUtc);
            })
            .ToList();

        // A purpose whose decision stands until withdrawn (marketing mail) is asked where it applies (checkout, the
        // settings page), not by the banner: an undecided one does not make the banner appear.
        var nonExpiring = options.Value.NonExpiringPurposes;
        var needsPrompt = states.Any(s => s.Granted is null && !nonExpiring.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
        return new SparkConsentState(options.Value.PolicyVersion, needsPrompt, options.Value.PolicyUrl, states);
    }

    private IAsyncDocumentSession OpenWriteSession()
        => store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide });

    private async Task<SparkConsentRecord> WriteAsync(SparkConsentSubject subject, SparkConsentChoice choice, CancellationToken cancellationToken, bool acceptUndeclared = false)
    {
        var o = options.Value;
        var source = string.IsNullOrWhiteSpace(choice.Source) ? SparkConsentSources.Application : choice.Source.Trim();
        if (source.Length > 32)
            source = source[..32];
        var language = string.IsNullOrWhiteSpace(choice.Language) ? null : choice.Language.Trim();
        var id = SparkConsentRecord.IdFor(subject);

        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenWriteSession();
            var record = await session.LoadAsync<SparkConsentRecord>(id, cancellationToken);
            var isNew = record is null;
            record ??= new SparkConsentRecord
            {
                Id = id,
                SubjectKind = subject.Kind,
                SubjectId = subject.Kind == SparkConsentSubjectKind.Email ? null : subject.Id,
                Email = subject.Kind == SparkConsentSubjectKind.Email ? subject.Id : null,
            };

            var now = Time.GetUtcNow();
            foreach (var (name, granted) in choice.Decisions ?? [])
            {
                if (IsNecessary(name))
                    continue;
                var declared = purposes.TryGetValue(name, out var purpose);
                // A withdrawal is honoured even for a purpose no longer declared (an old unsubscribe link); a grant never is.
                if (!declared && !(acceptUndeclared && !granted))
                    continue;
                record.Apply(new SparkConsentDecision
                {
                    Purpose = declared ? purpose!.Name : name,
                    Granted = granted,
                    PolicyVersion = o.PolicyVersion,
                    AtUtc = now,
                    Source = source,
                    Language = language,
                    TextHash = declared ? TextHash(purpose!, language, o.PolicyVersion) : null,
                }, o.HistoryLimit);
            }

            if (isNew)
                await session.StoreAsync(record, id, cancellationToken);
            if (subject.Kind == SparkConsentSubjectKind.Visitor)
            {
                // A guest's record lives as long as the cookie that can ask for it.
                session.Advanced.GetMetadataFor(record)[Constants.Documents.Metadata.Expires] =
                    (now + visitorOptions.Value.Lifetime).UtcDateTime.ToString("O");
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return record;
            }
            catch (ConcurrencyException) when (attempt < WriteAttempts)
            {
                // Two tabs answering the banner at once: load the other's write and apply ours on top.
            }
        }
    }

    /// <summary>The proof of what was shown: SHA-256 over the policy version, the purpose and its texts in <paramref name="language"/>.</summary>
    internal static string TextHash(SparkConsentPurpose purpose, string? language, string policyVersion)
    {
        var lang = language ?? TranslatedString.DefaultLanguage;
        var text = new StringBuilder()
            .Append(policyVersion).Append('\n')
            .Append(purpose.Name).Append('\n')
            .Append(purpose.Title.GetValue(lang)).Append('\n')
            .Append(purpose.Description.GetValue(lang)).Append('\n');
        foreach (var vendor in purpose.Vendors ?? [])
            text.Append(vendor.Name).Append('|').Append(vendor.PrivacyPolicyUrl).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private IDataProtector Protector() => dataProtection.CreateProtector(UnsubscribeProtectorPurpose);

    private string Origin()
    {
        var origin = SparkPublicBaseUrl.Resolve(options.Value.PublicOrigin, services.GetService<IConfiguration>(),
            services.GetService<IHostEnvironment>(), accessor.HttpContext);
        return origin ?? throw new InvalidOperationException(
            "Unsubscribe links need a public origin: set Spark:Consent:PublicOrigin or Spark:Auth:PublicBaseUrl.");
    }

    /// <summary><c>jane@example.com</c> → <c>j***@example.com</c>.</summary>
    internal static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }
}
