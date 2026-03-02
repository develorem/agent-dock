# Installing Agent Dock

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

### 3. A Supported AI Agent

Agent Dock launches AI coding agents as background processes for each project. Currently supported:

- **[Claude Code](https://docs.anthropic.com/en/docs/claude-code)** — the `claude` command must be installed and available in your system PATH

Support for additional agents and models is coming soon.

To verify Claude Code is installed correctly, open a terminal and run:

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

1. Click **+** in the toolbar to add a project folder
2. In the AI Chat panel, click **Claude** or **Claude Unrestricted** to start a session
3. Type your prompt and press **Enter**

For detailed usage, see the documentation:

- **[Features overview](docs/features.md)** — what Agent Dock can do
- **[Workspace](docs/workspace.md)** — saving, loading, themes, and settings
- **[Projects](docs/projects.md)** — tabs, project settings, and panel layouts
- **[Project features](docs/project-features.md)** — File Explorer, Git Status, and File Preview
- **[AI Chat](docs/ai-chat.md)** — sessions, message types, and troubleshooting

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

## Getting Help

- [Open an issue](https://github.com/develorem/agent-dock/issues) on GitHub
- Check [existing issues](https://github.com/develorem/agent-dock/issues?q=is%3Aissue) for known problems and workarounds
- See the [troubleshooting section](docs/ai-chat.md#troubleshooting) for common issues
