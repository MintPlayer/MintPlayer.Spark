using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client;
using Raven.Client.Documents;

namespace CodeCoverage.CommitGraph;

/// <summary>Commit-author avatars for the graph (D17, D28), without an email ever reaching a browser.</summary>
public interface ICommitGraphAvatars
{
    /// <summary>
    /// The opaque avatar key per author email for one page, for the authors that have an avatar.
    /// An author missing from the answer gets initials.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveKeysAsync(
        Repository repository, IReadOnlyList<AvatarLookup> authors, CancellationToken cancellationToken = default);

    /// <summary>The image for a key from <see cref="ResolveKeysAsync"/>, fetched once and cached; null when there is none.</summary>
    Task<AvatarImage?> GetImageAsync(string avatarKey, CancellationToken cancellationToken = default);
}

/// <summary>Cached image bytes and their content type.</summary>
public sealed record AvatarImage(byte[] Bytes, string ContentType);

/// <summary>
/// D28's resolution order, in this class only: a GitHub no-reply id parsed locally, an old no-reply
/// login parsed locally, the cache (including misses), one batched forge call for what is left,
/// else initials.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key.</b> An author is cached at <c>AuthorAvatars/{forge}/{hmac}</c>, where the HMAC is
/// over the normalised email under <c>CommitGraph:AvatarKeySecret</c>. When that is unset the key is
/// derived from <c>Coverage:BadgeSigningKey</c> under the purpose <c>commit-graph-avatar-key</c>, so
/// production needs no new secret and the two uses can never collide; when neither is set, avatars
/// are off. The browser sees <c>{forge}-{hmac}</c>: unlike a Gravatar hash it cannot be reversed by
/// hashing a list of addresses, because the key never leaves the server.
/// </para>
/// <para>
/// <b>The images.</b> Only <c>https://avatars.githubusercontent.com</c> is ever fetched, at most
/// <c>CommitGraph:AvatarMaxBytes</c>, and only PNG, JPEG, GIF or WebP is kept (never SVG, which can
/// carry script). The bytes are an attachment on the cache document and expire with it.
/// </para>
/// </remarks>
[Register(typeof(ICommitGraphAvatars), ServiceLifetime.Scoped)]
public partial class CommitGraphAvatars : ICommitGraphAvatars
{
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IOptions<CommitGraphOptions> options;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly IEnumerable<IForgeAvatarResolver> resolvers;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<CommitGraphAvatars> logger;

    /// <summary>The named client images are fetched with.</summary>
    public const string HttpClientName = "commit-graph-avatars";

    public static readonly TimeSpan PositiveLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromDays(1);

    private const string AvatarHost = "avatars.githubusercontent.com";
    private const string DerivationPurpose = "commit-graph-avatar-key";

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    public async Task<IReadOnlyDictionary<string, string>> ResolveKeysAsync(
        Repository repository, IReadOnlyList<AvatarLookup> authors, CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Secret() is not { } secret) return keys;

        var forge = repository.Provider.ToCanonicalString();
        var byEmail = authors
            .Where(a => !string.IsNullOrWhiteSpace(a.Email) && a.Email.Contains('@'))
            .GroupBy(a => Normalise(a.Email), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Sha, StringComparer.Ordinal);
        if (byEmail.Count == 0) return keys;

        var hashes = byEmail.Keys.ToDictionary(email => email, email => Hash(secret, forge, email), StringComparer.Ordinal);
        var now = time.GetUtcNow().UtcDateTime;

        using var session = store.OpenAsyncSession();
        var cached = await session.LoadAsync<AuthorAvatar>(hashes.Values.Select(h => DocumentId(forge, h)), cancellationToken);

        var misses = new List<AvatarLookup>();
        foreach (var (email, sha) in byEmail)
        {
            var hash = hashes[email];
            var entry = cached.GetValueOrDefault(DocumentId(forge, hash));
            var lifetime = entry?.Url is null ? NegativeLifetime : PositiveLifetime;
            if (entry is not null && entry.ResolvedAtUtc + lifetime > now)
            {
                if (entry.Url is not null) keys[email] = PublicKey(forge, hash);
                continue;
            }

            // Steps 1 and 2: GitHub's no-reply addresses name the account, so no call is needed.
            if (NoReplyUrl(email) is { } url)
            {
                await StoreAsync(session, forge, hash, url, now, entry, cancellationToken);
                keys[email] = PublicKey(forge, hash);
                continue;
            }

            misses.Add(new AvatarLookup(email, sha));
        }

