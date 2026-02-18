namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Base user class for Spark authentication backed by RavenDB.
/// All data is embedded in a single document. Extend this class
/// to add custom properties to the user document.
/// <example>
/// <code>
/// public class AppUser : SparkUser
/// {
///     public string DisplayName { get; set; }
/// }
/// </code>
/// </example>
/// </summary>
public class SparkUser
{
    public string? Id { get; set; }
    public string? UserName { get; set; }
    public string? NormalizedUserName { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? PasswordHash { get; set; }
    public string? SecurityStamp { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
    public string? PhoneNumber { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public bool LockoutEnabled { get; set; }
    public int AccessFailedCount { get; set; }
    public string? AuthenticatorKey { get; set; }

    public List<string> Roles { get; set; } = [];
    public List<SparkUserClaim> Claims { get; set; } = [];
    public List<SparkUserLogin> Logins { get; set; } = [];
    public List<string> TwoFactorRecoveryCodes { get; set; } = [];
    public List<SparkUserToken> Tokens { get; set; } = [];
}
