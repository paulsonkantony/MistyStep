using MistyStep.Core.Settings;

namespace MistyStep.Core.Tests.Settings;

public class UpdateCheckTests
{
    private const string Url = "https://github.com/paulsonkantony/MistyStep/releases/tag/v1.2.0";

    [Fact]
    public void Reads_a_stable_release()
    {
        var release = UpdateCheck.ParseLatest($$"""{"tag_name":"v1.2.0","html_url":"{{Url}}","draft":false,"prerelease":false}""");
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("v1.2.0", release.Tag);
        Assert.Equal(Url, release.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("""{"tag_name":"v1.2.0","draft":true}""")]
    [InlineData("""{"tag_name":"v1.2.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"v1.2.0-beta.1"}""")]
    [InlineData("""{"tag_name":"latest"}""")]
    public void Ignores_anything_else(string? json) => Assert.Null(UpdateCheck.ParseLatest(json));

    [Fact]
    public void Links_only_to_this_projects_release_pages()
    {
        var foreign = UpdateCheck.ParseLatest("""{"tag_name":"v2.0.0","html_url":"https://evil.example/download"}""");
        Assert.Equal(ProjectInfo.ReleasesUrl, foreign!.Url);
        var missing = UpdateCheck.ParseLatest("""{"tag_name":"v2.0.0"}""");
        Assert.Equal(ProjectInfo.ReleasesUrl, missing!.Url);
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1", "0.1.0", false)]
    [InlineData("v0.1.0.1", "0.1.0", true)]
    [InlineData("v0.0.9", "0.1.0", false)]
    [InlineData("v9.0.0", "not a version", false)]
    public void Compares_versions(string tag, string running, bool newer)
    {
        Assert.True(UpdateCheck.TryParseVersion(tag, out var version));
        Assert.Equal(newer, UpdateCheck.IsNewer(new ReleaseInfo(version!, tag, ProjectInfo.ReleasesUrl), running));
    }
}
