using Xunit;
using Baseport;

namespace Baseport.Tests;

// The break-glass sign-in codes.
public class OneTimeCodesTests
{
    // A code for a user with none outstanding; never null, so tests read plainly.
    private static string Fresh(string username)
    {
        var (code, _) = OneTimeCodes.Issue(username);
        Assert.NotNull(code);
        return code!;
    }

    public OneTimeCodesTests() => OneTimeCodes.Reset();

    [Fact]
    public void A_just_issued_code_signs_in_once()
    {
        var code = Fresh("admin");
        Assert.NotNull(code);
        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void A_code_is_spent_after_one_sign_in()
    {
        var code = Fresh("admin");
        Assert.True(OneTimeCodes.Consume("admin", code));
        Assert.False(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void A_wrong_code_is_rejected_and_leaves_the_real_one_usable()
    {
        var code = Fresh("admin");
        Assert.False(OneTimeCodes.Consume("admin", "0123456789"));
        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void A_user_that_never_asked_for_a_code_has_nothing_to_consume()
    {
        Assert.False(OneTimeCodes.Consume("nobody", "AAAAAAAAAA"));
    }

    [Fact]
    public void A_mutated_code_is_rejected_and_surrounding_space_is_trimmed()
    {
        var code = Fresh("admin");
        var mutated = code.Substring(0, code.Length - 1) + "0";
        Assert.False(OneTimeCodes.Consume("admin", mutated));
        Assert.True(OneTimeCodes.Consume("admin", $"  {code}  "));
    }

    [Fact]
    public void A_new_code_is_refused_while_the_first_is_still_live()
    {
        var (_, retryAfter) = OneTimeCodes.Issue("admin");
        Assert.Equal(TimeSpan.Zero, retryAfter);

        var (code, retryAfterAgain) = OneTimeCodes.Issue("admin");
        Assert.Null(code);
        Assert.True(retryAfterAgain > TimeSpan.Zero);
        Assert.True(retryAfterAgain <= OneTimeCodes.CodeLifetime);
    }

    [Fact]
    public void Lifetime_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OneTimeCodes.CodeLifetime);
    }

    [Fact]
    public void A_live_code_is_never_replaced()
    {
        // Hammering used to burn the operator's live code; a code now stands until it expires.
        var now = DateTime.UtcNow;
        var (code, _) = OneTimeCodes.IssueAt("admin", now);
        Assert.NotNull(code);

        var (later, retryAfter) = OneTimeCodes.IssueAt("admin", now + OneTimeCodes.MinimumInterval + TimeSpan.FromSeconds(1));
        Assert.Null(later);
        Assert.True(retryAfter > TimeSpan.Zero);

        // The original code still signs in: it was never replaced.
        Assert.True(OneTimeCodes.Consume("admin", code));
    }

    [Fact]
    public void An_expired_code_can_be_replaced()
    {
        var now = DateTime.UtcNow;
        var (_, _) = OneTimeCodes.IssueAt("admin", now);

        var (code, _) = OneTimeCodes.IssueAt("admin", now + OneTimeCodes.CodeLifetime + TimeSpan.FromSeconds(1));
        Assert.NotNull(code);
    }

    [Fact]
    public void Pruning_drops_expired_codes_and_keeps_live_ones()
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
