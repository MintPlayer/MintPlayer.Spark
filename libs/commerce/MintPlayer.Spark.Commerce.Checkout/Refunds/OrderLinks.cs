using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// The signed order link (D4, X1): a guest reaches their order page, and the withdrawal function on it (GA1), from the
/// confirmation mail without an account or the original browser. The token is the order id protected by ASP.NET Data
/// Protection; it never expires (the withdrawal function must stay reachable until the period ends, and reading an
/// order discloses nothing the mail did not). Without Data Protection, no link is made and guests use their visitor.
/// </summary>
internal sealed class OrderLinks(IServiceProvider services)
{
    private const string Purpose = "MintPlayer.Spark.Commerce.Checkout.OrderLink.v1";

    private IDataProtector? Protector => services.GetService<IDataProtectionProvider>()?.CreateProtector(Purpose);

    public string? Create(string orderId) => Protector?.Protect(orderId);

    /// <summary>The order id a token names, or null when it is not one of ours.</summary>
    public string? Read(string? token)
    {
        if (token is not { Length: > 0 } || Protector is not { } protector)
            return null;
        try
        {
            return protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
