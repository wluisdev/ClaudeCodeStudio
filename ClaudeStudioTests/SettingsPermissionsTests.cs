using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class SettingsPermissionsParseTests
{
    [Fact]
    public void Reads_all_three_buckets()
    {
        var (allow, ask, deny) = SettingsPermissions.Parse("""
            {
              "permissions": {
                "allow": ["PowerShell(git status)", "mcp__github"],
                "ask":   ["Edit"],
                "deny":  ["PowerShell(git push*)"]
              }
            }
            """);

        Assert.Equal(new[] { "PowerShell(git status)", "mcp__github" }, allow);
        Assert.Equal(new[] { "Edit" }, ask);
        Assert.Equal(new[] { "PowerShell(git push*)" }, deny);
    }

    [Fact]
    public void Ignores_a_settings_file_with_no_permissions_block()
    {
        var (allow, ask, deny) = SettingsPermissions.Parse("""{"model":"opus"}""");

        Assert.Empty(allow);
        Assert.Empty(ask);
        Assert.Empty(deny);
    }

    // A settings file being edited by hand is routinely half-written; failing the
    // turn over it would be worse than running with the rules we already had.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("""{"permissions": "nope"}""")]
    [InlineData("""{"permissions": {"allow": "not-an-array"}}""")]
    public void Never_throws_on_junk(string? json)
    {
        var (allow, ask, deny) = SettingsPermissions.Parse(json);

        Assert.Empty(allow);
        Assert.Empty(ask);
        Assert.Empty(deny);
    }

    [Fact]
    public void Skips_non_string_entries_and_trims_the_rest()
    {
        var (allow, _, _) = SettingsPermissions.Parse("""
            {"permissions": {"allow": ["  Read  ", 42, null, "", "   "]}}
            """);

        Assert.Equal(new[] { "Read" }, allow);
    }

    // Claude Code tolerates comments and trailing commas in its settings files, so
    // a file the CLI accepts must not be silently ignored here.
    [Fact]
    public void Accepts_comments_and_trailing_commas()
    {
        var (allow, _, _) = SettingsPermissions.Parse("""
            {
              // shell is fine in this repo
              "permissions": { "allow": ["PowerShell", ] }
            }
            """);

        Assert.Equal(new[] { "PowerShell" }, allow);
    }
}

public class SettingsPermissionsToolMatchTests
{
    [Fact]
    public void Matches_the_same_tool_ignoring_case()
    {
        Assert.True(SettingsPermissions.ToolMatches("PowerShell", "PowerShell"));
        Assert.True(SettingsPermissions.ToolMatches("powershell", "PowerShell"));
    }

    [Fact]
    public void Does_not_match_a_different_tool()
    {
        Assert.False(SettingsPermissions.ToolMatches("Bash", "PowerShell"));
    }

    // The point of issues/1: the server-wide form is what claude's own docs show.
    [Fact]
    public void A_server_wide_mcp_rule_covers_that_servers_tools()
    {
        Assert.True(SettingsPermissions.ToolMatches("mcp__github", "mcp__github__search_code"));
        Assert.True(SettingsPermissions.ToolMatches("mcp__testeLocal", "mcp__testeLocal__soma"));
    }

    // The "__" boundary is what keeps one server from swallowing another's tools.
    [Fact]
    public void A_server_prefix_does_not_leak_into_a_longer_server_name()
    {
        Assert.False(SettingsPermissions.ToolMatches("mcp__git", "mcp__github__search_code"));
    }

    // Prefix behaviour is reserved for MCP: a rule saying "Power" must not decide
    // calls to PowerShell.
    [Fact]
    public void Non_mcp_rules_stay_exact()
    {
        Assert.False(SettingsPermissions.ToolMatches("Power", "PowerShell"));
        Assert.False(SettingsPermissions.ToolMatches("Edit", "NotebookEdit"));
    }

    [Theory]
    [InlineData(null, "PowerShell")]
    [InlineData("PowerShell", null)]
    [InlineData("", "")]
    public void Empty_input_never_matches(string? rule, string? tool)
    {
        Assert.False(SettingsPermissions.ToolMatches(rule, tool));
    }
}