        // Step 4: one batched call for the rest, when this forge can answer at all.
        if (misses.Count > 0 && resolvers.FirstOrDefault(r => r.Provider == repository.Provider) is { } resolver)
        {
            var answers = await resolver.ResolveAsync(repository, misses, cancellationToken);
            if (answers is not null)
            {
                foreach (var miss in misses)
                {
                    var hash = hashes[miss.Email];
                    var url = answers.TryGetValue(miss.Email, out var found) && IsAllowed(found) ? found : null;
                    await StoreAsync(session, forge, hash, url, now, cached.GetValueOrDefault(DocumentId(forge, hash)), cancellationToken);
                    if (url is not null) keys[miss.Email] = PublicKey(forge, hash);
                }
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        // Keyed by the caller's spelling as well as the normalised one, so a lookup by the raw row
        // value always hits.
        foreach (var author in authors)
            if (!string.IsNullOrWhiteSpace(author.Email) && keys.TryGetValue(Normalise(author.Email), out var key))
                keys[author.Email] = key;
        return keys;
    }

    public async Task<AvatarImage?> GetImageAsync(string avatarKey, CancellationToken cancellationToken = default)
    {
        var match = PublicKeyShape().Match(avatarKey ?? string.Empty);
        if (!match.Success) return null;
        var id = DocumentId(match.Groups["forge"].Value, match.Groups["hash"].Value);

        using var session = store.OpenAsyncSession();
        var entry = await session.LoadAsync<AuthorAvatar>(id, cancellationToken);
        if (entry?.Url is null || !IsAllowed(entry.Url)) return null;

        using (var attachment = await session.Advanced.Attachments.GetAsync(entry, AuthorAvatar.ImageAttachment, cancellationToken))
        {
            if (attachment is not null)
            {
                using var buffer = new MemoryStream();
                await attachment.Stream.CopyToAsync(buffer, cancellationToken);
                return new AvatarImage(buffer.ToArray(), attachment.Details.ContentType);
            }
        }

        var image = await FetchAsync(entry.Url, cancellationToken);
        if (image is null) return null;

        session.Advanced.Attachments.Store(entry, AuthorAvatar.ImageAttachment, new MemoryStream(image.Bytes), image.ContentType);
        await session.SaveChangesAsync(cancellationToken);
        return image;
    }

    /// <summary>Fetches an allow-listed image, refusing anything too big or not a raster image.</summary>
    private async Task<AvatarImage?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var max = options.Value.AvatarMaxBytes;
        try
        {
            var http = httpClients.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // A redirect is not followed off the allow-listed host: the handler does not follow
            // redirects (see the registration), so anything but a 200 is a miss.
            if (!response.IsSuccessStatusCode) return null;

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !ImageTypes.Contains(contentType)) return null;
            if (response.Content.Headers.ContentLength > max) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > max) return null;
                buffer.Write(chunk, 0, read);
            }
            return new AvatarImage(buffer.ToArray(), contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not fetch an avatar image");
            return null;
        }
    }

    private async Task StoreAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, string forge, string hash, string? url, DateTime now,
        AuthorAvatar? existing, CancellationToken cancellationToken)
    {
        var entry = existing ?? new AuthorAvatar();
        if (existing is not null && existing.Url != url
            && session.Advanced.Attachments.GetNames(existing).Any(a => a.Name == AuthorAvatar.ImageAttachment))
        {
            // A changed answer must not keep serving the old picture.
            session.Advanced.Attachments.Delete(existing, AuthorAvatar.ImageAttachment);
        }
        entry.Url = url;
        entry.ResolvedAtUtc = now;
        if (existing is null) await session.StoreAsync(entry, DocumentId(forge, hash), cancellationToken);
        session.Advanced.GetMetadataFor(entry)[Constants.Documents.Metadata.Expires] =
            (now + (url is null ? NegativeLifetime : PositiveLifetime)).ToUniversalTime();
    }

    /// <summary>The image URL for a GitHub no-reply address, parsed locally (D28 steps 1 and 2).</summary>
    /// <remarks>
    /// The numeric id when the address carries one: a login can name a different account than
    /// the one that committed (a bot's <c>github.com/dependabot.png</c> is not the bot, S12).
    /// </remarks>
    public static string? NoReplyUrl(string email)
    {
        var withId = NoReplyWithId().Match(email);
        if (withId.Success) return $"https://{AvatarHost}/u/{withId.Groups["id"].Value}?v=4";

        var old = NoReplyLogin().Match(email);
        return old.Success ? $"https://{AvatarHost}/{old.Groups["login"].Value}" : null;
    }

    private static bool IsAllowed(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, AvatarHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort;

    /// <summary>The configured key, else one derived from the badge signing key, else null (avatars off).</summary>
    private byte[]? Secret()
    {
        if (options.Value.AvatarKeySecret is { Length: > 0 } own) return Encoding.UTF8.GetBytes(own);
        if (configuration[Badges.BadgePrSignature.KeyConfigurationPath] is { Length: > 0 } badge)
            return HMACSHA256.HashData(Encoding.UTF8.GetBytes(badge), Encoding.UTF8.GetBytes(DerivationPurpose));
        return null;
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static string Hash(byte[] secret, string forge, string email)
        => Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"avatar\n{forge}\n{email}")));

    private static string DocumentId(string forge, string hash) => $"{AuthorAvatar.Prefix}{forge}/{hash}";

    private static string PublicKey(string forge, string hash) => $"{forge}-{hash}";

    [GeneratedRegex("^(?<forge>[a-z]+)-(?<hash>[0-9a-f]{64})$")]
    private static partial Regex PublicKeyShape();

    [GeneratedRegex(@"^(?<id>[0-9]{1,20})\+(?<login>[A-Za-z0-9-]+(?:\[bot\])?)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex NoReplyWithId();

    [GeneratedRegex(@"^(?<login>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex NoReplyLogin();
}
