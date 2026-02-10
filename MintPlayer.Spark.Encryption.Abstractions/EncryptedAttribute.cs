namespace MintPlayer.Spark.Encryption.Abstractions;

/// <summary>
/// Marks a string property for field-level encryption at rest in RavenDB.
/// The property value is encrypted before storing and decrypted after loading.
/// Only valid on properties of type <see cref="string"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute
{
}
