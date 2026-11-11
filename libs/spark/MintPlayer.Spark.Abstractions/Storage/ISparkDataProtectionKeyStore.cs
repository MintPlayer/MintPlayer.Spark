using Microsoft.AspNetCore.DataProtection.Repositories;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The Data Protection key ring kept in the application's own database, for <c>Spark:DataProtection:Storage = RavenDb</c>
/// (#460 D5). A store provider that can keep it registers one; without it that setting refuses startup.
/// </summary>
public interface ISparkDataProtectionKeyStore : IXmlRepository;
