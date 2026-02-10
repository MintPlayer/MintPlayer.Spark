# Field-Level Encryption PRD for MintPlayer.Spark

## Overview

Implement transparent field-level encryption for the Spark framework, allowing developers to mark sensitive entity properties with an `[Encrypted]` attribute. Encrypted fields are stored as ciphertext in RavenDB documents and transparently decrypted when loaded by the application. Encryption keys reside exclusively in the application configuration (never in the database), and encrypted fields are ETL-compatible — replicated as opaque blobs without re-encryption.

## Motivation

In a multi-module architecture where data is replicated between databases via RavenDB ETL, some fields contain sensitive data (e.g., VIN numbers, social security numbers, salary information). These fields must be encrypted at rest in the database while remaining transparent to application code. Since ETL scripts execute server-side inside RavenDB (with no access to application secrets), the encryption design must ensure encrypted fields can be copied as-is during replication.

### Design Constraints

1. **ETL scripts are crypto-blind** — they run inside RavenDB and cannot access encryption keys
2. **No double encryption** — each field read/write involves exactly one encryption or decryption operation
3. **No single shared key** — per-module key isolation for ISO compliance
4. **Keys stay in the application** — stored in `appsettings.json` or environment variables, never in the database

### Encryption Strategy: Per-Module Symmetric Keys

Each module has its own AES-256-GCM symmetric key. Modules that consume replicated data from other modules are configured with the source module's key.

```
Fleet appsettings:
  Encryption:
    OwnKey: <fleet-aes-key>          # Encrypts/decrypts Fleet's own data
    ModuleKeys:
      HR: <hr-aes-key>              # Decrypts replicated HR data

HR appsettings:
  Encryption:
    OwnKey: <hr-aes-key>            # Encrypts/decrypts HR's own data
    ModuleKeys:
      Fleet: <fleet-aes-key>        # Decrypts replicated Fleet data
```

**Why not asymmetric encryption?** Asymmetric crypto solves key distribution with untrusted parties. In this architecture, the receiving app needs the decryption key regardless — so it's the same trust level as sharing a symmetric key, but ~1000x slower. Between modules operated by the same team, symmetric keys are simpler, faster, and equally secure.

---

## Goals

1. **Transparent Encryption**: Developers mark properties with `[Encrypted]` — encryption/decryption happens automatically
2. **ETL-Compatible**: Encrypted fields are replicated as opaque blobs — ETL scripts require no changes
3. **Per-Module Key Isolation**: Each module's key protects only its own data
4. **Zero Performance Overhead on Non-Encrypted Fields**: Only fields marked `[Encrypted]` incur crypto cost
5. **Backward Compatible**: Existing documents without encryption continue to work

---

## Architecture

### High-Level Flow

```
┌──────────────────────────────────────────────────────────────────┐
│  Fleet Application                                               │
│                                                                  │
│  Car { LicensePlate: "ABC-123", VinNumber: "WBA..." }           │
│                        │                                         │
│                   OnBeforeStore                                   │
│                        │                                         │
│                   Detect [Encrypted]                              │
│                   on VinNumber                                    │
│                        │                                         │
│                   AES-256-GCM Encrypt                            │
│                   with Fleet's OwnKey                             │
│                        │                                         │
│                        ▼                                         │
│  RavenDB Document:                                               │
│  { LicensePlate: "ABC-123", VinNumber: "ENC:iv:ciphertext:tag" }│
│                                                                  │
│                   ┌─────────────────┐                            │
│                   │  RavenDB ETL    │                            │
│                   │  (crypto-blind) │                            │
│                   │                 │                            │
│                   │  Copies blob    │                            │
│                   │  as-is to HR DB │                            │
│                   └────────┬────────┘                            │
│                            │                                     │
└────────────────────────────┼─────────────────────────────────────┘
                             │
┌────────────────────────────┼─────────────────────────────────────┐
│  HR Application            ▼                                     │
│                                                                  │
│  RavenDB Document:                                               │
│  { LicensePlate: "ABC-123", VinNumber: "ENC:iv:ciphertext:tag" }│
│                        │                                         │
│                   OnAfterLoad                                    │
│                        │                                         │
│                   Detect [Encrypted]                              │
│                   on VinNumber                                    │
│                        │                                         │
│                   Determine key:                                  │
│                   Replicated from Fleet?                          │
│                   → Use ModuleKeys["Fleet"]                      │
│                        │                                         │
│                   AES-256-GCM Decrypt                            │
│                        │                                         │
│                        ▼                                         │
│  Car { LicensePlate: "ABC-123", VinNumber: "WBA..." }           │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

### Key Selection Logic

When **encrypting** (before store):
- Always use `OwnKey` — the module only encrypts its own data

When **decrypting** (after load):
- Check if the entity type has a `[Replicated(SourceModule = "X")]` attribute
  - Yes → Use `ModuleKeys["X"]` (the source module's key)
  - No → Use `OwnKey` (this module's own data)

---

## New Library: MintPlayer.Spark.Encryption

A separate library project to keep encryption concerns isolated.

### Project: MintPlayer.Spark.Encryption.Abstractions

Shared types that entity libraries reference:

```
MintPlayer.Spark.Encryption.Abstractions/
├── EncryptedAttribute.cs
└── Configuration/
    └── SparkEncryptionOptions.cs
