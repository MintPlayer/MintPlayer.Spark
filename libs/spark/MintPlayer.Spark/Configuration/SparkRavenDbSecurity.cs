using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Raven.Client.Documents;
using Raven.Client.Http;

namespace MintPlayer.Spark.Configuration;

/// <summary>
/// Connects a <see cref="DocumentStore"/> to a secured RavenDB server from configuration: the client
/// certificate (<see cref="RavenDbOptions.CertificateFile"/>) and, for a server certificate issued by a
/// private authority, that authority (<see cref="RavenDbOptions.ServerCertificateAuthorityFile"/>).
/// <para>
/// <c>AddSpark</c> calls it for its own store, so an app needs only the settings
/// (<c>Spark__RavenDb__CertificateFile</c>, ...). Public so an app or library that builds a store of
/// its own can apply the same settings. With neither setting it does nothing: the unsecured
/// <c>http://</c> server of local development and the tests is untouched.
/// </para>
/// </summary>
public static class SparkRavenDbSecurity
{
    /// <summary>Applies <paramref name="options"/>' certificate settings to <paramref name="store"/>; call before <c>Initialize()</c>.</summary>
    /// <exception cref="InvalidOperationException">A certificate is configured for an <c>http://</c> URL, a key without a certificate, or a certificate without a private key.</exception>
    public static void Apply(DocumentStore store, RavenDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        var hasCertificate = !string.IsNullOrWhiteSpace(options.CertificateFile);
        if (!hasCertificate && !string.IsNullOrWhiteSpace(options.CertificateKeyFile))
            throw new InvalidOperationException("Spark:RavenDb:CertificateKeyFile is set without Spark:RavenDb:CertificateFile.");

        if (hasCertificate)
        {
            var plain = (store.Urls ?? []).Where(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (plain.Length > 0)
                throw new InvalidOperationException(
                    $"Spark:RavenDb:CertificateFile is set, but these RavenDB URLs are not https://: {string.Join(", ", plain)}. "
                    + "A client certificate is only sent over TLS; use the server's https:// URL.");
            store.Certificate = LoadClientCertificate(options);
        }

        if (!string.IsNullOrWhiteSpace(options.ServerCertificateAuthorityFile))
            TrustServerAuthority(options.ServerCertificateAuthorityFile);
    }

    internal static X509Certificate2 LoadClientCertificate(RavenDbOptions options)
    {
        var file = options.CertificateFile!;
        var password = string.IsNullOrEmpty(options.CertificatePassword) ? null : options.CertificatePassword;
        X509Certificate2 certificate;
        if (!string.IsNullOrWhiteSpace(options.CertificateKeyFile) || IsPem(file))
        {
            var keyFile = string.IsNullOrWhiteSpace(options.CertificateKeyFile) ? null : options.CertificateKeyFile;
            using var pem = password is null
                ? X509Certificate2.CreateFromPemFile(file, keyFile)
                : X509Certificate2.CreateFromEncryptedPemFile(file, password, keyFile);
            // A key read from PEM is ephemeral, and Windows' TLS stack (SChannel) refuses to sign the
            // client handshake with an ephemeral key. A PKCS#12 round trip gives it a usable one; on
            // Linux it changes nothing.
            certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        else
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(file, password);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"Spark:RavenDb:CertificateFile '{file}' has no private key. Give the key in Spark:RavenDb:CertificateKeyFile, or use a .pfx that contains it.");
        }

        return certificate;
    }

    private static bool IsPem(string file)
    {
        using var reader = new StreamReader(file);
        var buffer = new char[64];
        var read = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, read).TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal);
    }

    // RavenDB's client validates server certificates through ONE process-wide hook
    // (RequestExecutor.RemoteCertificateValidationCallback). Once any callback is registered the
    // client asks the callbacks instead of its default "no SSL policy errors", so each callback
    // added here accepts a clean certificate too and otherwise only a certificate of its own
    // authority. One registration per authority, however many stores use it.
    private static readonly ConcurrentDictionary<string, ServerAuthority> Authorities = new(StringComparer.Ordinal);

    internal static void TrustServerAuthority(string file)
    {
        var authority = X509CertificateLoader.LoadCertificateFromFile(file);
        var registered = Authorities.GetOrAdd(authority.Thumbprint, _ =>
        {
            var created = new ServerAuthority(authority);
            RequestExecutor.RemoteCertificateValidationCallback += created.Validate;
            return created;
        });
        if (!ReferenceEquals(registered.Certificate, authority))
            authority.Dispose();
    }

    internal sealed class ServerAuthority(X509Certificate2 certificate)
    {
        public X509Certificate2 Certificate { get; } = certificate;

        public bool Validate(object sender, X509Certificate? presented, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
                return true;
            // Only an unknown root is forgiven; a wrong host name or a missing certificate never is.
            if (presented is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                return false;

            using var leaf = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(Certificate);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                    custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }

            if (!custom.Build(leaf))
                return false;
            var root = custom.ChainElements[^1].Certificate;
            return root.RawDataMemory.Span.SequenceEqual(Certificate.RawDataMemory.Span);
        }
    }
}
