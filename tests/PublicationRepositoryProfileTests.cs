using VmbLauncher.Services;

namespace VmbLauncher.Tests;

public class PublicationRepositoryProfileTests
{
    [Theory]
    [InlineData("Ensrick/doomrocket-private", "doomrocket", "3794172730", "0.1.68-dev", "private-copy", true)]
    [InlineData("Ensrick/doomrocket-public", "doomrocket", "3771657344", "0.1.56-alpha", "main", true)]
    [InlineData("Ensrick/doomrocket-private", "doomrocket", "3771657344", "0.1.68-dev", "private-copy", false)]
    [InlineData("Ensrick/doomrocket-public", "doomrocket", "3794172730", "0.1.56-alpha", "main", false)]
    [InlineData("Ensrick/doomrocket-private", "doomrocket", "3794172730", "0.1.68-alpha", "private-copy", false)]
    [InlineData("Ensrick/doomrocket-private", "doomrocket", "3794172730", "0.1.68-dev", "main", false)]
    [InlineData("Ensrick/doomrocket-private", "weapon_tweaker", "3794172730", "0.1.68-dev", "private-copy", false)]
    [InlineData("other/doomrocket-private", "doomrocket", "3794172730", "0.1.68-dev", "private-copy", false)]
    public void BindsRepositoryModItemVersionAndBranch(string repo, string mod, string id,
        string version, string branch, bool expected) =>
        Assert.Equal(expected, PublicationRepositoryProfile.MatchesChannel(repo, mod, id, version, branch));

    [Fact]
    public void PreservesTweakerLayoutAndRejectsWarlockInTweaker()
    {
        Assert.Equal("weapon_tweaker/", PublicationRepositoryProfile.Prefix("weapon_tweaker"));
        Assert.Equal("", PublicationRepositoryProfile.Prefix("doomrocket"));
        Assert.True(PublicationRepositoryProfile.Allows(PublicationReceiptGate.GitHubRepo, "weapon_tweaker"));
        Assert.False(PublicationRepositoryProfile.Allows(PublicationReceiptGate.GitHubRepo, "doomrocket"));
        Assert.False(PublicationRepositoryProfile.Allows("other/repo", "doomrocket"));
    }
}
