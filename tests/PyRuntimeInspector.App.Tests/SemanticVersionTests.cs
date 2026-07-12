using PyRuntimeInspector.App.Infrastructure;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class SemanticVersionTests
{
    [Fact]
    public void ComparisonUsesSemanticPrecedenceInsteadOfLexicalOrdering()
    {
        Assert.True(SemanticVersion.Parse("26.10.0").CompareTo(SemanticVersion.Parse("26.9.99")) > 0);

        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        }.Select(SemanticVersion.Parse).ToArray();

        for (var index = 1; index < ordered.Length; index++)
            Assert.True(ordered[index - 1].CompareTo(ordered[index]) < 0);
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("26.7.11")]
    [InlineData("1.2.3-rc.1+build.42")]
    public void ValidSemanticVersionsRoundTripWithoutBuildMetadata(string value)
    {
        var version = SemanticVersion.Parse(value);

        Assert.Equal(value.Split('+')[0], version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3 alpha")]
    public void InvalidSemanticVersionsAreRejected(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));
    }
}
