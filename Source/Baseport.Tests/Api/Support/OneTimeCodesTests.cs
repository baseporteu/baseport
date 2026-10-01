using Xunit;
using Baseport;

namespace Baseport.Tests;

public class OneTimeCodesTests
{

    private static string Fresh(string username)
    {
        var (code, _) = OneTimeCodes.Issue(username);
        Assert.NotNull(code);
        return code!;
    }

    public OneTimeCodesTests() => OneTimeCodes.Reset();

    [Fact]
    public void FreshCodeSignsIn()
    {
        var code = Fresh("admin");
        Assert.NotNull(code);
        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void CodeIsSingleUse()
    {
        var code = Fresh("admin");
        Assert.True(OneTimeCodes.Consume("admin", code));
        Assert.False(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void WrongCodeKeepsRealCode()
    {
        var code = Fresh("admin");
        Assert.False(OneTimeCodes.Consume("admin", "0123456789"));
        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void NoCodeNothingToConsume()
    {
        Assert.False(OneTimeCodes.Consume("nobody", "AAAAAAAAAA"));
    }

    [Fact]
    public void MutatedCodeRejected()
    {
        var code = Fresh("admin");

        var mutatedChar = code[^1] == '0' ? '1' : '0';
        var mutated = code.Substring(0, code.Length - 1) + mutatedChar;
        Assert.False(OneTimeCodes.Consume("admin", mutated));
        Assert.True(OneTimeCodes.Consume("admin", $"  {code}  "));
    }

    [Fact]
    public void LiveCodeBlocksNew()
    {
        var (_, retryAfter) = OneTimeCodes.Issue("admin");
        Assert.Equal(TimeSpan.Zero, retryAfter);

        var (code, retryAfterAgain) = OneTimeCodes.Issue("admin");
        Assert.Null(code);
        Assert.True(retryAfterAgain > TimeSpan.Zero);
        Assert.True(retryAfterAgain <= OneTimeCodes.CodeLifetime);
    }

    [Fact]
    public void LifetimeIsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OneTimeCodes.CodeLifetime);
    }

    [Fact]
    public void LiveCodeNotReplaced()
    {

        var now = DateTime.UtcNow;
        var (code, _) = OneTimeCodes.IssueAt("admin", now);
        Assert.NotNull(code);

        var (later, retryAfter) = OneTimeCodes.IssueAt("admin", now + OneTimeCodes.MinimumInterval + TimeSpan.FromSeconds(1));
        Assert.Null(later);
        Assert.True(retryAfter > TimeSpan.Zero);

        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void ExpiredCodeReplaceable()
    {
        var now = DateTime.UtcNow;
        var (_, _) = OneTimeCodes.IssueAt("admin", now);

        var (code, _) = OneTimeCodes.IssueAt("admin", now + OneTimeCodes.CodeLifetime + TimeSpan.FromSeconds(1));
        Assert.NotNull(code);
    }

    [Fact]
    public void PruneKeepsLiveCodes()
    {
        var now = DateTime.UtcNow;
        OneTimeCodes.IssueAt("spent", now);
        OneTimeCodes.IssueAt("live", now);

        Assert.Equal(0, OneTimeCodes.PruneExpired(now));

        var expiry = now + OneTimeCodes.CodeLifetime + TimeSpan.FromSeconds(1);
        OneTimeCodes.IssueAt("live", expiry);
        Assert.Equal(1, OneTimeCodes.PruneExpired(expiry));
        Assert.Equal(0, OneTimeCodes.PruneExpired(expiry));
    }
}
