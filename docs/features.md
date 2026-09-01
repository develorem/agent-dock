# Features

**Features** · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

Agent Dock gives you a single window to manage AI coding sessions across all your projects. Here's the whole map.

---

## Multi-project workspaces

Open as many project folders as you need, each in its own tab with its own panel layout and its own agent session. Save the lot — projects, layouts, groups, theme, toolbar position — and restore it later.

[Workspace →](workspace.md)

## Tab groups & Active Projects

With twenty projects open, one row of tabs isn't enough. Group your tabs, give the groups icons and colours, and let each group tab roll up the status of everything inside it. A dynamic **Active** tab always shows the projects with a live session, most recent first.

[Tab Groups →](groups.md)

## Project tabs & settings

Every tab shows what its session is doing — working, waiting on you, errored, or holding a scheduled message. Name projects, give them icons from a 197-glyph searchable library, add a description, and set per-project notification sounds.

[Projects →](projects.md)

## AI Chat

Stream responses, one collapsible activity bubble per turn, inline permission and question prompts, image attachments, a send queue, scheduled messages, slash-command autocomplete, clickable file and web links, and live subagent tracking.

[AI Chat →](ai-chat.md)

## Panels

Six panels per project, all dockable, floatable and independently arranged:

| Panel | What it does |
|-------|--------------|
| **File Explorer** | `.gitignore`-aware tree that patches in place instead of flickering |
| **Git Status** | Branch header with switcher and open-in-browser, staged/unstaged changes, click for a diff |
| **File Preview** | Syntax-highlighted code, rendered markdown, images, inline diffs |
| **Project Description** | What this folder is for |
| **Todo List** | A per-project checklist |
| **AI Chat** | The agent |

[Panels →](project-features.md)

## Accounts, usage & cost

Hold more than one Claude login, pick which one a session signs in as, and see each plan's 5-hour and 7-day quotas separately in the title bar. Live cost and token counts per session and across the workspace.

[Accounts →](accounts.md)

## Remote sessions

Host this machine's live sessions for a second copy of Agent Dock on another machine, and drive them from there — chat, files, git and diffs all come from the host. TLS with a pinned certificate, an eight-character pairing code, one driver at a time.

[Remote Sessions →](remote-sessions.md)

## Themes

Six built-in themes — three dark (Obsidian, Midnight, Ember) and three light (Frost, Parchment, Sakura). Every panel respects the active theme, including rendered markdown, code highlighting and diffs. Each theme gets its own taskbar accent bar, so you can tell two windows apart.

[Settings →](settings.md#themes)

## Auto-update

Agent Dock checks GitHub at startup and offers the update with its release notes inline. Two channels: **Stable**, or **Beta** for pre-release builds. The running version is shown in the title bar next to the product name, so you never have to open a dialog to find out what you're on.

[Settings →](settings.md#update-channel)

---

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| **Ctrl+N** | Add a project folder |
| **Ctrl+S** | Save workspace |
| **Ctrl+W** | Close the current project |
| **Enter** | Send the message (or queue it, if the agent is busy) |
| **Shift+Enter** | Newline in the composer |
| **Ctrl+V** | Paste an image into the composer |
| **Alt+F4** | Exit |

---

## Help inside the app

| Menu item | What it does |
|-----------|--------------|
| **Help → Getting Started** | A short in-app tour |
| **Help → Check Prerequisites** | Reports what it can find: Claude Code, Git, VS Code and others |
| **Help → About Agent Dock** | Version, links, and the release notes for this build |
