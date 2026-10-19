using MintPlayer.Spark.Authorization;
using MintPlayer.Spark.IdentityProvider;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// D28: a library looks its own JSON-only types up by id, never by a name literal an application rename would break
/// (<c>PasskeysActions.cs:43</c>, <c>RenamePasskeyAction.cs:39</c>, <c>OrganizationCustomActions.cs:22</c>,
/// <c>TeamActions.cs:38</c>). Each constant is the id its model file ships: UUIDv5 of <c>alias:DeclaredName</c>.
/// </summary>
public class LibraryTypeIdConstantsTests
{
    public static TheoryData<string, string, Guid> Constants => new()
    {
        { "authorization:Passkeys", "libs/authorization/MintPlayer.Spark.Authorization/App_Data/Model/Passkeys.json", AuthorizationTypeIds.Passkeys },
        { "authorization:PasskeyRename", "libs/authorization/MintPlayer.Spark.Authorization/App_Data/Model/PasskeyRename.json", AuthorizationTypeIds.PasskeyRename },
        { "organizations:OrganizationInvite", "libs/organizations/MintPlayer.Spark.Organizations/App_Data/Model/OrganizationInvite.json", OrganizationTypeIds.OrganizationInvite },
        { "identity-provider:OidcInvitation", "libs/identity_provider/MintPlayer.Spark.IdentityProvider/App_Data/Model/OidcInvitation.json", IdentityProviderTypeIds.OidcInvitation },
    };

    [Theory]
    [MemberData(nameof(Constants))]
    public void Each_constant_is_the_id_the_library_ships(string seed, string file, Guid constant)
    {
        constant.Should().Be(SparkModelIds.For(seed));

        var root = (SparkJsonObject)SparkJson.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), file)));
        var po = (SparkJsonObject)root["persistentObject"]!;
        Guid.Parse(((SparkJsonString)po["id"]!).Value).Should().Be(constant);
        ((SparkJsonString)po["name"]!).Value.Should().Be(seed[(seed.IndexOf(':') + 1)..]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
