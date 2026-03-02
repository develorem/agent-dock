# Workspace

[Features](features.md) | **Workspace** | [Projects](projects.md) | [Project Features](project-features.md) | [AI Chat](ai-chat.md)

---

A workspace is a snapshot of your entire Agent Dock session. It captures which projects are open, how the panels are arranged, your theme, and toolbar position — so you can pick up exactly where you left off.

## Saving a Workspace

**File > Save Workspace** (or **Ctrl+S**) saves everything to a `.agentdock` file:

- All open project folders
- Panel layouts for each project
- Toolbar position (top, left, right, bottom)
- Active theme
- AI session modes (normal or dangerous)

You choose where to save the file. It's a plain JSON file you can store alongside your projects or anywhere convenient.

## Loading a Workspace

- **File > Open Workspace** — browse for a `.agentdock` file
- **File > Recent Workspaces** — quick access to workspaces you've opened recently

When you load a workspace, Agent Dock closes any currently open projects and restores the saved session. If you have unsaved changes, you'll be prompted to save first.

## Close Behavior

When you close Agent Dock:

- All running AI sessions are stopped
- If your workspace has unsaved changes, you'll be prompted to save

## Settings

Access settings from the **Settings** menu in the title bar.

### Themes

Six built-in themes are available:

| Theme | Variant |
|-------|---------|
| Obsidian | Dark |
| Midnight | Dark |
| Ember | Dark |
| Frost | Light |
| Parchment | Light |
| Sakura | Light |

The selected theme applies globally and persists across sessions.

### Toolbar Position

Move the project toolbar to any edge of the window:

- **Top** (default)
- **Left**
- **Right**
- **Bottom**

This is a per-workspace setting — different workspaces can use different positions.

### Claude Path Override

By default, Agent Dock looks for the `claude` command in your system PATH. If your Claude CLI is installed elsewhere, use **Settings > Claude Path Override** to browse for the executable. You can also reset to the default.

### Open Logs Folder

Opens the logs directory in Windows Explorer. Useful when troubleshooting issues — you can find the log files here and share them when reporting bugs.
