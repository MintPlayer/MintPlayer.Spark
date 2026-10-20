using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MintPlayer.Spark.Sms;

/// <summary>
/// The test adapter (<c>docs/commerce_PRD.md</c> X13): records every message instead of sending it, for
/// tests and local development. Nothing leaves the process.
/// </summary>
/// <remarks>
/// <para>
/// In Development the body is also logged, so a developer can read a verification code from the console.
/// In any other environment only the recipient and purpose are logged: a body may hold a one-time code.
/// </para>
/// <para>
/// Registered as a singleton by <see cref="SparkSmsServiceCollectionExtensions.AddSparkTestSms"/>; a test
/// resolves it to read <see cref="Sent"/>.
/// </para>
/// </remarks>
public sealed class SparkTestSmsSender(ILogger<SparkTestSmsSender>? logger = null, IHostEnvironment? environment = null) : ISparkSmsSender
{
    private readonly ILogger logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly List<SparkSmsMessage> sent = [];

    /// <summary>Every message sent so far, oldest first. A snapshot.</summary>
    public IReadOnlyList<SparkSmsMessage> Sent
    {
        get
        {
            lock (sent)
                return sent.ToArray();
        }
    }

    /// <summary>The last message to <paramref name="to"/>, or null.</summary>
    public SparkSmsMessage? LastTo(string to)
    {
        lock (sent)
            return sent.LastOrDefault(m => string.Equals(m.To, to, StringComparison.Ordinal));
    }

    /// <summary>Forgets every recorded message.</summary>
    public void Clear()
    {
        lock (sent)
            sent.Clear();
    }

    /// <inheritdoc />
    public Task<SparkSmsReceipt> SendAsync(SparkSmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (sent)
            sent.Add(message);

        if (environment?.IsDevelopment() == true)
            logger.LogInformation("Test SMS to {To} ({Purpose}), not delivered: {Body}", message.To, message.Purpose, message.Body);
        else
            logger.LogInformation("Test SMS to {To} ({Purpose}) recorded, not delivered.", message.To, message.Purpose);

        return Task.FromResult(new SparkSmsReceipt(true, "test-" + Guid.NewGuid().ToString("N")));
    }
}
