using DemoApp.Library.Entities;
using MintPlayer.Spark.Actions;

namespace DemoApp.Actions;

/// <summary>
/// Custom Actions class for Person entity demonstrating lifecycle hooks.
/// </summary>
public class PersonActions : DefaultPersistentObjectActions<Person>
{
    /// <summary>
    /// Called before saving a Person entity.
    /// Normalizes email to lowercase and trims whitespace from names.
    /// </summary>
    public override Task OnBeforeSaveAsync(Person entity)
    {
        // Normalize email to lowercase
        if (!string.IsNullOrEmpty(entity.Email))
        {
            entity.Email = entity.Email.Trim().ToLowerInvariant();
        }

        // Trim whitespace from names
        entity.FirstName = entity.FirstName?.Trim() ?? string.Empty;
        entity.LastName = entity.LastName?.Trim() ?? string.Empty;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after saving a Person entity.
    /// Logs the save operation for demonstration purposes.
    /// </summary>
    public override Task OnAfterSaveAsync(Person entity)
    {
        Console.WriteLine($"[PersonActions] Person saved: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before deleting a Person entity.
    /// Logs the delete operation for demonstration purposes.
    /// </summary>
    public override Task OnBeforeDeleteAsync(Person entity)
    {
        Console.WriteLine($"[PersonActions] Person being deleted: {entity.FirstName} {entity.LastName} (ID: {entity.Id})");
        return Task.CompletedTask;
    }
}
