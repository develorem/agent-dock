# Projects

[Features](features.md) | [Workspace](workspace.md) | **Projects** | [Project Features](project-features.md) | [AI Chat](ai-chat.md)

---

Each project in Agent Dock is a folder on your machine. You can have as many projects open at once as you need — each gets its own tab, its own panel layout, and its own AI session.

## Adding a Project

- Click the **+** button in the toolbar, or
- Use **File > Add Project Folder** (Ctrl+N)

A folder picker opens. Select any folder and it appears as a new tab in the toolbar.

## Project Tabs

Projects show as buttons in the toolbar. Click a tab to switch to that project. Only one project is visible at a time, but all sessions continue running in the background.

### Reordering Tabs

Drag a tab to reorder it within the toolbar. A visual indicator shows where the tab will be placed.

### Closing a Project

Right-click a tab or press **Ctrl+W** to close the current project. This also stops the project's AI session if one is running.

## Toolbar Status Icons

Each tab shows an icon that reflects the AI session's current state:

| Icon | Meaning |
|------|---------|
| Folder | No AI session running |
| Agent icon | Session active, agent is idle |
| Agent icon + spinner | Agent is working |
| Agent icon + **?** badge | Agent is waiting for your input |
| Agent icon + red badge | Session running in dangerous mode |

## Project Settings

Right-click a project tab and select **Project Settings**, or click the settings button in the File Explorer toolbar. The settings dialog lets you customize:

### Icon

Choose from built-in icons (folder, code, language logos, etc.) or Agent Dock will discover project-specific images like `logo.png` or `icon.png` in your project folder.

### Color

Pick a foreground color for the tab icon from the preset swatches.

### Description

Add a brief description (up to 500 characters). This appears in the optional Project Description panel within the project's workspace.

Project settings are stored in a `.agentdock/settings.json` file inside the project folder.

## Panel Layout

Each project has four main panels:

| Panel | Purpose |
|-------|---------|
| [**File Explorer**](project-features.md#file-explorer) | Tree view of your project folder |
| [**Git Status**](project-features.md#git-status) | Staged and unstaged changes |
| [**File Preview**](project-features.md#file-preview) | Code, markdown, images, and diffs |
| [**AI Chat**](ai-chat.md) | Terminal-style AI interaction |

### Rearranging Panels

Drag any panel by its title bar to dock it to a different edge, tab it alongside another panel, or float it as a separate window. Each project's layout is fully independent — rearranging one project doesn't affect the others.

### Default Layout

When you add a new project, the default layout is a three-column arrangement:

- **Left** — File Explorer + Git Status (tabbed together)
- **Center** — File Preview
- **Right** — AI Chat

You can customize this freely and save it as part of your workspace.
