using System.Diagnostics.CodeAnalysis;

namespace MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

/// <summary>
/// A Peppol access point (D19, Q-O2, K7): the framework does not pick one, the host registers an adapter
/// (<c>UseStorecovePeppol()</c>, <c>UseBillitPeppol()</c>, <c>UseTestPeppol()</c>) and names it in
/// <c>Spark:Commerce:Invoicing:Peppol:Provider</c> when several are registered. Adapters send the UBL this library
/// writes and validates; they never build documents themselves.
/// </summary>
public interface IPeppolAccessPoint
{
    /// <summary>The adapter's permanent name (<c>storecove</c>, <c>billit</c>, <c>test</c>).</summary>
    string Name { get; }

    /// <summary>Whether <paramref name="receiver"/> is registered on Peppol for the document type.</summary>
    Task<PeppolLookupResult> LookupAsync(PeppolParticipant receiver, string documentTypeId, CancellationToken cancellationToken);

    /// <summary>Hands the UBL to the access point. Idempotency: the adapter passes <see cref="PeppolSendRequest.InvoiceId"/>
    /// where the API supports a client reference; the dispatcher never resends a transmission marked sent.</summary>
    Task<PeppolSendResult> SendAsync(PeppolSendRequest request, CancellationToken cancellationToken);
}

/// <summary>A Peppol participant id: an ISO 6523 ICD scheme and an identifier (<c>0208:0123456789</c>).</summary>
public readonly record struct PeppolParticipant(string Scheme, string Identifier)
{
    public override string ToString() => Scheme + ":" + Identifier;

    public static bool TryParse(string? value, [NotNullWhen(true)] out PeppolParticipant participant)
    {
        participant = default;
        if (value is null)
            return false;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("iso6523-actorid-upis::", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["iso6523-actorid-upis::".Length..];
        var colon = trimmed.IndexOf(':');
        if (colon != 4 || !trimmed[..4].All(char.IsAsciiDigit) || trimmed.Length <= 5)
            return false;
        participant = new PeppolParticipant(trimmed[..4], trimmed[5..]);
        return true;
    }

    /// <summary>
    /// The participant id a Belgian VAT id implies (ICD 0208, the enterprise number), the common case in Belgium;
    /// other countries' schemes are not guessed (the buyer's Peppol id is asked for, or looked up through the access point).
    /// </summary>
    public static PeppolParticipant? FromBelgianVatId(string? vatId)
        => vatId is { Length: 12 } v && v.StartsWith("BE", StringComparison.OrdinalIgnoreCase) && v[2..].All(char.IsAsciiDigit)
            ? new PeppolParticipant("0208", v[2..])
            : null;
}

/// <summary>A directory answer.</summary>
public sealed record PeppolLookupResult(bool CanReceive, string? Error = null);

/// <summary>One document to send.</summary>
/// <param name="InvoiceId">Our document id (the adapter's client reference).</param>
/// <param name="InvoiceNumber">The printed number.</param>
/// <param name="DocumentTypeId">The Peppol document type identifier (<see cref="UblWriter.InvoiceDocumentType"/> or the credit note's).</param>
/// <param name="Ubl">The UBL 2.1 bytes.</param>
/// <param name="Sender">The issuer's participant id.</param>
/// <param name="Receiver">The buyer's participant id.</param>
/// <param name="IssuerKey">The issuer (<c>platform</c> or a seller organization id): adapters with one account per legal entity pick it.</param>
public sealed record PeppolSendRequest(string InvoiceId, string InvoiceNumber, string DocumentTypeId, byte[] Ubl, PeppolParticipant Sender, PeppolParticipant Receiver, string IssuerKey);

/// <summary>The access point's answer.</summary>
/// <param name="Accepted">The access point took the document.</param>
/// <param name="ProviderReference">Its id for the submission.</param>
/// <param name="Error">Why not.</param>
/// <param name="Retryable">A later attempt may succeed (a 5xx, a timeout); false for a refusal of the document.</param>
public sealed record PeppolSendResult(bool Accepted, string? ProviderReference, string? Error = null, bool Retryable = false);

/// <summary>An access point's failure the dispatcher records (never the credentials).</summary>
public sealed class PeppolException(string provider, string message, int? statusCode = null) : Exception($"{provider}: {message}")
{
    public string Provider { get; } = provider;

    public int? StatusCode { get; } = statusCode;
}
