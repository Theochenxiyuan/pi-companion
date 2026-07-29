using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class SkillCompletionQueryTests
{
    [Theory]
    [InlineData("/", "")]
    [InlineData("/find", "find")]
    [InlineData("/skill:", "")]
    [InlineData("/skill:find-skills", "find-skills")]
    public void TryParse_AcceptsCompactAndNativeSkillQueries(string source, string expected)
    {
        Assert.True(SkillCompletionQuery.TryParse(source, out var query));
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("普通任务")]
    [InlineData("//literal")]
    [InlineData("/find task")]
    [InlineData("/skill:find-skills task")]
    [InlineData("/find\nnext")]
    public void TryParse_IgnoresMessagesThatAreNoLongerCompletionQueries(string source)
    {
        Assert.False(SkillCompletionQuery.TryParse(source, out _));
    }

    [Fact]
    public void CreateInvocation_AlwaysProducesNativePiSyntax()
    {
        Assert.Equal("/skill:find-skills ", SkillCompletionQuery.CreateInvocation("find-skills"));
    }
}
