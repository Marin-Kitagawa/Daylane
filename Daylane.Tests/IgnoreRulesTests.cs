using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class IgnoreRulesTests
{
    private static IgnoreRule[] Rules(params IgnoreRule[] rules) => rules;

    [Fact]
    public void EmptyRuleList_ExcludesNothing()
        => Assert.False(IgnoreRules.IsExcluded("chrome", "anything", Array.Empty<IgnoreRule>()));

    [Fact]
    public void NullKeyword_ExcludesTheWholeProcess()
        => Assert.True(IgnoreRules.IsExcluded("Solitaire", "any title", Rules(new IgnoreRule("Solitaire", null))));

    [Fact]
    public void KeywordMatch_IsCaseInsensitiveSubstring()
        => Assert.True(IgnoreRules.IsExcluded("WindowsTerminal", "npm DOWNLOAD running",
            Rules(new IgnoreRule("windowsterminal", "download"))));

    [Fact]
    public void KeywordMiss_DoesNotExclude()
        => Assert.False(IgnoreRules.IsExcluded("WindowsTerminal", "editing a file",
            Rules(new IgnoreRule("WindowsTerminal", "download"))));

    [Fact]
    public void ProcessNameMustMatch_KeywordAloneIsNotEnough()
        => Assert.False(IgnoreRules.IsExcluded("chrome", "download page",
            Rules(new IgnoreRule("WindowsTerminal", "download"))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKeyword_MatchesNothing(string keyword)
    {
        // Contains("") is always true. Treating an empty keyword as "match everything" would
        // let one slip in the UI silently exclude an entire application.
        Assert.False(IgnoreRules.IsExcluded("chrome", "any title at all",
            Rules(new IgnoreRule("chrome", keyword))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RuleWithEmptyProcessName_IsInert(string processName)
        => Assert.False(IgnoreRules.IsExcluded("chrome", "any title",
            Rules(new IgnoreRule(processName, null))));

    [Fact]
    public void NullTitle_StillMatchesAWholeProcessRule()
        => Assert.True(IgnoreRules.IsExcluded("Solitaire", null, Rules(new IgnoreRule("Solitaire", null))));

    [Fact]
    public void NullTitle_NeverMatchesAKeywordRule()
        => Assert.False(IgnoreRules.IsExcluded("chrome", null, Rules(new IgnoreRule("chrome", "mail"))));

    [Fact]
    public void AnyMatchingRule_Excludes()
        => Assert.True(IgnoreRules.IsExcluded("chrome", "webmail",
            Rules(new IgnoreRule("firefox", null), new IgnoreRule("chrome", "mail"))));

    [Fact]
    public void ProcessNameComparison_IgnoresSurroundingWhitespace()
        => Assert.True(IgnoreRules.IsExcluded("chrome", "x", Rules(new IgnoreRule("  chrome  ", null))));
}