```

### Project: MintPlayer.Spark.Encryption

Implementation:

```
MintPlayer.Spark.Encryption/
├── Services/
│   ├── FieldEncryptionService.cs
│   └── EncryptedFieldResolver.cs
├── Conventions/
│   └── EncryptionSessionListeners.cs
└── Extensions/
    └── SparkEncryptionExtensions.cs
```

---

## Detailed Design

### 1. The [Encrypted] Attribute

```csharp
namespace MintPlayer.Spark.Encryption.Abstractions;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class EncryptedAttribute : Attribute
{
}
```

#### Example Usage (in Fleet entity)

```csharp
namespace Fleet.Library.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }

    [Encrypted]
    public string VinNumber { get; set; } = string.Empty;
}
```

#### Supported Property Types

The `[Encrypted]` attribute is only supported on `string` properties. Most sensitive data is string-typed (VIN, SSN, email, etc.), and restricting to strings avoids JSON deserialization mismatches where the CLR type is `int` but the stored value is an encrypted string.

| CLR Type | Support |
|----------|---------|
| `string` | Supported — stored as `"ENC:v1:{base64(iv)}:{base64(ciphertext)}:{base64(tag)}"` |
| All other types | Not supported — will throw at startup validation |

> **Important**: **Encrypted fields cannot be meaningfully queried or sorted on in RavenDB indexes** — ciphertext is not meaningful for comparison. This is an intentional tradeoff: if a field needs to be queryable, it should not be encrypted.

### 2. Encryption Format

Encrypted field values follow a deterministic format stored as a string in the RavenDB document:

```
ENC:v1:{base64(iv)}:{base64(ciphertext)}:{base64(tag)}
```

| Component | Description |
|-----------|-------------|
| `ENC` | Magic prefix to identify encrypted values |
| `v1` | Format version (for future algorithm changes) |
| `iv` | 12-byte initialization vector (randomly generated per encryption) |
| `ciphertext` | AES-256-GCM encrypted data |
| `tag` | 16-byte GCM authentication tag |

The `ENC:` prefix allows the decryption logic to distinguish encrypted values from plaintext, which is essential for **backward compatibility** — existing documents with plaintext values will continue to work. On load, if a field marked `[Encrypted]` does not start with `ENC:`, it is treated as plaintext (and optionally re-encrypted on next save).

### 3. Configuration

#### SparkEncryptionOptions

```csharp
namespace MintPlayer.Spark.Encryption.Abstractions.Configuration;

public class SparkEncryptionOptions
{
    /// <summary>
    /// This module's encryption key (base64-encoded, 256-bit AES key).
    /// Used to encrypt this module's own data and decrypt it on read.
    /// </summary>
    public required string OwnKey { get; set; }

    /// <summary>
    /// Encryption keys of other modules, keyed by module name.
    /// Used to decrypt replicated data from those modules.
    /// Example: { "Fleet": "base64(fleet-aes-key)" }
    /// </summary>
    public Dictionary<string, string> ModuleKeys { get; set; } = new();
}
```

#### appsettings.json (Fleet)

```json
{
    "Spark": {
        "RavenDb": {
            "Urls": ["http://localhost:8080"],
            "Database": "SparkFleet"
        }
    },
    "SparkReplication": {
        "ModuleName": "Fleet",
        "ModuleUrl": "https://localhost:5001",
        "SparkModulesUrls": ["http://localhost:8080"],
        "SparkModulesDatabase": "SparkModules"
    },
    "SparkEncryption": {
        "OwnKey": "base64-encoded-256-bit-key-here",
        "ModuleKeys": {
            "HR": "base64-encoded-256-bit-key-here"
        }
    }
}
```

#### appsettings.json (HR)

```json
{
    "Spark": {
        "RavenDb": {
            "Urls": ["http://localhost:8080"],
            "Database": "SparkHR"
        }
    },
    "SparkReplication": {
        "ModuleName": "HR",
        "ModuleUrl": "https://localhost:5002",
        "SparkModulesUrls": ["http://localhost:8080"],
        "SparkModulesDatabase": "SparkModules"
    },
    "SparkEncryption": {
        "OwnKey": "base64-encoded-256-bit-key-here",
        "ModuleKeys": {
            "Fleet": "base64-encoded-256-bit-key-here"
        }
    }
}
```

### 4. FieldEncryptionService

Core service handling AES-256-GCM encrypt/decrypt operations.

```csharp
namespace MintPlayer.Spark.Encryption.Services;

