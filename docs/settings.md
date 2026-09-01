# Settings

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · **Settings**

---

Agent Dock has three levels of settings, each stored somewhere different:

| Level | Dialog | Stored in | Applies to |
|-------|--------|-----------|------------|
| **App** | Settings → App Settings… | `%LOCALAPPDATA%\AgentDock\settings.json` | Every workspace, every launch |
| **Workspace** | Settings → Workspace Settings… | The `.agentdock` [workspace file](workspace.md) | The workspace you have open |
| **Project** | Right-click a tab → Project Settings… | `.agentdock/settings.json` inside the project folder | That one project |

The narrower level wins. A workspace's theme overrides the app default theme.

**Contents**

- [App Settings](#app-settings)
- [Workspace Settings](#workspace-settings)
- [Project Settings](#project-settings)
- [Sound notifications](#sound-notifications)
- [Themes](#themes)
- [Update channel](#update-channel)
- [Logs](#logs)

---

## App Settings

**Settings → App Settings…** — a left-rail dialog with four sections.

### Appearance

| Setting | Notes |
|---------|-------|
| **Default Theme** | Used when launching without a workspace, and as the starting point for new workspaces |
| **Default Toolbar Position** | Where the project tab toolbar sits: Top, Left, Right or Bottom |

### Integrations

| Setting | Notes |
|---------|-------|
| **Claude Path Override** | Leave blank to use `claude` from your system PATH. Set this if Claude Code is installed somewhere unusual. Agent Dock prefers a `.cmd`/`.bat` wrapper over a bare `.exe`, which is what an npm install produces |

### Updates

| Setting | Notes |
|---------|-------|
| **Release Channel** | **Stable** (default) or **Beta** — see [Update channel](#update-channel) |

### Diagnostics

| Setting | Notes |
|---------|-------|
| **Open Logs Folder** | Opens the log directory in Windows Explorer |

---

## Workspace Settings

**Settings → Workspace Settings…**

These apply to the workspace you currently have open. **File → Save Workspace** keeps them across launches.

### Appearance

| Setting | Notes |
|---------|-------|
| **Theme** | Overrides the app default for this workspace |
| **Toolbar Position** | Top, Left, Right or Bottom |

### Tab Groups

| Setting | Default | Notes |
|---------|---------|-------|
| **Show the Active Projects group** | On | Adds the dynamic group that gathers projects with a live session. Only appears when two or more groups exist — see [Active Projects](groups.md#active-projects) |
| **Limit** | 5 (1–20) | How many projects it lists. Projects beyond this are left out; they stay reachable from their own group |

---

## Project Settings

**Right-click a project tab → Project Settings…**, or the settings button in the File Explorer toolbar.

| Setting | Notes |
|---------|-------|
| **Project Name** | Display name override for tabs, panel titles and the window title. Blank uses the folder name |
| **Icon** | A built-in glyph, or an image file from the project folder. Agent Dock auto-discovers `logo.png` / `icon.png` and similar on first open — see [the icon library](projects.md#the-icon-library) |
| **Icon colour** | Preset swatches for the glyph foreground |
| **Description** | Up to 500 characters, shown in the [Project Description](project-features.md#project-description) panel |
| **Sounds** | Three independent toggles — see below |

Stored in `.agentdock/settings.json` inside the project folder, so it travels with the repository if you commit it.

---

## Sound notifications

Agent Dock plays **Windows system sounds** on session state transitions, so they follow whatever you have configured in Windows and respect your volume mixer.

| Toggle | Fires when | Sound |
|--------|-----------|-------|
| **Session start** | A session begins initializing | Device Connect |
| **Agent waiting for input** | A turn finishes, *or* the agent hits a permission or question prompt | Message Nudge for a finished turn; a distinct notification for a prompt, so a blocked turn is audibly different from a completed one |
| **Session end** | A session exits | Device Disconnect |

All three default to on, and are set **per project**: in Project Settings, the **Sounds** section shows a summary — *All enabled*, *All disabled* or *N of 3 enabled* — so you can tell at a glance without expanding it. **Configure…** opens the three checkboxes inline.

They're stored in the project's `.agentdock/settings.json` as `soundOnSessionStart`, `soundOnAgentWaiting` and `soundOnSessionEnd`.

### Sounds and remote sessions

A machine in [server mode](remote-sessions.md) **goes silent** — nobody is in the room with it. The connected client plays the sounds instead, using the host's per-project toggles. See [Notification sounds](remote-sessions.md#notification-sounds).

---

## Themes

Six built-in themes. Every panel respects the active theme, including rendered markdown, code highlighting and diffs.

| Theme | Variant |
|-------|---------|
| Obsidian | Dark |
| Midnight | Dark |
| Ember | Dark |
| Frost | Light |
| Parchment | Light |
| Sakura | Light |

Set the app-wide default in **App Settings → Appearance**, or override it for the open workspace in **Workspace Settings → Appearance**.

The theme also drives the taskbar icon's accent bar, so you can tell two Agent Dock windows apart on the taskbar.

---

## Update channel

Agent Dock checks GitHub for a newer release at startup.

| Channel | Behaviour |
|---------|-----------|
| **Stable** (default) | Only ever updates to official releases. Pre-releases are skipped |
| **Beta** | Considers pre-releases too, picks the highest version, and moves between successive betas *and* on to the eventual stable release |

Version ordering is proper SemVer: `1.0.0` > `1.0.0-rc.1` > `1.0.0-beta.10` > `1.0.0-beta.2`. A beta tester moving through `beta.1 → beta.2 → rc.1 → 1.0.0` gets each one in order without ever stepping back.

When an update is available, the dialog shows the new version's release notes inline before you commit to it. After updating, a **What's New** popup shows those notes once.

---

## Logs

**App Settings → Diagnostics → Open Logs Folder**, or type `/logs` in the chat.

One log file per app run, named with a timestamp and the workspace or folder it was opened for. Each session's traffic is logged: system init, assistant messages, tool results, control requests and results. Streaming token deltas are deliberately *not* written to disk — they used to grow a single session's log past 100 MB.

The log also carries diagnostics worth knowing about when something feels slow:

- A startup line with the **WPF render tier** (0 = software/no GPU, 1 = partial, 2 = full hardware). Tier 0 — common over Remote Desktop — makes all UI sluggish
- `PERF ui-stall` lines whenever the UI thread is blocked past a threshold, with a snapshot of what was running
- Periodic health samples: working set, GC heap, handle and thread counts, live session counts
- Timed `PERF` lines for slow git calls, markdown builds, tab switches and theme changes

When reporting a bug, attach the relevant log file. Look for `[WARN]` and `[ERROR]` first.
