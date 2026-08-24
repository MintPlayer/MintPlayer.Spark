using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Builds the object a refresh hook is handed: every attribute the model declares, carrying the
/// values the client currently holds — and nothing else the client sent.
/// </summary>
public interface IEffectiveObjectFactory
{
    /// <summary>
    /// Scaffolds from the model and copies only <see cref="PersistentObjectAttribute.Value"/> and
    /// <see cref="PersistentObjectAttribute.IsValueChanged"/> off <paramref name="submitted"/>,
    /// matching attributes by name.
    /// </summary>
    PersistentObject Build(EntityTypeDefinition entityType, PersistentObject? submitted);

    /// <summary>
    /// The attributes of <paramref name="entityType"/> that declare <c>triggersRefresh</c>, in model
    /// order. Empty for the overwhelming majority of types, which is what makes it cheap to ask.
    /// </summary>
    IReadOnlyList<string> TriggeringAttributeNames(EntityTypeDefinition entityType);
}

[Register(typeof(IEffectiveObjectFactory), ServiceLifetime.Scoped)]
internal partial class EffectiveObjectFactory : IEffectiveObjectFactory
{
    [Inject] private readonly IEntityMapper entityMapper;

    public PersistentObject Build(EntityTypeDefinition entityType, PersistentObject? submitted)
    {
        // Scaffolding from the model rather than trusting the submitted object is the whole security
        // story of this feature. Labels, rules, visibility, order, renderer — everything a client
        // might assert about how the form should look — comes from the server's model, so a hostile
        // client cannot claim an attribute is optional, visible, or writable when the model says
        // otherwise. What it legitimately owns is what the user typed, and that is all that is
        // copied across.
        var effective = entityMapper.GetPersistentObject(entityType.Id);
        effective.Id = submitted?.Id;

        if (submitted is null)
            return effective;

        var submittedByName = submitted.Attributes
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var attribute in effective.Attributes)
        {
            if (!submittedByName.TryGetValue(attribute.Name, out var incoming))
                continue;

            attribute.Value = incoming.Value;
            attribute.IsValueChanged = incoming.IsValueChanged;
        }

        return effective;
    }

    public IReadOnlyList<string> TriggeringAttributeNames(EntityTypeDefinition entityType) =>
        [.. entityType.Attributes
            .Where(a => a.TriggersRefresh == true)
            .OrderBy(a => a.Order)
            .Select(a => a.Name)];
}