public class FieldEncryptionService
{
    /// <summary>
    /// Encrypts a plaintext string value using AES-256-GCM.
    /// Returns the encrypted value in "ENC:v1:{iv}:{ciphertext}:{tag}" format.
    /// </summary>
    public string Encrypt(string plaintext, byte[] key);

    /// <summary>
    /// Decrypts an "ENC:v1:{iv}:{ciphertext}:{tag}" formatted string.
    /// Returns the original plaintext.
    /// Throws if the tag verification fails (tampered data).
    /// </summary>
    public string Decrypt(string encryptedValue, byte[] key);

    /// <summary>
    /// Checks if a string value is in encrypted format (starts with "ENC:").
    /// </summary>
    public bool IsEncrypted(string value);
}
```

**Implementation Notes:**
- Uses `System.Security.Cryptography.AesGcm` (.NET built-in, no external dependencies)
- Generates a random 12-byte IV per encryption call (never reuses IVs)
- 128-bit authentication tag for tamper detection
- Thread-safe (no shared mutable state)

### 5. EncryptedFieldResolver

Service that resolves which properties on an entity type are marked `[Encrypted]` and caches the result.

```csharp
namespace MintPlayer.Spark.Encryption.Services;

public class EncryptedFieldResolver
{
    /// <summary>
    /// Returns the PropertyInfo[] for all properties on the given type
    /// that are marked with [Encrypted]. Results are cached per type.
    /// </summary>
    public PropertyInfo[] GetEncryptedProperties(Type entityType);

    /// <summary>
    /// Determines which encryption key to use for the given entity type.
    /// If the type has [Replicated(SourceModule = "X")], returns ModuleKeys["X"].
    /// Otherwise returns OwnKey.
    /// </summary>
    public byte[] GetDecryptionKey(Type entityType);

    /// <summary>
    /// Returns OwnKey (always used for encryption).
    /// </summary>
    public byte[] GetEncryptionKey();
}
```

**Caching:** Uses `ConcurrentDictionary<Type, PropertyInfo[]>` to avoid repeated reflection.

### 6. RavenDB Session Integration

The encryption/decryption hooks into RavenDB's document store conventions using `OnBeforeStore` and `OnAfterSaveChanges` for encryption, and a custom session wrapper or `OnBeforeQuery`/`OnAfterConversionToEntity` for decryption.

#### Encryption Hook (Before Store)

Register on the `IDocumentStore` using RavenDB's `OnBeforeStore` session event:

```csharp
store.OnBeforeStore += (sender, args) =>
{
    var entityType = args.Entity.GetType();
    var encryptedProps = resolver.GetEncryptedProperties(entityType);
    if (encryptedProps.Length == 0) return;

    var key = resolver.GetEncryptionKey();
    foreach (var prop in encryptedProps)
    {
        var value = prop.GetValue(args.Entity);
        if (value == null) continue;

        var plaintext = ConvertToString(value, prop.PropertyType);
        var encrypted = encryptionService.Encrypt(plaintext, key);

        // Store encrypted value in the document metadata or directly
        // Use args.DocumentMetadata or modify the entity
    }
};
```

#### Decryption Hook (After Load)

Register on the `IDocumentStore` using the session's `OnAfterConversionToEntity` event:

```csharp
store.OnAfterConversionToEntity += (sender, args) =>
{
    var entityType = args.Entity.GetType();
    var encryptedProps = resolver.GetEncryptedProperties(entityType);
    if (encryptedProps.Length == 0) return;

    var key = resolver.GetDecryptionKey(entityType);
    foreach (var prop in encryptedProps)
    {
        var value = prop.GetValue(args.Entity);
        if (value is not string strValue) continue;
        if (!encryptionService.IsEncrypted(strValue)) continue;

        var plaintext = encryptionService.Decrypt(strValue, key);
        var typed = ConvertFromString(plaintext, prop.PropertyType);
        prop.SetValue(args.Entity, typed);
    }
};
```

Since `[Encrypted]` is restricted to `string` properties, the RavenDB document stores a string and the CLR type is a string — no type conversion is needed. The `OnBeforeStore` hook replaces the plaintext string with the encrypted string, and the `OnAfterConversionToEntity` hook replaces the encrypted string with the decrypted plaintext.

### 7. ETL Script Compatibility

**ETL scripts require no changes.** Encrypted fields appear as normal string values in the RavenDB document. The ETL script copies them as-is:

```javascript
// Existing ETL script — works unchanged with encrypted fields
loadToCars({
    LicensePlate: this.LicensePlate,
    VinNumber: this.VinNumber,  // Encrypted blob, copied as-is
    '@metadata': {
        '@collection': 'Cars'
    }
});
```

The receiving application (HR) decrypts using `ModuleKeys["Fleet"]` when loading the replicated document.

### 8. Extension Methods

```csharp
namespace MintPlayer.Spark.Encryption.Extensions;

