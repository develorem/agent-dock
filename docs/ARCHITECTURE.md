# Agent Dock — Architecture Document

## Overview

**Agent Dock** is a Windows desktop application built by **Develorem** that allows developers to manage multiple Claude Code AI sessions across different projects from a single window. Each project gets its own tabbed workspace with a file explorer, git status panel, file preview, and AI interaction panel.

- **GitHub**: https://github.com/develorem/agent-dock
- **License**: Open Source (MIT)
- **Branding**: Develorem (logo at https://github.com/develorem)

## Technology Stack

| Component | Choice | Notes |
|-----------|--------|-------|
| Framework | WPF (.NET 10) | Windows desktop app |
| Docking | AvalonDock (Dirkster fork) | One DockingManager per project tab |
| Syntax Highlighting | AvalonEdit | WPF-native, free |
| Claude Integration | Subprocess + JSON-lines protocol | stdin/stdout communication |
| Git Integration | LibGit2Sharp or git CLI | Read-only status + diffs |
| Theme | Custom light/dark | AvalonDock themes + app-wide resources |

## Core Concepts

### Project Tabs

The main window has a configurable toolbar (top/left/right/bottom) displaying project tabs. Each tab represents an open project folder. The toolbar contains:

- Project tab icons (one per open project) with status badges
- A `+` button to add new project folders

Projects are **tabs**, not docked panels. Only one project is visible at a time (the selected tab). Each project tab contains its own independent docking layout.

### Per-Project Docking

Each project tab contains its own AvalonDock `DockingManager` with four panels:

1. **File Explorer** — Read-only tree view of the project folder
2. **Git Status** — Modified/staged/unstaged files with color coding
3. **File Preview** — Syntax-highlighted code, image preview, markdown rendering, inline diffs
4. **AI Interaction** — Terminal-styled chat panel for Claude Code

Panels can be rearranged, tabbed, and floated **within** their project's docking area. Since each project has its own DockingManager, panels cannot escape to other projects.

**Default layout**: 3 columns — left (File Explorer top, Git Status bottom), center (File Preview), right (AI Interaction).

### Claude Code Integration

Claude Code CLI is spawned as a **subprocess** per project. Communication uses the **JSON-lines protocol** over stdin/stdout (same approach as the VS Code extension).

**Key aspects:**
- Structured messages: conversation content, streaming deltas, control requests
- State tracking: idle, working, waiting-for-input — emitted as events
- Permission prompts handled inline in the chat panel (no modals)
- Supports normal mode and `--dangerously-skip-permissions` mode
- App assumes `claude` is already installed and in PATH

**State machine:**
```
[Not Started] → [Idle] → [Working] ↔ [Waiting for Input]
                  ↑          |
                  └──────────┘
```

### Toolbar Icon States

| State | Visual |
|-------|--------|
| No Claude session | Generic project icon |
| Claude active (idle) | Claude icon |
| Dangerous mode | Claude icon + red skull badge |
| Claude working | Claude icon + activity overlay |
| Waiting for user input | Claude icon + `?` badge |

Icons are sized to accommodate multiple badges. Clicking a tab switches to that project.

### AI Interaction Panel

- **Terminal aesthetic**: monospace font, themed background, custom Agent Dock prompt icon
- **Message rendering**: clear distinction between user and AI messages
- **Streaming**: responses render in real-time as they arrive
- **History**: past messages are collapsed (first line visible + expand button)
- **Permissions**: when Claude requests permission, the input area is replaced with an inline approval prompt (Allow/Deny + context), following VS Code extension patterns
- **Session start**: panel shows options to start Claude in normal or dangerous mode

### File Preview Panel

- Syntax highlighting for common languages (C#, JS/TS, Python, JSON, YAML, XML, HTML, CSS, Markdown, etc.)
- Image preview (PNG, JPG, GIF, SVG)
- Markdown rendering (or syntax-highlighted markdown as fallback)
- Binary files show "No Preview"
- When a git-changed file is clicked from Git Status, shows inline/unified diff

### Git Status Panel

- Shows modified, added, deleted, untracked files
- **Staged files** in one color, **unstaged files** in another
- Auto-refreshes via file watcher or polling
- Clicking a file opens its diff in File Preview
- Non-git folders show "Not a git repository"
- Read-only — no staging/committing from the UI

### Workspace Model

- **No auto-restore**: app opens to a blank slate
- **Explicit save/load**: File > Save Workspace / Open Workspace
- **Workspace file** stores: open projects, docking layouts, toolbar position, theme, Claude session modes
- **Recent Workspaces** listed under File menu
- **On close**: if workspace has unsaved changes, prompt to save. All running Claude instances are killed on exit.

### Theme System

- Global light/dark toggle via Settings menu
- All panels, chrome, and AvalonDock respect the active theme
- Theme preference persists across sessions in app settings
- AI panel maintains terminal aesthetic in both themes

### Settings

- Theme (light/dark)
- Toolbar position (top/left/right/bottom) — per-workspace setting
- Claude binary path override (fallback if not in PATH)
- Default Claude flags (future)

### Menu Structure

```
File
├── Add Project Folder       (Ctrl+N)
├── Open Workspace
├── Save Workspace            (Ctrl+S)
├── Recent Workspaces →
└── Exit

Settings
├── Theme → Light / Dark
├── Toolbar Position → Top / Left / Right / Bottom
└── Claude Path Override

Help
├── Getting Started
└── About
```

## Future Considerations (Not in v1)

- Tear-off project tabs as floating windows (requires nested DockingManager or alternative framework)
- Multiple AI providers beyond Claude Code
- Git staging/committing from UI
- System tray notifications for background Claude activity
- Multiple Claude sessions per project
- Cross-platform support (Avalonia migration)
