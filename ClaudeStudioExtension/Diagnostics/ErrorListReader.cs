using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace ClaudeStudioExtension.Diagnostics;

/// <summary>
/// Reads the VS Error List (the same surface as the gutter/Error List window,
/// language-agnostic) filtered to a single file, formatted for feeding back to
/// claude after an edit. Mirrors the official extension's findDiagnosticsProblems.
/// </summary>
internal static class ErrorListReader
{
    /// <summary>
    /// Returns a formatted block of errors/warnings for <paramref name="filePath"/>,
    /// or an empty string when there are none. Switches to the UI thread internally.
    /// </summary>
    public static async Task<string> ReadForFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "";

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            if (Package.GetGlobalService(typeof(SVsErrorList)) is not IErrorList errorList)
                return "";
            if (errorList.TableControl is not IWpfTableControl2 tableControl)
                return "";

            // ForceUpdateAsync returns a snapshot of ALL entries (even virtualized
            // ones not currently scrolled into view).
            var result = await tableControl.ForceUpdateAsync();

            string Normalize(string p)
            {
                try { return Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); }
                catch { return p.ToLowerInvariant(); }
            }
            var target = Normalize(filePath);

            var rows = new List<(int order, string line)>();
            foreach (var handle in result.AllEntries)
            {
                if (!handle.TryGetValue(StandardTableKeyNames.DocumentName, out string doc) || string.IsNullOrEmpty(doc))
                    continue;
                if (Normalize(doc) != target)
                    continue;

                handle.TryGetValue(StandardTableKeyNames.ErrorSeverity, out __VSERRORCATEGORY sev);
                if (sev == __VSERRORCATEGORY.EC_MESSAGE)
                    continue; // skip informational messages — keep errors + warnings

                handle.TryGetValue(StandardTableKeyNames.Line, out int line);    // 0-based
                handle.TryGetValue(StandardTableKeyNames.Text, out string text);
                handle.TryGetValue(StandardTableKeyNames.ErrorCode, out string code);

                var sevName = sev == __VSERRORCATEGORY.EC_ERROR ? "error" : "warning";
                var codePart = string.IsNullOrEmpty(code) ? "" : $" {code}";
                rows.Add(((int)sev, $"  line {line + 1}: {sevName}{codePart}: {text}"));
            }

            if (rows.Count == 0)
                return "";

            // errors (EC_ERROR=0) before warnings (EC_WARNING=1)
            var ordered = rows.OrderBy(r => r.order).Select(r => r.line).ToList();
            var errorCount = rows.Count(r => r.order == (int)__VSERRORCATEGORY.EC_ERROR);
            var warnCount = rows.Count - errorCount;

            var sb = new StringBuilder();
            sb.AppendLine($"VS Error List for {Path.GetFileName(filePath)} after your edit " +
                          $"({errorCount} error(s), {warnCount} warning(s)):");
            foreach (var r in ordered)
                sb.AppendLine(r);
            sb.Append("Fix the errors above if they were caused by your edit.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"ErrorListReader failed for {filePath}: {ex.Message}");
            return "";
        }
    }

    /// <summary>Caps for the build-error prompt, so a build with hundreds of issues doesn't flood the turn.</summary>
    private const int MaxPromptErrors = 40;
    private const int MaxPromptWarnings = 20;

    /// <summary>
    /// Reads the whole Error List (no file filter) and formats a "fix these build
    /// errors" prompt. Paths are made relative to <paramref name="baseDir"/> (the
    /// agent's cwd) with forward slashes. Returns errorCount 0 with an empty prompt
    /// when the list has no errors. Switches to the UI thread internally.
    /// </summary>
    public static async Task<(int errorCount, int warningCount, string prompt)> ReadAllAsync(string? baseDir)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            if (Package.GetGlobalService(typeof(SVsErrorList)) is not IErrorList errorList)
                return (0, 0, "");
            if (errorList.TableControl is not IWpfTableControl2 tableControl)
                return (0, 0, "");

            var result = await tableControl.ForceUpdateAsync();

            string? basePrefix = null;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                try { basePrefix = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\"; }
                catch { }
            }

            string FormatPath(string doc)
            {
                try
                {
                    var full = Path.GetFullPath(doc);
                    if (basePrefix != null && full.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                        return full.Substring(basePrefix.Length).Replace('\\', '/');
                    return full.Replace('\\', '/');
                }
                catch { return doc; }
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            foreach (var handle in result.AllEntries)
            {
                handle.TryGetValue(StandardTableKeyNames.ErrorSeverity, out __VSERRORCATEGORY sev);
                if (sev != __VSERRORCATEGORY.EC_ERROR && sev != __VSERRORCATEGORY.EC_WARNING)
                    continue; // skip informational messages

                handle.TryGetValue(StandardTableKeyNames.Text, out string text);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                handle.TryGetValue(StandardTableKeyNames.DocumentName, out string doc);
                handle.TryGetValue(StandardTableKeyNames.Line, out int line);      // 0-based
                handle.TryGetValue(StandardTableKeyNames.Column, out int column);  // 0-based
                handle.TryGetValue(StandardTableKeyNames.ErrorCode, out string code);

                var codePart = string.IsNullOrEmpty(code) ? "" : $"{code}: ";
                var lineText = string.IsNullOrEmpty(doc)
                    ? $"{codePart}{text}"
                    : $"{FormatPath(doc)}({line + 1},{column + 1}): {codePart}{text}";
                (sev == __VSERRORCATEGORY.EC_ERROR ? errors : warnings).Add(lineText);
            }

            if (errors.Count == 0)
                return (0, warnings.Count, "");

            var sb = new StringBuilder();
            var warnPart = warnings.Count == 0 ? "" : $" and {warnings.Count} warning(s)";
            sb.AppendLine($"The Visual Studio build finished with {errors.Count} error(s){warnPart}. " +
                          "Investigate and fix the errors:");
            sb.AppendLine();
            sb.AppendLine("Errors:");
            AppendCapped(sb, errors, MaxPromptErrors, "error");
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings (for context):");
                AppendCapped(sb, warnings, MaxPromptWarnings, "warning");
            }
            sb.AppendLine();
            sb.Append("After making changes, rebuild to confirm the errors are resolved.");
            return (errors.Count, warnings.Count, sb.ToString());
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"ErrorListReader.ReadAllAsync failed: {ex.Message}");
            return (0, 0, "");
        }
    }

    private static void AppendCapped(StringBuilder sb, List<string> lines, int cap, string noun)
    {
        var shown = Math.Min(lines.Count, cap);
        for (var i = 0; i < shown; i++)
            sb.AppendLine($"{i + 1}. {lines[i]}");
        if (lines.Count > shown)
            sb.AppendLine($"... and {lines.Count - shown} more {noun}(s).");
    }
}
