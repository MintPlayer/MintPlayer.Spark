using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Abstractions;

public interface IManager
{
    /// <summary>
    /// Creates a virtual PersistentObject (not backed by a DB entity).
    /// Useful for building custom dialogs in Retry.Action().
    /// </summary>
    PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes);

    /// <summary>
    /// Access to the Retry Action subsystem.
    /// </summary>
    IRetryAccessor Retry { get; }

    /// <summary>
    /// Gets a translated message for the current request culture, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetTranslatedMessage(string key, params object[] parameters);

    /// <summary>
    /// Gets a translated message for a specific language, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetMessage(string key, string language, params object[] parameters);
}
