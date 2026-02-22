# Agent Dock

**Manage multiple Claude Code AI sessions across projects from a single window.**

Agent Dock is a Windows desktop application that lets developers run and monitor multiple [Claude Code](https://code.claude.com/) instances — each tied to a different project — all within one unified interface. Each project gets its own tabbed workspace with a file explorer, git status panel, file preview, and an AI interaction terminal.

Built by [Develorem](https://github.com/develorem).

## Features

- **Multi-project tabs** — Open multiple project folders, each in its own tab with independent layout
- **Per-project AI sessions** — Start and interact with Claude Code in each project independently
- **File explorer** — Read-only tree view of your project files (respects .gitignore)
- **Git status** — See staged and unstaged changes at a glance with color-coded indicators
- **File preview** — Syntax-highlighted code, image preview, markdown rendering, and inline diffs
- **Dockable panels** — Rearrange, tab, and float panels within each project workspace
- **Status indicators** — Toolbar icons show Claude's state across all projects (working, waiting, idle)
- **Light & dark themes** — Full theme support across the entire application
- **Workspace save/load** — Save your project layout and restore it later

## Prerequisites

- Windows 10 or later
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Claude Code CLI](https://code.claude.com/docs/en/overview) installed and available in your system PATH

## Getting Started

1. Download the latest release (or build from source)
2. Launch Agent Dock
3. Click the **+** button in the toolbar to add a project folder
4. In the AI panel, start a Claude Code session
5. Add more project tabs as needed — monitor all of them from the toolbar

## Building from Source

```bash
git clone https://github.com/develorem/agent-dock.git
cd agent-dock/src
dotnet build
dotnet run --project AgentDock
```

## Tech Stack

- **WPF** on **.NET 10** (Windows)
- **AvalonDock** for dockable panel layouts
- **AvalonEdit** for syntax-highlighted file preview
- Claude Code CLI integration via subprocess + JSON protocol

## License

MIT

## Contributing

Contributions are welcome. Please open an issue first to discuss what you'd like to change.
