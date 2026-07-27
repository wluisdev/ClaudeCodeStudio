# Changelog

All notable changes to Claude Code Studio are documented here. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
