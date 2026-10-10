namespace MintPlayer.Spark.Http;

/// <summary>Why the SSRF-safe client refused a request or a response.</summary>
public enum SafeHttpBlockReason
{
    /// <summary>The host resolved to no address, or only to addresses that are not global unicast (loopback, private, link-local, …).</summary>
    Address,
    /// <summary>The host name did not resolve.</summary>
    Resolution,
    /// <summary>A scheme other than <c>http</c> or <c>https</c>, on the request or a redirect.</summary>
    Scheme,
    /// <summary>A port outside <see cref="SafeHttpOptions.AllowedPorts"/>.</summary>
    Port,
    /// <summary>More redirects than <see cref="SafeHttpOptions.MaxRedirects"/>.</summary>
    TooManyRedirects,
    /// <summary>A redirect left the requested origin while <see cref="SafeHttpOptions.RedirectsWithinOriginOnly"/> is set.</summary>
    CrossOriginRedirect,
    /// <summary>The response exceeded <see cref="SafeHttpOptions.MaxResponseBytes"/>.</summary>
    ResponseTooLarge,
    /// <summary>The decompressed response exceeded <see cref="SafeHttpOptions.MaxDecompressedBytes"/>.</summary>
    DecompressedTooLarge,
}

/// <summary>
/// The SSRF-safe client refused a request, a redirect or a response. An <see cref="HttpRequestException"/>,
/// so code that already handles network failures handles this too.
/// <para>
/// The message is fixed per <see cref="Reason"/> and never names a resolved address: it may reach the
/// user who supplied the URL, and an internal address is exactly what they must not learn.
/// </para>
/// </summary>
public sealed class SafeHttpBlockedException(SafeHttpBlockReason reason)
    : HttpRequestException(MessageFor(reason))
{
    public SafeHttpBlockReason Reason { get; } = reason;

    private static string MessageFor(SafeHttpBlockReason reason) => reason switch
    {
        SafeHttpBlockReason.Address => "The target address is not allowed.",
        SafeHttpBlockReason.Resolution => "The target host could not be resolved.",
        SafeHttpBlockReason.Scheme => "Only http and https are allowed.",
        SafeHttpBlockReason.Port => "The target port is not allowed.",
        SafeHttpBlockReason.TooManyRedirects => "Too many redirects.",
        SafeHttpBlockReason.CrossOriginRedirect => "A redirect to another origin is not allowed.",
        SafeHttpBlockReason.ResponseTooLarge => "The response is too large.",
        SafeHttpBlockReason.DecompressedTooLarge => "The decompressed response is too large.",
        _ => "The request is not allowed.",
    };
}
