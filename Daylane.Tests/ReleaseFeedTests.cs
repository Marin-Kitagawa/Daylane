using Daylane.Services;

namespace Daylane.Tests;

public class ReleaseFeedTests
{
    [Fact]
    public void Parse_ReadsTheTagAndThePageUrl()
    {
        const string json = """
            {"tag_name":"v1.0.2","html_url":"https://github.com/Marin-Kitagawa/Daylane/releases/tag/v1.0.2","draft":false,"prerelease":false}
            """;

        var release = ReleaseFeed.Parse(json);

        Assert.NotNull(release);
        Assert.Equal("v1.0.2", release.TagName);
        Assert.Equal("https://github.com/Marin-Kitagawa/Daylane/releases/tag/v1.0.2", release.HtmlUrl);
    }

    [Fact]
    public void Parse_RejectsADraft()
    {
        const string json = """
            {"tag_name":"v9.0.0","html_url":"https://example.com/r","draft":true,"prerelease":false}
            """;

        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void Parse_RejectsAPrerelease()
    {
        const string json = """
            {"tag_name":"v9.0.0","html_url":"https://example.com/r","draft":false,"prerelease":true}
            """;

        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void Parse_ReturnsNullForAnythingUnusable(string? json)
    {
        // Rate-limit bodies are valid JSON with no tag_name. They must read as "nothing to
        // report", not as an exception escaping into a background thread.
        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void Parse_ReturnsNullWhenTheTagIsPresentButThereIsNoUrl()
    {
        const string json = """{"tag_name":"v1.0.2","draft":false,"prerelease":false}""";

        // Offering an update with nowhere to send the user is worse than offering none.
        Assert.Null(ReleaseFeed.Parse(json));
    }
}
