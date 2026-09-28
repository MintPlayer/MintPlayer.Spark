namespace MintPlayer.Spark.Configuration;

/// <summary>
/// Where Spark keeps the ASP.NET Core Data Protection key ring, bound from
/// <c>Spark:DataProtection</c> (#460, D5). Spark always calls <c>AddDataProtection()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The key ring decrypts every authentication cookie, antiforgery token and protected payload. A
/// key ring that does not survive a redeploy signs every user out on every redeploy — which is
/// what the ASP.NET Core default does inside a container, where the default folder is part of the
/// discarded filesystem. So outside Development one of <see cref="KeysPath"/> or
/// <see cref="Storage"/> is required, and a host with neither refuses to start.
/// </para>
/// <para>
/// ⚠️ <see cref="KeysPath"/> is only as durable as the folder it names. A path inside a container
/// that is not a mounted volume loses its keys on redeploy exactly as the default does; making it
/// durable is the operator's responsibility. <see cref="SparkDataProtectionStorage.RavenDb"/> keeps
/// the keys next to the rest of the application's state instead.
/// </para>
/// </remarks>
public sealed class SparkDataProtectionOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:DataProtection";

    /// <summary>
    /// The application discriminator: payloads protected under one name cannot be read under
    /// another. Defaults to the entry assembly's name. ⚠️ Changing it on a running deployment
    /// invalidates every cookie issued before the change.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>Persist the key ring as XML files in this folder.</summary>
    public string? KeysPath { get; set; }

    /// <summary>Persist the key ring in a store Spark manages. See <see cref="SparkDataProtectionStorage"/>.</summary>
    public SparkDataProtectionStorage Storage { get; set; }
}

/// <summary>Where <see cref="SparkDataProtectionOptions"/> persists the key ring.</summary>
public enum SparkDataProtectionStorage
{
    /// <summary>
    /// Not chosen: <see cref="SparkDataProtectionOptions.KeysPath"/> decides, and without it a
    /// Development host uses a local folder while any other host refuses to start.
    /// </summary>
    None,

    /// <summary>
    /// In RavenDB, as documents under <c>DataProtectionKeys/</c> in the application's database.
    /// </summary>
    RavenDb,
}
