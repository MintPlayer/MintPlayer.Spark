using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Endpoints;

[assembly: EndpointsMethodName("MapSparkPrintingQzTrayEndpoints")]

namespace MintPlayer.Spark.Printing.QzTray;

/// <summary>
/// <c>Spark:Printing:QzTray</c>: the host's QZ Tray signing certificate and private key (PRD §4.11: silent printing needs
/// signed messages; the key stays in user-secrets or configuration, never in the repository).
/// </summary>
public sealed class SparkQzTrayOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Printing:QzTray";

    /// <summary>The public certificate (PEM) QZ Tray is given; or <see cref="CertificatePath"/>.</summary>
    public string? Certificate { get; set; }

    public string? CertificatePath { get; set; }

    /// <summary>The RSA private key (PEM, PKCS#1 or PKCS#8); or <see cref="PrivateKeyPath"/>. A secret: user-secrets or a vault.</summary>
    public string? PrivateKey { get; set; }

    public string? PrivateKeyPath { get; set; }

    /// <summary><c>SHA512</c> (QZ Tray 2.1+, the default) or <c>SHA1</c> (older QZ Tray).</summary>
    public string Algorithm { get; set; } = "SHA512";

    internal string? ReadCertificate() => Certificate ?? (CertificatePath is { Length: > 0 } p && File.Exists(p) ? File.ReadAllText(p) : null);

    internal string? ReadPrivateKey() => PrivateKey ?? (PrivateKeyPath is { Length: > 0 } p && File.Exists(p) ? File.ReadAllText(p) : null);
}

internal sealed class SparkQzTrayOptionsValidator : IValidateOptions<SparkQzTrayOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkQzTrayOptions options)
    {
        var problems = new List<string>();
        if (options.ReadCertificate() is not { Length: > 0 })
            problems.Add("Spark:Printing:QzTray:Certificate (or CertificatePath) is missing: QZ Tray needs the host's public certificate.");
        var key = options.ReadPrivateKey();
        if (key is not { Length: > 0 })
            problems.Add("Spark:Printing:QzTray:PrivateKey (or PrivateKeyPath) is missing: set it in user-secrets or a vault, never in the repository.");
        else
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(key);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                // Never echo the value: only that it does not parse.
                problems.Add("Spark:Printing:QzTray:PrivateKey is not an RSA private key in PEM.");
            }
        }
        if (options.Algorithm is not ("SHA512" or "SHA1"))
            problems.Add($"Spark:Printing:QzTray:Algorithm must be SHA512 or SHA1 (is {options.Algorithm}).");
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}

/// <summary>Signs QZ Tray's messages with the host's key (RSA, PKCS#1 v1.5, SHA-512 or SHA-1), base64.</summary>
public interface IQzTraySigner
{
    string Certificate { get; }

    string Sign(string message);
}

internal sealed class QzTraySigner(IOptions<SparkQzTrayOptions> options) : IQzTraySigner
{
    public string Certificate => options.Value.ReadCertificate() ?? "";

    public string Sign(string message)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.Value.ReadPrivateKey());
        var algorithm = options.Value.Algorithm == "SHA1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA512;
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(message), algorithm, RSASignaturePadding.Pkcs1));
    }
}

public static class SparkPrintingQzTrayExtensions
{
    /// <summary>
    /// Registers the <c>QzTray</c> printer adapter (delivered by the station's browser through QZ Tray) and
    /// <c>/spark/printing/qz-tray/{certificate,sign}</c>; refuses startup without a certificate and a parsable key.
    /// </summary>
    public static ISparkBuilder AddPrintingQzTray(this ISparkBuilder builder, Action<SparkQzTrayOptions>? configure = null)
    {
        builder.AddPrinting();
        var services = builder.Services;
        if (!services.TryAddSparkMarker<QzTrayMarker>())
            return builder;
        services.AddSparkConfigurationSection<SparkQzTrayOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSingleton<IValidateOptions<SparkQzTrayOptions>, SparkQzTrayOptionsValidator>();
        services.AddOptions<SparkQzTrayOptions>().ValidateOnStart();
        services.TryAddSingleton<IQzTraySigner, QzTraySigner>();
        builder.AddPrinterAdapter(PrinterAdapters.QzTray, deliveredByStation: true);
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkPrintingQzTrayEndpoints());
        return builder;
    }

    private sealed class QzTrayMarker;
}

internal class QzTrayGroup : IEndpointGroup
{
    public static string Prefix => "/spark/printing/qz-tray";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.RequireAuthorization();
}

public sealed record QzTraySignRequest(string? Message);

[SparkContract]
public sealed record QzTraySignature(string Signature, string Algorithm);

[SparkContract]
public sealed record QzTrayCertificate(string Certificate);

/// <summary><c>GET /spark/printing/qz-tray/certificate</c>: the public certificate (station operators only).</summary>
[MemberOf<QzTrayGroup>]
internal sealed partial class GetQzTrayCertificate : IGetEndpoint
{
    public static string Path => "/certificate";

    [Inject] private readonly IQzTraySigner signer;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await permissions.IsAllowedAsync("Read", "PrintJob", httpContext.RequestAborted)
            ? Results.Ok(new QzTrayCertificate(signer.Certificate))
            : addOn.Refusal(httpContext);
}

/// <summary><c>POST /spark/printing/qz-tray/sign</c>: QZ Tray's message signed with the host's key (station operators only).</summary>
[MemberOf<QzTrayGroup>]
internal sealed partial class SignQzTrayMessage : IPostEndpoint<QzTraySignRequest>
{
    public static string Path => "/sign";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IQzTraySigner signer;
    [Inject] private readonly IOptions<SparkQzTrayOptions> options;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(QzTraySignRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        // QZ Tray signs short JSON call descriptions; a bound keeps the endpoint from signing arbitrary documents.
        if (request?.Message is not { Length: > 0 and <= 65_536 } message || !await permissions.IsAllowedAsync("Read", "PrintJob", cancellationToken))
            return addOn.Refusal(http);
        return Results.Ok(new QzTraySignature(signer.Sign(message), options.Value.Algorithm));
    }
}
