# Proposed Features & Future Roadmap: ClaudeStatusTray

This document details new features proposed for the **ClaudeStatusTray for Windows** application to enhance developer productivity, customization, and system diagnostics.

---

## 🚀 1. Interactive Windows Toast Notifications
Currently, when a session transitions to `permission`, the tray icon alerts the user. However, if the terminal window is covered or minimized, this might still be missed.

- **Proposed Solution**: Trigger a native Windows Toast Notification when a session goes into `permission` state.
- **Interactivity**: Clicking the toast notification automatically invokes [FocusSession](file:///c:/Users/Vyshnav%20Suresh/Documents/notification-ide-ai/app/Program.cs#L562) to bring the blocked terminal to the foreground immediately.
- **Effort / Priority**: Medium Effort | High Priority.

---

## 💀 2. Session Process Management (Kill Process)
If an agent gets stuck in a loop, runs a heavy task, or hangs, the user currently has to find the process ID (PID) manually in Task Manager or run `taskkill` in another terminal.

- **Proposed Solution**: Add a "Kill Session" button or context menu option next to each running session row.
- **Implementation**: The tray app already reads the session process ID (`pid`). Selecting "Kill" will send a process termination command (`Process.GetProcessById(pid).Kill(true)`).
- **Effort / Priority**: Low Effort | High Priority.

---

## 🔊 3. Sound Customization Profiles
Currently, the completion sound is a synthesized two-note chime that is either ON or OFF.

- **Proposed Solution**: Expand the audio configuration to support multiple sound profiles:
  - **Gentle Chime** (Default synthesized wave)
  - **Retro Beep** (Short square wave sound)
  - **Custom Wave File** (Allows picking a local `.wav` file path via a settings configuration)
- **Controls**: Add a volume adjustment slider or volume percentages (20%, 50%, 100%) in the settings menu.
- **Effort / Priority**: Medium Effort | Medium Priority.

---

## 📋 4. Hover Flyout / Mini-Dashboard
Hovering over the tray icon currently displays a simple text tooltip.

- **Proposed Solution**: Replace the default tooltip with a lightweight native flyout window (using a borderless Form or a WPF panel) when hovering.
- **Flyout Details**:
  - Showing active session list with elapsed progress timers.
  - Showing CPU and Memory metrics of the agent process (`pid`).
  - Quick action buttons (Focus, Kill, View Transcript).
- **Effort / Priority**: High Effort | Medium Priority.

---

## 📝 5. Quick Transcript and Working Directory Access
The session state JSON contract contains `project` (working directory path) and `transcript` (path to the Claude session log file).

- **Proposed Solution**: Right-clicking a session in the tray menu exposes sub-menus:
  - **Open Project Folder**: Opens the workspace directory in File Explorer.
  - **View Log/Transcript**: Opens the session's active transcript file in the user's default text editor.
- **Effort / Priority**: Low Effort | Medium Priority.

---

## 🎨 6. Enhanced Status Pill Customization
The floating status pill provides glanceable visibility.

- **Proposed Solution**: Introduce settings in settings menu for:
  - **Opacity Control**: Let users adjust pill background opacity (e.g. 50% to 100%) for custom glassmorphism styles.
  - **Font Choice**: Allow switching between standard Windows system fonts (Segoe UI, Cascadia Code, Consolas).
  - **Hotkeys**: Configurable hotkey to toggle the visibility of the pill on-demand.
- **Effort / Priority**: Low Effort | Low Priority.

hello world

