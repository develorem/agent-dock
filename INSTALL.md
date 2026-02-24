# Installing & Using Agent Dock

## Prerequisites

Before installing Agent Dock, make sure you have the following:

### 1. Windows 10 or later

Agent Dock is a Windows desktop application. macOS and Linux are not supported at this time.

### 2. .NET 10 Runtime

Agent Dock is built on .NET 10. Download and install the **desktop runtime** from Microsoft:

**[Download .NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)**

Choose the **Windows x64** installer under ".NET Desktop Runtime". If you're unsure whether you already have it, open a terminal and run:

```
dotnet --list-runtimes
```

Look for a line starting with `Microsoft.WindowsDesktop.App 10.x`.

### 3. Claude Code CLI

Agent Dock launches Claude Code as a background process for each project. The `claude` command must be installed and available in your system PATH.

**[Install Claude Code](https://docs.anthropic.com/en/docs/claude-code)**

To verify it's installed correctly, open a terminal and run:

```
claude --version
```

If this prints a version number, you're ready to go.

---

## Installation

1. Go to the [Releases page](https://github.com/develorem/agent-dock/releases/latest)
2. Download the installer (`.exe`) from the latest release
3. Run the installer and follow the prompts
4. Launch Agent Dock from the Start menu or desktop shortcut

---

## Quick Start

### Adding a Project

Click the **+** button in the toolbar to open a folder picker. Select any project folder — it will appear as a new tab in the toolbar. You can add as many projects as you like.

### Starting a Claude Session

Each project has an **AI panel** on the right side of the workspace. When you open a project for the first time, the panel will show two options:

- **Start Claude** — launches Claude Code in normal mode, where it asks for permission before running commands or editing files
- **Start Claude (Dangerous Mode)** — launches with `--dangerously-skip-permissions`, giving Claude full autonomy. Use with caution.

Once started, type your prompt in the input area at the bottom of the AI panel and press Enter.

### Navigating the Workspace

Each project tab has four panels that you can rearrange freely:

| Panel | Description |
|-------|-------------|
| **File Explorer** | Tree view of your project folder (respects `.gitignore`) |
| **Git Status** | Staged and unstaged changes with color-coded indicators |
| **File Preview** | Syntax-highlighted code, images, markdown, and diffs |
| **AI Chat** | Terminal-style interface for interacting with Claude Code |

**Rearranging panels** — Drag any panel by its title bar to dock it to a different edge, tab it alongside another panel, or float it as a separate window. Each project's layout is independent.

### Understanding Toolbar Icons

The toolbar shows one icon per project. The icon reflects Claude's current state:

| Icon State | Meaning |
|------------|---------|
| Folder icon | No Claude session running |
| Claude icon | Session active, Claude is idle |
| Claude icon + spinner | Claude is working |
| Claude icon + **?** badge | Claude is waiting for your input |
| Claude icon + red badge | Session running in dangerous mode |

### Handling Permission Prompts

When Claude wants to run a command or edit a file (in normal mode), a permission prompt appears inline in the AI panel — right where the input area normally is. You'll see what Claude wants to do and can **Allow** or **Deny** the action. No pop-up dialogs.

### Viewing Files and Diffs

- Click any file in the **File Explorer** to open it in the **File Preview** panel with syntax highlighting
- Click a changed file in **Git Status** to see its diff in the preview panel
- Supported previews: source code (dozens of languages), images (PNG, JPG, GIF, SVG), and markdown

---

## Workspaces

Workspaces let you save and restore your entire session.

### Saving a Workspace

**File > Save Workspace** (Ctrl+S) saves:
- All open project folders
- Panel layouts for each project
- Toolbar position
- Active theme
- Claude session modes

### Loading a Workspace

**File > Open Workspace** to browse for a workspace file, or check **File > Recent Workspaces** for quick access.

### Behavior on Close

When you close Agent Dock:
- All running Claude sessions are stopped
- If you have unsaved workspace changes, you'll be prompted to save

---

## Settings

Access settings from the **Settings** menu:

| Setting | Options | Notes |
|---------|---------|-------|
| **Theme** | 6 built-in themes (light & dark variants) | Applied globally |
| **Toolbar Position** | Top, Left, Right, Bottom | Per-workspace setting |
| **Claude Path Override** | Custom path to `claude` binary | Use if `claude` isn't in your PATH |

---

## Building from Source

If you prefer to build Agent Dock yourself:

```bash
git clone https://github.com/develorem/agent-dock.git
cd agent-dock/src
dotnet build
dotnet run --project AgentDock
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (not just the runtime).

---

## Troubleshooting

### "Claude not found" or session won't start

Make sure the `claude` command is available in your PATH. Open a terminal and run `claude --version`. If it's not found, [install Claude Code](https://docs.anthropic.com/en/docs/claude-code) or set a custom path in **Settings > Claude Path Override**.

### App won't launch

Ensure you have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed. The SDK alone isn't sufficient — you need the desktop runtime.

### Panels disappeared

If a panel goes missing, you can reset the layout by closing the project tab and re-adding the folder. Alternatively, load a saved workspace.

### Git status not updating

Git status refreshes automatically via file watcher. If it seems stuck, click the refresh button in the Git Status panel header. The folder must be a git repository — non-git folders will show "Not a git repository".

---

## Getting Help

- [Open an issue](https://github.com/develorem/agent-dock/issues) on GitHub
- Check [existing issues](https://github.com/develorem/agent-dock/issues?q=is%3Aissue) for known problems and workarounds
