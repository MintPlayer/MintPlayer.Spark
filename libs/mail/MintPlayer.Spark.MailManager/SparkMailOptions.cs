namespace MintPlayer.Spark.MailManager;

/// <summary>
/// <c>Spark:Mail</c>. Everything MailManager does is chosen here, so an operator can change it per
/// environment (appsettings, environment variables <c>Spark__Mail__…</c>, user secrets) without a
/// rebuild (#460, D9). The code delegate of <c>AddMailManager</c> runs after binding.
/// </summary>
public sealed class SparkMailOptions
{
    /// <summary>The culture of a mail whose request and recipient name none. Default <c>en</c>.</summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>
    /// The application name every template sees as <c>app_name</c> (unless its data sets one). Null
    /// renders templates without it.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>The <c>From:</c> of every mail. <see cref="SparkMailAddressOptions.Address"/> is required.</summary>
    public SparkMailAddressOptions From { get; set; } = new();

    /// <summary>SMTP through MailKit: <c>UseSmtpTransport()</c>, or — with no transport registered — whenever <see cref="SparkMailSmtpOptions.Host"/> is set.</summary>
    public SparkMailSmtpOptions Smtp { get; set; } = new();

    /// <summary><c>Spark:Mail:Mailpit</c>: the local mail catcher <c>UseMailpitTransport()</c> sends to.</summary>
    public SparkMailMailpitOptions Mailpit { get; set; } = new();

    /// <summary>
    /// A folder every mail is written to as an <c>.eml</c> file instead of being sent — for development
    /// and demo apps. Relative paths are under the content root. Mutually exclusive with
    /// <see cref="SparkMailSmtpOptions.Host"/>.
    /// </summary>
    public string? PickupFolder { get; set; }

    /// <summary>Where the application's own template files are.</summary>
    public SparkMailTemplateOptions Templates { get; set; } = new();

    /// <summary>Development helpers.</summary>
    public SparkMailDevelopmentOptions Development { get; set; } = new();

    /// <summary>VERP and the bounce endpoint (opt-in).</summary>
    public SparkMailBounceOptions Bounces { get; set; } = new();

    /// <summary>
    /// The public origin (<c>https://app.example</c>) one-click unsubscribe links are built on. Falls
    /// back to <c>Spark:Auth:PublicBaseUrl</c>. Links are never built from the request <c>Host</c>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>How long a <c>SparkMailDeliveries/{id}</c> record is kept (RavenDB <c>@expires</c>). Default 30 days.</summary>
    public int DeliveryRetentionDays { get; set; } = 30;
}

/// <summary>An address with an optional display name.</summary>
public sealed class SparkMailAddressOptions
{
    /// <summary>The address.</summary>
    public string? Address { get; set; }

    /// <summary>The display name.</summary>
    public string? Name { get; set; }
}

/// <summary>How <see cref="SparkMailSmtpOptions"/> secures the connection.</summary>
public enum SparkMailSmtpSecurity
{
    /// <summary>MailKit's choice: TLS on connect for port 465, otherwise STARTTLS when the server offers it.</summary>
    Auto,
    /// <summary>
    /// No TLS, even when the server advertises STARTTLS (measured, spike S-M6). For a relay on a
    /// private network — such as a Postfix container reachable only by the app.
    /// </summary>
    None,
    /// <summary>STARTTLS, required.</summary>
    StartTls,
    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,
}

/// <summary><c>Spark:Mail:Smtp</c>.</summary>
public sealed class SparkMailSmtpOptions
{
    /// <summary>The relay's host name. Without a registered transport, setting it selects SMTP.</summary>
    public string? Host { get; set; }

    /// <summary>The port. Default 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>The connection security. Default <see cref="SparkMailSmtpSecurity.Auto"/>; no STARTTLS is forced.</summary>
    public SparkMailSmtpSecurity Security { get; set; } = SparkMailSmtpSecurity.Auto;

    /// <summary>The user name, when the relay needs authentication.</summary>
    public string? UserName { get; set; }

    /// <summary>The password. Keep it out of appsettings: environment variable or user secrets.</summary>
    public string? Password { get; set; }

    /// <summary>The socket timeout for one send. Default 100 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
}

/// <summary>
/// <c>Spark:Mail:Mailpit</c>, for <c>UseMailpitTransport()</c>: plain SMTP into a local
/// <see href="https://mailpit.axllent.org/">Mailpit</see> (no TLS, no authentication), so the exact MIME
/// is what Mailpit shows.
/// </summary>
public sealed class SparkMailMailpitOptions
{
    /// <summary>Mailpit's SMTP host. Default <c>localhost</c>.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Mailpit's SMTP port. Default 1025.</summary>
    public int Port { get; set; } = 1025;

    /// <summary>Extra Mailpit tags on every mail, besides the template and the lane (<c>transactional</c>/<c>bulk</c>).</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>The socket timeout for one send. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary><c>Spark:Mail:Templates</c>.</summary>
public sealed class SparkMailTemplateOptions
{
    /// <summary>
    /// The folder with the application's template files, relative to the content root. Default
    /// <c>Templates/Mail</c>. A file here wins over an embedded default with the same name and culture.
    /// </summary>
    public string Path { get; set; } = "Templates/Mail";
}

/// <summary><c>Spark:Mail:Development</c>.</summary>
public sealed class SparkMailDevelopmentOptions
{
    /// <summary>
    /// Every mail goes to this address instead of its recipient (the original is in
    /// <c>X-Spark-Original-To</c>). Logged as a warning outside Development; startup refuses it in
    /// Production (<c>IHostEnvironment.IsProduction()</c>), where it would silently divert every user's mail.
    /// </summary>
    public string? RedirectTo { get; set; }
}

/// <summary><c>Spark:Mail:Bounces</c>.</summary>
public sealed class SparkMailBounceOptions
{
    /// <summary>
    /// Enables VERP: the envelope sender of each mail becomes <c>bounces+{deliveryId}@{VerpDomain}</c>,
    /// so a bounce names the delivery it is about. The relay must accept this domain as a sender
    /// (Postfix <c>ALLOWED_SENDER_DOMAINS</c>) and its SPF must cover it.
    /// </summary>
    public string? VerpDomain { get; set; }

    /// <summary><c>POST /spark/mail/bounces</c>.</summary>
    public SparkMailBounceEndpointOptions Endpoint { get; set; } = new();
}

/// <summary><c>Spark:Mail:Bounces:Endpoint</c>.</summary>
public sealed class SparkMailBounceEndpointOptions
{
    /// <summary>Maps <c>POST /spark/mail/bounces</c>. Requires <see cref="Secret"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The shared bearer secret the relay's pipe sends (<c>Authorization: Bearer …</c>); at least 32
    /// characters. Compared in constant time, before any parser sees the body.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>The largest DSN accepted. Default 1 MiB.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;
}