public static class SparkEncryptionExtensions
{
    /// <summary>
    /// Registers encryption services and configuration.
    /// </summary>
    public static IServiceCollection AddSparkEncryption(
        this IServiceCollection services,
        Action<SparkEncryptionOptions> configure);

    /// <summary>
    /// Applies encryption/decryption hooks to the RavenDB document store.
    /// Must be called after AddSpark() and before the app starts handling requests.
    /// </summary>
    public static WebApplication UseSparkEncryption(this WebApplication app);
}
```

#### Registration in Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpark(builder.Configuration);
builder.Services.AddSparkMessaging();
builder.Services.AddSparkReplication(opt => { ... });
builder.Services.AddSparkEncryption(opt =>
{
    var section = builder.Configuration.GetSection("SparkEncryption");
    opt.OwnKey = section["OwnKey"]!;
    opt.ModuleKeys = section.GetSection("ModuleKeys").Get<Dictionary<string, string>>() ?? new();
});

var app = builder.Build();

app.UseSpark();
app.UseSparkEncryption();  // Must be called after UseSpark()
app.CreateSparkIndexes();
app.CreateSparkMessagingIndexes();
app.UseSparkReplication();

app.MapSpark();
app.MapSparkReplication();

app.Run();
```

---

## Demo Application Changes

### Fleet: Add Encrypted VinNumber

**File**: `Demo/Fleet/Fleet.Library/Entities/Car.cs`

```csharp
using MintPlayer.Spark.Encryption.Abstractions;

namespace Fleet.Library.Entities;

public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }

    [Encrypted]
    public string VinNumber { get; set; } = string.Empty;
}
```

### HR: Replicated Car with Encrypted VinNumber

**File**: `Demo/HR/HR.Library/Replicated/Car.cs`

```csharp
using MintPlayer.Spark.Encryption.Abstractions;
using MintPlayer.Spark.Replication.Abstractions;

namespace HR.Library.Replicated;

[Replicated(
    SourceModule = "Fleet",
    SourceCollection = "Cars",
    EtlScript = """
        loadToCars({
            LicensePlate: this.LicensePlate,
            Model: this.Model,
            Year: this.Year,
            Color: this.Color,
            VinNumber: this.VinNumber,
            '@metadata': {
                '@collection': 'Cars'
            }
        });
    """)]
public class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }

    [Encrypted]
    public string VinNumber { get; set; } = string.Empty;
}
```

### ETL Script Note

The ETL script now includes `VinNumber: this.VinNumber` — this copies the encrypted string as-is from Fleet's database to HR's database. No decryption happens during ETL. HR's application decrypts using `ModuleKeys["Fleet"]` when loading the Car entity.

---

## Key Generation

A helper command or utility should be provided to generate AES-256 keys:

```csharp
// Generate a new 256-bit AES key
using System.Security.Cryptography;
var key = new byte[32]; // 256 bits
RandomNumberGenerator.Fill(key);
Console.WriteLine(Convert.ToBase64String(key));
```

This could be exposed as a CLI command:

```bash
dotnet run --spark-generate-encryption-key
```

---

## Implementation Steps

### Phase 1: Abstractions

1. [ ] Create `MintPlayer.Spark.Encryption.Abstractions` project
2. [ ] Implement `EncryptedAttribute`
3. [ ] Implement `SparkEncryptionOptions`

### Phase 2: Core Encryption Library

