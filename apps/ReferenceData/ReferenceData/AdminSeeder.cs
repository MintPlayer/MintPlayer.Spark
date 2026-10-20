using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;

namespace ReferenceData;

/// <summary>
/// The first administrator (RD-DEF 2), in Development only: when <c>ReferenceData:Admin:Email</c> and
/// <c>ReferenceData:Admin:Password</c> are set (user secrets, never appsettings), that user is created with the password
/// and put in the Administrators group, which security.json binds to <c>reference-data:administrators</c> (the import
/// actions and the admin pages). An existing user is only added to the group; its password is never changed.
/// </summary>
internal sealed class AdminSeeder(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment, ILogger<AdminSeeder> logger) : IHostedLifecycleService
{
    public const string Group = "Administrators";

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;
        var email = configuration["ReferenceData:Admin:Email"];
        var password = configuration["ReferenceData:Admin:Password"];
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
                logger.LogWarning("The development administrator could not be created: {Errors}", string.Join("; ", created.Errors.Select(e => e.Code)));
                return;
            }
        }
        if (!await users.IsInRoleAsync(user, Group))
            await users.AddToRoleAsync(user, Group);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
