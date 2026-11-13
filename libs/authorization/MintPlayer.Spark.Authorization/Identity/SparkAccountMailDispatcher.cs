using System.Threading.Channels;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Hands account mail to <see cref="IEmailSender{TUser}"/> after the request that caused it has
/// answered (A11). <c>forgotPassword</c> and <c>resendConfirmationEmail</c> mail only an address that
/// belongs to someone; awaited in the request, the delivery (MailManager's suppression lookup, culture
/// resolution and queue write, or an app's own SMTP sender) made a known address measurably slower
/// than an unknown one — an account-enumeration oracle.
/// </summary>
/// <remarks>
/// <para>
/// In-process and bounded, not Spark Messaging: queuing a message on the bus is itself the database
/// write that took the time, and Authorization does not require Messaging (an app may register its own
/// <see cref="IEmailSender{TUser}"/>). With MailManager the mail becomes durable within milliseconds
/// anyway; a restart in that window loses the mail, and the user asks again. Nothing is lost silently:
/// a full queue, a failing sender and a sender that does not return within <see cref="SendTimeout"/>
/// are logged as errors.
/// </para>
/// <para>
/// Each delivery runs in its own DI scope, so a scoped sender (MailManager's <c>ISparkMailer</c> and its
/// session) never outlives or shares the request's.
/// </para>
/// </remarks>
internal sealed class SparkAccountMailDispatcher<TUser>(
    IServiceScopeFactory scopes,
    ILogger<SparkAccountMailDispatcher<TUser>> logger) : BackgroundService
    where TUser : SparkUser
{
    /// <summary>Deliveries waiting at most; beyond it a request's mail is refused (and logged), never awaited.</summary>
    internal const int Capacity = 1024;

    /// <summary>
    /// How long one delivery may take before the queue moves on. <see cref="IEmailSender{TUser}"/> takes
    /// no cancellation token, so a hung sender is abandoned, not cancelled.
    /// </summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(1);

    private readonly Channel<Delivery> queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait, // so TryWrite reports a full queue instead of dropping silently
    });

    /// <summary>
    /// Queues one delivery and returns at once. <see langword="false"/> (logged) when the queue is full;
    /// the caller's answer must not depend on it.
    /// </summary>
    public bool Enqueue(string kind, string? userId, Func<IEmailSender<TUser>, Task> send)
    {
        if (queue.Writer.TryWrite(new Delivery(kind, userId, send)))
            return true;

        logger.LogError("The account-mail queue is full ({Capacity}): the {Kind} mail for user {UserId} was not sent.", Capacity, kind, userId);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var delivery in queue.Reader.ReadAllAsync(stoppingToken))
            await DeliverAsync(delivery, stoppingToken);
    }

    private async Task DeliverAsync(Delivery delivery, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender<TUser>>();
            await delivery.Send(sender).WaitAsync(SendTimeout, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Shutting down: the {Kind} mail for user {UserId} was not sent.", delivery.Kind, delivery.UserId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send the {Kind} mail for user {UserId}.", delivery.Kind, delivery.UserId);
        }
    }

    private sealed record Delivery(string Kind, string? UserId, Func<IEmailSender<TUser>, Task> Send);
}