4. [ ] Create `MintPlayer.Spark.Encryption` project
5. [ ] Implement `FieldEncryptionService` (AES-256-GCM encrypt/decrypt)
6. [ ] Implement `EncryptedFieldResolver` (reflection + caching)
7. [ ] Implement RavenDB session hooks (`OnBeforeStore`, `OnAfterConversionToEntity`)
8. [ ] Implement `SparkEncryptionExtensions` (`AddSparkEncryption`, `UseSparkEncryption`)

### Phase 3: Demo Application Integration

9. [ ] Add `[Encrypted]` to `Car.VinNumber` in Fleet
10. [ ] Add `[Encrypted]` to `Car.VinNumber` in HR's replicated Car
11. [ ] Update ETL script to include VinNumber
12. [ ] Configure encryption keys in both apps' `appsettings.json`
13. [ ] Test end-to-end: Fleet stores encrypted → ETL copies → HR decrypts

### Phase 4: Polish

14. [ ] Add backward compatibility (plaintext values encrypted on next save)
15. [ ] Add key generation CLI command (`--spark-generate-encryption-key`)
16. [ ] Add logging for encryption/decryption operations
17. [ ] Add validation: warn if `[Encrypted]` is used on a property that's indexed in a queryable way

---

## Files to Create

### New Projects

- `MintPlayer.Spark.Encryption.Abstractions/MintPlayer.Spark.Encryption.Abstractions.csproj`
- `MintPlayer.Spark.Encryption/MintPlayer.Spark.Encryption.csproj`

### New Files (Abstractions)

- `EncryptedAttribute.cs`
- `Configuration/SparkEncryptionOptions.cs`

### New Files (Encryption Library)

- `Services/FieldEncryptionService.cs`
- `Services/EncryptedFieldResolver.cs`
- `Conventions/EncryptionSessionListeners.cs`
- `Extensions/SparkEncryptionExtensions.cs`

### Modified Files

- `MintPlayer.Spark.sln` — add new projects
- `Demo/Fleet/Fleet.Library/Entities/Car.cs` — add `[Encrypted]` on VinNumber
- `Demo/Fleet/Fleet/appsettings.json` — add `SparkEncryption` section
- `Demo/Fleet/Fleet/Program.cs` — add `AddSparkEncryption()` and `UseSparkEncryption()`
- `Demo/HR/HR.Library/Replicated/Car.cs` — add `[Encrypted]` on VinNumber, add VinNumber to ETL script
- `Demo/HR/HR/appsettings.json` — add `SparkEncryption` section
- `Demo/HR/HR/Program.cs` — add `AddSparkEncryption()` and `UseSparkEncryption()`

---

## Security Considerations

### Key Management

- Keys MUST NOT be committed to source control
- Use `appsettings.Production.json` (gitignored) or environment variables in production
- `appsettings.Development.json` may contain development-only keys for local testing
- Key rotation: implement a `--spark-rotate-encryption-key` command that re-encrypts all documents with a new key (future enhancement)

### Encryption Algorithm

- **AES-256-GCM** — authenticated encryption providing both confidentiality and integrity
- 12-byte random IV per encryption (never reused)
- 128-bit authentication tag for tamper detection
- Uses `System.Security.Cryptography.AesGcm` — no external crypto dependencies

### Threat Model

| Threat | Mitigation |
|--------|------------|
| Database breach (attacker reads RavenDB files) | Encrypted fields are ciphertext — unusable without the key |
| ETL copies sensitive data between databases | Data remains encrypted — only apps with the key can read it |
| Key compromised for one module | Only that module's data is exposed (per-module isolation) |
| Tampered ciphertext | GCM authentication tag verification fails → exception thrown |
| IV reuse | Fresh random IV generated per encryption call |

### Limitations

- **Encrypted fields cannot be queried or sorted in RavenDB indexes** — ciphertext is not meaningful for comparison
- **`[Encrypted]` is restricted to `string` properties** — non-string types are not supported
- **No key rotation mechanism** in the initial implementation (planned for future)
- **Model synchronization** should be aware that `[Encrypted]` string properties store ciphertext, not user-visible strings

---

## Design Decisions

1. **String-only encryption**: `[Encrypted]` is restricted to `string` properties. Most sensitive data is string-typed (VIN, SSN, email, etc.), and this avoids JSON deserialization mismatches.

2. **Backend-only concern**: Encryption is transparent to the frontend. The Angular app receives decrypted values via the API and renders normal text inputs. No UI indicators needed.

3. **Auto-generate dev keys**: In development mode, `UseSparkEncryption()` will auto-generate and persist an AES-256 key to `appsettings.Development.json` if `SparkEncryption:OwnKey` is not configured. This removes friction for local development.

---

*This document is a living specification and will be updated as the project evolves.*
