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
}
