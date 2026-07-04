# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Greenfield. No code exists yet — only the PRD (title: **ClaudeStatusTray for Windows**). This file captures the intended architecture so implementation can start. When you write code, update the Commands section below with the real build/test invocations.

## What this is

A Windows **system-tray** indicator that mirrors the macOS [m1ckc3s/claude-status-bar](https://github.com/m1ckc3s/claude-status-bar) menu-bar app: it shows Claude Code's live status (thinking / running a tool / awaiting permission / idle) and the current turn's elapsed time. No window, no dashboard.

Two parts, developed independently:
1. **Hooks** (Node.js, ported from upstream) — Claude Code hook scripts that write per-session state files. Reused near-verbatim from the macOS project.
2. **Tray app** (C#/.NET WinForms `NotifyIcon`, written from scratch) — a resident process that polls those state files and renders the icon. The macOS Swift UI is **discarded, not ported**.

The two sides communicate only through the state-file contract below. There is no other coupling — the tray app is a pure consumer.

## The integration contract (do not change the schema)

Each live session writes one JSON file to `%USERPROFILE%\.claude\statusbar\state.d\<session_id>.json`. This schema is inherited from upstream and is the source of truth linking the two halves:

- `state`: `idle | thinking | tool | permission | done` — drives the icon.
- `label`, `tool`, `project` (`basename(cwd)`), `sessionId`, `transcript`, `entrypoint` (`CLAUDE_CODE_ENTRYPOINT`), `term_program` (`TERM_PROGRAM`).
- `pid`: the session's `claude` process — liveness probe.
- `started`: bool — `false` = merely opened, `true` = real activity (only these show in the session list).
- `startedAt`: epoch s, turn start; `0` when idle/permission/done. Used for the elapsed timer.
- `ts`: epoch s, last write — staleness check.

**Hook → state mapping** (already implemented upstream, don't redesign): `UserPromptSubmit→thinking`, `PreToolUse→tool`, `PostToolUse→thinking`, `Notification`(permission only)`/PermissionRequest→permission`, `Stop→done`, `SessionStart→seed idle`, `SessionEnd→delete file`.

**Consumer rules:** poll `state.d/` (~400ms), re-parse only files whose mtime changed, ignore `.tmp` files, tolerate parse errors, drop sessions whose `pid` is dead or `ts` is stale. Icon priority: **permission > tool > thinking > idle**.

## Porting map (macOS → Windows)

The hook files are ported from upstream. Reuse `hooks/update.js` (pure JS, cross-platform) as-is. The Windows-specific edits are concentrated in:
- `hooks/lifecycle.js` — replace `pgrep`/`open -b` (liveness: `Process.GetProcessById`; launch: resident autostart, not per-hook launch).
- `hooks/install.js` — guard `getuid`/`launchctl` behind `os.platform() !== 'win32'`, and **quote node + script paths** in the hook command strings (paths contain spaces; macOS tolerates unquoted, Windows does not).

Everything else macOS-specific (`NSWorkspace` focus, menu-bar theming, `open -a`) is re-implemented natively in the C# app via WinAPI (`SetForegroundWindow`, `OpenProcess`, `SystemUsesLightTheme` registry key for light/dark taskbar). Autostart is an HKCU `...\Run` key, not a LaunchAgent.

## Known-unsettled (verify empirically, don't assume)

- **Hook shell + quoting on Windows.** Whether Claude Code runs hook commands via `cmd.exe` or PowerShell — and thus the exact quoting for `install.js` — is unconfirmed. Validate on a real session before hardcoding the command string.
- **Terminal focus is weak.** `TERM_PROGRAM` is unreliable under Windows Terminal (which sets `WT_SESSION`). Expect window-level focus at best; treat click-to-focus (FR4) as best-effort.
- **Node discovery.** nvm-windows PATH layout differs from macOS nvm; hook commands may need to resolve node explicitly.

## Milestones (build order)

1. **M1** — get the ported hooks writing all state files correctly on a real Windows session. No UI; validate by tailing `state.d/`.
2. **M2** — resident tray app: poll, priority-pick, static per-state icons, tooltip, autostart.
3. **M3** — elapsed timer, settings (persisted), click-to-focus.
4. **M4** — animation frames, completion sound, update check, installer packaging.

Do M1 (hooks) fully before M2 (app) — the app has nothing to consume until state files land.
