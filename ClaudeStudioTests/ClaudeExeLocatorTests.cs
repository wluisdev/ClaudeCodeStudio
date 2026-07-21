using System.Collections.Generic;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

// FindClaudeExe's precedence (configured > native install > PATH > fallbacks)
// depends on real filesystem/PATH state in production. Tests inject fake
// fileExists/directoryExists/pathDirs/nativeInstallPath/fallbackPaths so the
// precedence chain is deterministic regardless of what's actually installed
// on the machine running the tests.
public class ClaudeExeLocatorTests
{
    private const string Native = @"C:\Users\fake\.local\bin\claude.exe";

    private static string FindClaudeExe(
        string? configured,
        System.Action<string>? warn = null,
        HashSet<string>? existingFiles = null,
        HashSet<string>? existingDirs = null,
        IReadOnlyList<string>? pathDirs = null,
        string? nativeInstallPath = null,
        IReadOnlyList<string>? fallbackPaths = null)
    {
        existingFiles ??= new HashSet<string>();
        existingDirs ??= new HashSet<string>();
        return ClaudeExeLocator.FindClaudeExe(
            configured,
            warn,
            fileExists: existingFiles.Contains,
            directoryExists: existingDirs.Contains,
            pathDirs: pathDirs ?? new List<string>(),
            nativeInstallPath: nativeInstallPath ?? Native,
            fallbackPaths: fallbackPaths ?? new List<string>());
    }

    [Fact]
    public void Configured_file_path_that_exists_is_returned_as_is()
    {
        var warned = false;
        var result = FindClaudeExe(
            @"C:\custom\claude.exe",
            warn: _ => warned = true,
            existingFiles: new HashSet<string> { @"C:\custom\claude.exe" });

        Assert.Equal(@"C:\custom\claude.exe", result);
        Assert.False(warned);
    }

    [Fact]
    public void Configured_directory_containing_claude_exe_resolves_to_the_exe_inside_it()
    {
        var result = FindClaudeExe(
            @"C:\custom\dir",
            existingDirs: new HashSet<string> { @"C:\custom\dir" },
            existingFiles: new HashSet<string> { @"C:\custom\dir\claude.exe" });

        Assert.Equal(@"C:\custom\dir\claude.exe", result);
    }

    [Fact]
    public void Configured_path_that_does_not_exist_throws_with_the_bad_path_in_the_message()
    {
        var ex = Assert.Throws<ClaudeNotFoundException>(() => FindClaudeExe(@"C:\nope\claude.exe"));
        Assert.Contains(@"C:\nope\claude.exe", ex.Message);
    }

    [Fact]
    public void Whitespace_only_configured_is_treated_as_unconfigured()
    {
        var result = FindClaudeExe("   ", existingFiles: new HashSet<string> { Native });
        Assert.Equal(Native, result);
    }

    [Fact]
    public void Configured_path_wins_even_when_native_install_also_exists()
    {
        var warned = false;
        var result = FindClaudeExe(
            @"C:\custom\claude.exe",
            warn: _ => warned = true,
            existingFiles: new HashSet<string> { @"C:\custom\claude.exe", Native });

        Assert.Equal(@"C:\custom\claude.exe", result);
        Assert.False(warned);
    }

    [Fact]
    public void Native_install_exists_with_no_PATH_match_returns_native_and_does_not_warn()
    {
        var warned = false;
        var result = FindClaudeExe(
            null,
            warn: _ => warned = true,
            existingFiles: new HashSet<string> { Native },
            pathDirs: new[] { @"C:\shim" });

        Assert.Equal(Native, result);
        Assert.False(warned);
    }

    [Fact]
    public void Native_install_exists_and_a_different_claude_exe_earlier_on_PATH_returns_native_but_warns()
    {
        string? warning = null;
        var result = FindClaudeExe(
            null,
            warn: msg => warning = msg,
            existingFiles: new HashSet<string> { Native, @"C:\shim\claude.exe" },
            pathDirs: new[] { @"C:\shim" });

        Assert.Equal(Native, result);
        Assert.NotNull(warning);
        Assert.Contains(@"C:\shim\claude.exe", warning);
    }

    [Fact]
    public void Native_install_and_the_PATH_match_are_the_same_file_does_not_warn()
    {
        var warned = false;
        var result = FindClaudeExe(
            null,
            warn: _ => warned = true,
            existingFiles: new HashSet<string> { Native },
            pathDirs: new[] { @"C:\Users\fake\.local\bin" });

        Assert.Equal(Native, result);
        Assert.False(warned);
    }

    [Fact]
    public void No_native_install_but_PATH_has_a_match_returns_the_PATH_match()
    {
        var result = FindClaudeExe(
            null,
            existingFiles: new HashSet<string> { @"C:\onpath\claude.exe" },
            pathDirs: new[] { @"C:\onpath" });

        Assert.Equal(@"C:\onpath\claude.exe", result);
    }

    [Fact]
    public void PATH_scan_stops_at_the_first_matching_directory()
    {
        var result = FindClaudeExe(
            null,
            existingFiles: new HashSet<string> { @"C:\dirB\claude.exe", @"C:\dirC\claude.exe" },
            pathDirs: new[] { @"C:\dirA", @"C:\dirB", @"C:\dirC" });

        Assert.Equal(@"C:\dirB\claude.exe", result);
    }

    [Fact]
    public void No_native_no_PATH_falls_back_to_first_existing_fallback_path_in_order()
    {
        var result = FindClaudeExe(
            null,
            existingFiles: new HashSet<string> { @"C:\f2\claude.exe", @"C:\f3\claude.exe" },
            fallbackPaths: new[] { @"C:\f1\claude.exe", @"C:\f2\claude.exe", @"C:\f3\claude.exe" });

        Assert.Equal(@"C:\f2\claude.exe", result);
    }

    [Fact]
    public void Nothing_found_anywhere_throws_the_generic_not_found_message()
    {
        var ex = Assert.Throws<ClaudeNotFoundException>(() => FindClaudeExe(null));
        Assert.Equal("claude.exe was not found on your PATH or any standard install location.", ex.Message);
    }
}
