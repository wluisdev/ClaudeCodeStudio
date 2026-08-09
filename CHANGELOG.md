# Changelog

All notable changes to Claude Code Studio are documented here. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0] - 2026-08-08

### Security

- **Tool calls that skipped the permission prompt are now gated.** In ask and plan mode the CLI runs under `bypassPermissions` so the extension's own hook is the only checkpoint, and that hook matched a hard-coded list of tool names. Anything absent from the list ran with no prompt at all — including `PowerShell`, which is the default shell tool on Windows in Claude Code 2.1.226, so shell commands could execute unprompted. The check is now inverted: every tool is gated except a known read-only set (`Read`, `Glob`, `Grep`, `NotebookRead`, `ToolSearch`, `TodoWrite`, `BashOutput`, `WebSearch`), so a tool introduced by a future CLI defaults to asking.

### Added

- **Re-authenticate** in the ⌘ menu, and an inline card when a session expires mid-turn. An expired session used to surface as a plain error with no way forward, because the pre-flight check can only see whether an account exists in `~/.claude.json`, not whether its credentials still work.
- **Unanswered permission** setting (⚙ → Claude Code): how long a permission prompt may sit before it is denied for you. Defaults to **waiting for the answer**, matching the CLI's own prompt — cancelling the turn still releases it.
- Permission rules dialog now ships **clickable examples** (exact command, glob, prefix, whole tool, MCP), collapsible once you know the format.
- Auto-decisions are written to the Output window (`permission auto: <tool> — allowed by rule: …`), so a rule that silently approves is no longer indistinguishable from a tool that never needed permission.

### Fixed

- **Permission rules in `.claude/settings.json` had no effect** ([#1](https://github.com/wluisdev/ClaudeCodeStudio/issues/1)). Only rules typed into the extension's own settings panel were consulted, because the CLI's native evaluation is switched off in ask and plan mode. Rules are now also read from `.claude/settings.json`, `.claude/settings.local.json` and the user-level `settings.json`. A rule naming just an MCP server (`mcp__github`) now covers every tool that server exposes, instead of requiring each tool's full name.

  One deliberate limit: `allow` rules are **not** taken from the project's `.claude/settings.json`, since that file travels with the repository and granting permissions is not something a cloned project should be able to do to you. `deny` and `ask` are honoured from it — they can only ever be more restrictive. Put `allow` rules in `.claude/settings.local.json`, the user-level `settings.json`, or ⚙ → Permission rules; if any are found in the project file, the Output window says so and points at where to move them.
- Sign-in ran `claude login`, which Claude Code 2.1.226 no longer accepts as a command — it was parsed as a prompt, and the terminal opened a session asking what you meant. Now uses `claude auth login`, falling back to the old spelling on older CLIs.
- The sign-in overlay never closed when you re-authenticated while already signed in: it only reacted to an account *appearing* in `~/.claude.json`, which does not change in that case. Closing the terminal now dismisses it too.
- A permission prompt left unanswered was denied silently while its modal stayed on screen; answering it afterwards failed with `no pending permission request`. Prompts resolved without you — by timeout, cancel or shutdown — now retract their card and say why.
- Malformed permission rules were accepted, listed as if active, and then discarded by the agent's parser. `PowerShell *` matched nothing. They are now rejected when you add them, with a message pointing at the right form.

## [1.0.2] - 2026-07-27

### Added
- Opus 5 (`claude-opus-5`) in the model picker and pricing table, alongside Opus 4.8.

### Fixed
- The turn timer no longer keeps ticking while a permission-prompt modal is open — it now pauses the same way it already did for `AskUserQuestion` cards, and resumes (or stays paused, if another request is already queued) once the prompt is answered.

## [1.0.1] - 2026-07-24

### Added
- Marketplace listing metadata (categories, tags, license, preview image) and an extension icon, packed into the VSIX.

### Fixed
- Marketplace overview and README: corrected the chat-access instructions to **Extensions → Claude Code Studio → Chat**.
- VSIX packaging: `$(Version)` is now stamped into the final packed manifest instead of a placeholder.

## [1.0.0] - 2026-05-15

Initial release. See the [README](README.md) for the full feature set, including:

- Streaming chat panel (WebView2) with live turn indicators, auto-compact feedback, and subagent trace.
- Inline permission prompts, permission modes (ask/plan/bypass/don't-ask), and permission rules.
- Native diff viewer, rewind, and branch.
- Per-workspace session history with rename, view, and fork.
- Composer `@` file picker, slash commands & skills, editor-selection send, image/file paste, prompt history.
- Usage dashboard with cost tracking and per-session filtering.
- Command palette (⌘): Doctor, MCP status & reconnect, context usage, build-errors-to-agent.
- Workspace/trust management, integrated sign-in, working-directory cascade.
- Searchable settings panel, model picker, fallback model, configurable CLI path, status line, theme awareness.

[1.0.2]: https://github.com/wluisdev/ClaudeCodeStudio/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/wluisdev/ClaudeCodeStudio/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/wluisdev/ClaudeCodeStudio/releases/tag/v1.0.0
