using FluentAssertions;
using VSoftSol.Syslog.Core.Search;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Search;

/// <summary>
/// The oracle's free-text matching must be identical to the SQLite FTS5 index, so the
/// tokenizer must reproduce <c>unicode61 remove_diacritics 2 tokenchars '.:-_/@'</c>.
/// </summary>
public sealed class FtsTokenizerTests
{
    [Theory]
    [InlineData("Failed login for admin", "failed|login|for|admin")]
    [InlineData("user=admin action=DENY", "user|admin|action|deny")]
    [InlineData("a,b;c d", "a|b|c|d")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:ff")]
    [InlineData("/var/log/auth.log", "/var/log/auth.log")]
    [InlineData("user@example.com", "user@example.com")]
    [InlineData("café RÉSUMÉ", "cafe|resume")]
    [InlineData("   spaced   out   ", "spaced|out")]
    [InlineData("", "")]
    [InlineData("!!!", "")]
    public void Tokenize_ProducesFtsEquivalentTokens(string input, string pipeDelimited)
    {
        string[] expected = pipeDelimited.Length == 0
            ? []
            : pipeDelimited.Split('|');

        FtsTokenizer.Tokenize(input).Should().Equal(expected);
    }

    [Fact]
    public void Tokenize_KeepsTokenCharsAttachedLikeFts5()
    {
        // FTS5 treats '.' as a token character everywhere, so "denied." is one token and a
        // bare-word "denied" query would not match it. The oracle must agree.
        FtsTokenizer.Tokenize("denied.").Should().Equal("denied.");
    }

    [Fact]
    public void Tokenize_IsCaseInsensitive()
    {
        FtsTokenizer.Tokenize("ADMIN").Should().Equal(FtsTokenizer.Tokenize("admin"));
    }
}
