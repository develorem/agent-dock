# Projects

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · **Projects** · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

Each project in Agent Dock is a folder on your machine. You can have as many open at once as you need — each gets its own tab, its own panel layout and its own agent session.

**Contents**

- [Adding a project](#adding-a-project)
- [Project tabs](#project-tabs)
- [Tab status indicators](#tab-status-indicators)
- [The tab right-click menu](#the-tab-right-click-menu)
- [Project settings](#project-settings)
- [The icon library](#the-icon-library)
- [Panel layout](#panel-layout)

---

## Adding a project

- Click **+** in the project toolbar, or
- **File → Add Project Folder** (**Ctrl+N**)

The **+** button is position-aware: it sits at the end of the tab row wherever you've docked the toolbar.

If [groups](groups.md) are in use, the new project joins whichever group is currently showing.

The empty-state screen also lists your **recent workspaces**, so a fresh launch is one click from where you were.

---

## Project tabs

Click a tab to switch to that project. Only one project is visible at a time, but every session keeps running in the background.

| Action | How |
|--------|-----|
| **Switch** | Click the tab |
| **Reorder** | Drag it. An insertion line shows where it will land |
| **Move into a group** | Drag it onto a [group tab](groups.md) |
| **Close** | Right-click → **Close Project**, or **Ctrl+W** for the current one |

Closing a project stops its agent session.

The active tab reads as joined to the workspace below it, via an accent rail that frames the content it owns — see [how tabs frame their work](groups.md#how-tabs-frame-their-work).

---

## Tab status indicators

Every tab carries a status diamond showing what its session is doing:

| Indicator | State |
|-----------|-------|
| Hollow purple ◇ | No session, or inactive |
| Solid green ◆ | Ready — the agent is waiting for you |
| Flashing green ◆ | A response you haven't looked at yet |
| Flashing blue ◆ | Working |
| Orange ◆ | A question or permission prompt is waiting |
| Red ◆ with `!` | Error |
| Cyan ◷ | A [scheduled message](ai-chat.md#scheduling-a-message) is parked and nothing more urgent is happening |
| Red badge | The session is running in **dangerous mode** |

A tab that finishes work while you're looking elsewhere flashes green until you visit it.

A session with subagents or background tasks still running keeps pulsing rather than showing a solid "ready" diamond — "idle, but three subagents are still going" is visibly different from "done". See [Subagents and background work](ai-chat.md#subagents-and-background-work).

Hovering a tab shows the folder path, plus the due time of any scheduled message.

[Group tabs roll these up](groups.md#status-roll-up), so a collapsed group whose projects need attention lights up too.

---

## The tab right-click menu

| Item | Notes |
|------|-------|
| **Project Settings…** | See below |
| **Open in VS Code** | Only shown when VS Code is detected |
| **Open in Cursor** | Only shown when Cursor is detected |
| **Open in Explorer** | Windows Explorer at the project root |
| **Open in Console** | A command prompt at the project root |
| **Add to new group** | See [Tab Groups](groups.md#creating-a-group) |
| **Close Project** | Stops the session too |

The same actions are available from the [File Explorer toolbar](project-features.md#the-explorer-toolbar).

---

## Project settings

**Right-click a tab → Project Settings…**, or the settings button in the File Explorer toolbar.

| Setting | Notes |
|---------|-------|
| **Project Name** | Overrides the folder name in tabs, panel titles and the window title (up to 120 characters) |
| **Icon** | A built-in glyph or an image from the project folder |
| **Icon colour** | Preset swatches for the glyph foreground |
| **Description** | Up to 500 characters, shown in the [Project Description](project-features.md#project-description) panel |
| **Sounds** | Session start, agent waiting for input, session end — see [Sound notifications](settings.md#sound-notifications) |

Everything is stored in `.agentdock/settings.json` inside the project folder, so it travels with the repository if you commit it.

Full reference: [Settings](settings.md#project-settings).

---

## The icon library

Agent Dock ships **197 built-in glyphs** from the Segoe MDL2 Assets font, organised into categories: files & editing, dev & hardware, charts & data, status & symbols, security, navigation & arrows, communication & people, media & scanning, places & travel, time, shopping, devices, lifestyle & nature.

The picker has a **search box** that focuses automatically. Each icon carries keywords, so searching by what an icon is *for* works — `audio` finds the speaker, headphone, music and microphone glyphs; `chart`, `cloud` and `lock` all behave the same way.

Because the glyphs are vector, they stay crisp at any DPI and pick up the per-icon colour swatch.

Instead of a glyph, Agent Dock can use an **image from the project folder**. On first open it looks for `logo.png`, `icon.png`, `<foldername>.png` and `.ico` equivalents, in the project root and in `assets/`, and persists whatever it finds.

[Groups](groups.md#group-settings) use the same picker, minus the image-file option — groups have no folder.

---

## Panel layout

Each project has six panels:

| Panel | Purpose | Default position |
|-------|---------|------------------|
| [**File Explorer**](project-features.md#file-explorer) | Tree view of the project folder | Left, tabbed with Git Status |
| [**Git Status**](project-features.md#git-status) | Staged and unstaged changes | Left, tabbed with File Explorer |
| [**File Preview**](project-features.md#file-preview) | Code, markdown, images and diffs | Centre |
| [**AI Chat**](ai-chat.md) | Agent interaction | Right |
| [**Project Description**](project-features.md#project-description) | The project's description text | Auto-hidden tab, right edge |
| [**Todo List**](project-features.md#todo-list) | A per-project checklist | Auto-hidden tab, right edge |

### Rearranging

Drag any panel by its title bar to dock it to a different edge, tab it alongside another panel, or float it as a separate window. Click an auto-hidden tab on the right edge to slide its panel out.

**Each project's layout is fully independent** — rearranging one doesn't affect the others. Layouts are saved with the [workspace](workspace.md).

Panels can't be closed outright, only hidden, auto-hidden or floated, so a layout can't lose a panel permanently. If one goes missing, drag it back from the edge or reload a saved workspace.

### Suspended background tabs

Only the tab you're looking at does git work. A project's file watcher and git refreshes are **suspended while its tab is in the background** and resume when you switch to it. The tab's status diamond still updates. With ten projects open and agents writing files, this removes a large, constant source of UI contention.
