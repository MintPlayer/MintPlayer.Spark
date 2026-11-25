using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.Spark.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The one avatar every page draws (#700 M11, PRD Q15): the custom one when it can be served, otherwise the forge's. A custom
/// avatar that would answer 404 (not scanned clean, no thumbnail) must never replace a working forge avatar.
/// </summary>
public class AccountAvatarTests
{
    private const string Forge = "https://avatars.example/acme.png";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 1);

    private static Account With(SparkImage? avatar) => new() { Id = Id<Account>.FromTrusted(AccountId), GitHubId = 1, Login = "acme", AvatarUrl = Forge, CustomAvatar = avatar };

    private static SparkImage Avatar(SparkFileScanStatus status = SparkFileScanStatus.Clean, bool thumb = true)
    {
        var image = new SparkImage { FileId = "f-1", ContentType = "image/png", ScanStatus = status };
        if (thumb) image.Variants["thumb"] = new SparkImageVariant { FileId = "f-1.thumb", ContentType = "image/webp" };
        return image;
    }

    [Fact]
    public void A_served_custom_avatar_wins_with_its_checked_thumbnail_url()
    {
        var url = AccountAvatar.Resolve(With(Avatar()));

        url.Should().Be(SparkFileDownloadUrl.For(new SparkFileLocation(AccountAvatar.AccountType, AccountId.ToLowerInvariant(), "CustomAvatar", Avatar(), "thumb")));
        url.Should().StartWith("/spark/files/download?type=");
        url.Should().EndWith("&attribute=CustomAvatar&file=f-1&variant=thumb");
    }

    [Fact]
    public void Without_a_custom_avatar_the_forge_avatar_is_drawn()
        => AccountAvatar.Resolve(With(null)).Should().Be(Forge);

    [Theory]
    [InlineData(SparkFileScanStatus.Pending)]
    [InlineData(SparkFileScanStatus.Infected)]
    [InlineData(SparkFileScanStatus.ScanFailed)]
    public void A_custom_avatar_that_is_not_clean_falls_back_to_the_forge(SparkFileScanStatus status)
        => AccountAvatar.Resolve(With(Avatar(status))).Should().Be(Forge);

    [Fact]
    public void A_custom_avatar_without_its_thumbnail_falls_back_to_the_forge()
        => AccountAvatar.Resolve(With(Avatar(thumb: false))).Should().Be(Forge);

    [Fact]
    public void An_account_with_neither_has_no_avatar()
        => AccountAvatar.Resolve(new Account { Id = Id<Account>.FromTrusted(AccountId), GitHubId = 1, Login = "acme" }).Should().BeNull();
}
