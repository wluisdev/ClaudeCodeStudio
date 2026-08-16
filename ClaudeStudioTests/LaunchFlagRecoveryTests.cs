using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class LaunchFlagRecoveryTests
{
    // The exact message that started issue #7.
    [Fact]
    public void Parses_the_flag_from_a_real_rejection()
    {
        Assert.Equal(
            "--forward-subagent-text",
            LaunchFlagRecovery.ParseRejectedFlag("error: unknown option '--forward-subagent-text'"));
    }

    [Theory]
    [InlineData("error: unrecognized option '--foo'", "--foo")]
    [InlineData("Unknown option --bar", "--bar")]              // no quotes, different case
    [InlineData("something else entirely", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseRejectedFlag_handles_wording_variants(string? stderr, string? expected)
    {
        Assert.Equal(expected, LaunchFlagRecovery.ParseRejectedFlag(stderr));
    }

    [Fact]
    public void Drops_the_named_optional_flag_and_leaves_the_rest()
    {
        var args = new List<string>
        {
            "--input-format", "stream-json",
            "--forward-subagent-text",
            "--model", "opus",
        };

        Assert.True(LaunchFlagRecovery.TryDropRejectedFlags(
            args, "error: unknown option '--forward-subagent-text'", out var desc));

        Assert.Equal("--forward-subagent-text", desc);
        Assert.Equal(new[] { "--input-format", "stream-json", "--model", "opus" }, args);
    }

    // A load-bearing / value-taking flag being rejected must NOT trigger a drop:
    // removing its name would strand its value, and the session can't run without it
    // anyway, so the caller should surface the real error instead.
    [Fact]
    public void Does_not_drop_a_non_droppable_flag()
    {
        var args = new List<string> { "--input-format", "stream-json", "--verbose" };

        Assert.False(LaunchFlagRecovery.TryDropRejectedFlags(
            args, "error: unknown option '--verbose'", out _));

        Assert.Equal(new[] { "--input-format", "stream-json", "--verbose" }, args);
    }

    // Never strand a value: dropping must only remove standalone flags, so a
    // value-flag rejection leaves the pair intact.
    [Fact]
    public void Does_not_strand_a_value_when_a_value_flag_is_rejected()
    {
        var args = new List<string> { "--model", "opus", "--forward-subagent-text" };

        // Pretend the CLI rejected --model (value flag, not in the droppable set).
        Assert.False(LaunchFlagRecovery.TryDropRejectedFlags(
            args, "error: unknown option '--model'", out _));

        Assert.Contains("--model", args);
        Assert.Contains("opus", args);
    }

    // Message is clearly an unknown-option error but the flag name can't be parsed:
    // shed every optional flag at once as a last resort.
    [Fact]
    public void Shotgun_drops_all_optionals_when_the_flag_is_unparseable()
    {
        var args = new List<string>
        {
            "--input-format", "stream-json",
            "--forward-subagent-text",
            "--include-partial-messages",
            "--model", "opus",
        };

        Assert.True(LaunchFlagRecovery.TryDropRejectedFlags(
            args, "error: unknown option (near byte 42)", out var desc));

        Assert.Equal("2 optional flag(s)", desc);
        Assert.DoesNotContain("--forward-subagent-text", args);
        Assert.DoesNotContain("--include-partial-messages", args);
        Assert.Equal(new[] { "--input-format", "stream-json", "--model", "opus" }, args);
    }

    [Fact]
    public void No_change_when_the_error_is_not_about_an_unknown_option()
    {
        var args = new List<string> { "--forward-subagent-text" };

        Assert.False(LaunchFlagRecovery.TryDropRejectedFlags(
            args, "error: not logged in", out _));

        Assert.Equal(new[] { "--forward-subagent-text" }, args);
    }

    [Fact]
    public void The_issue7_flag_is_in_the_droppable_set()
    {
        Assert.Contains("--forward-subagent-text", LaunchFlagRecovery.DroppableOptionalFlags);
    }
}
