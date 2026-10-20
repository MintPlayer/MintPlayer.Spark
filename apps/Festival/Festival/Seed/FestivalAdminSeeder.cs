using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;

namespace Festival.Seed;

/// <summary>
/// The organiser account, in Development only: when <c>Festival:Admin:Email</c> and <c>Festival:Admin:Password</c> are
/// set (user secrets, never appsettings), that user is created, put in the Administrators group (platform admin:
/// sellers, notices, shipping staff) and made Owner of the demo organisation with every seller role
/// (<see cref="FestivalOrganizers"/>). An existing user's password is never changed.
/// </summary>
internal sealed class FestivalAdminSeeder(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment, ILogger<FestivalAdminSeeder> logger) : IHostedLifecycleService
{
    public const string Group = "Administrators";

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;
        var email = configuration["Festival:Admin:Email"];
        var password = configuration["Festival:Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return;

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new SparkUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                logger.LogWarning("The development organiser could not be created: {Errors}", string.Join("; ", created.Errors.Select(e => e.Code)));
                return;
            }
        }
        if (!await users.IsInRoleAsync(user, Group))
            await users.AddToRoleAsync(user, Group);
        await FestivalOrganizers.GrantAsync(scope.ServiceProvider.GetRequiredService<IDocumentStore>(), user.Id!, user.UserName ?? email, cancellationToken);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Makes a user an organiser of the demo organisation.</summary>
public static class FestivalOrganizers
{
    /// <summary>
    /// The organisation's custom roles an organiser holds: <c>seller-admin</c> and <c>finance</c> (Commerce),
    /// <c>box-office</c> (Checkout: comps, guest lists, back-office orders, GB7) and <c>scanner</c> (Tickets: door devices).
    /// </summary>
    public static readonly IReadOnlyList<string> Roles = ["seller-admin", "finance", "box-office", "scanner"];

    /// <summary>Stores (or completes) the user's Owner membership of <see cref="FestivalIds.Organization"/>. Idempotent.</summary>
    public static async Task GrantAsync(IDocumentStore store, string userId, string userName, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var id = OrganizationMembership.IdFor(userId, FestivalIds.Organization);
        var membership = await session.LoadAsync<OrganizationMembership>(id, cancellationToken);
        if (membership is null)
        {
            membership = new OrganizationMembership
            {
                Id = id,
                OrganizationId = FestivalIds.Organization,
                UserId = userId,
                UserName = userName,
                JoinedAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime,
            };
            await session.StoreAsync(membership, id, cancellationToken);
        }
        membership.Role = OrganizationRole.Owner;
        foreach (var role in Roles.Where(r => !membership.CustomRoles.Contains(r)))
            membership.CustomRoles.Add(role);
        await session.SaveChangesAsync(cancellationToken);
    }
}
