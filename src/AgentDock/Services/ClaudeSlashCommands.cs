namespace AgentDock.Services;

public record ClaudeSlashCommand(string Command, string Description);

/// <summary>
/// Static list of Claude Code slash commands plus Agent Dock local commands.
/// Sourced from https://code.claude.com/docs/en/commands (Claude Code 2.1.x).
/// Primary names only — aliases are mentioned in the description.
/// </summary>
public static class ClaudeSlashCommands
{
    public static readonly IReadOnlyList<ClaudeSlashCommand> All =
    [
        new("/add-dir", "Add a working directory for file access during this session"),
        new("/agents", "Manage agent configurations"),
        new("/autofix-pr", "Watch the current PR and push fixes on CI or review failures"),
        new("/batch", "Orchestrate large-scale parallel changes across the codebase"),
        new("/branch", "Branch the conversation at this point (alias: /fork)"),
        new("/btw", "Ask a quick side question without adding to the conversation"),
        new("/chrome", "Configure Claude in Chrome settings"),
        new("/claude-api", "Load Claude API reference material for your project's language"),
        new("/clear", "Clear the chat display (Agent Dock local)"),
        new("/color", "Set the prompt bar color for this session"),
        new("/compact", "Summarize the conversation so far to free up context"),
        new("/config", "Open the Settings interface (alias: /settings)"),
        new("/context", "Visualize current context usage"),
        new("/copy", "Copy the last assistant response to the clipboard"),
        new("/cost", "Show token usage statistics"),
        new("/debug", "Enable debug logging and troubleshoot issues"),
        new("/desktop", "Continue this session in the Claude Code Desktop app (alias: /app)"),
        new("/diff", "Open an interactive diff viewer"),
        new("/doctor", "Diagnose and verify your Claude Code installation"),
        new("/effort", "Set the model effort level (low, medium, high, xhigh, max)"),
        new("/exit", "Exit the CLI (alias: /quit)"),
        new("/export", "Export the current conversation as plain text"),
        new("/extra-usage", "Configure extra usage to keep working past rate limits"),
        new("/fast", "Toggle fast mode on or off"),
        new("/feedback", "Submit feedback about Claude Code (alias: /bug)"),
        new("/fewer-permission-prompts", "Add an allowlist to reduce permission prompts"),
        new("/focus", "Toggle the focus view"),
        new("/heapdump", "Write a JS heap snapshot for memory diagnostics"),
        new("/help", "Show help and available commands"),
        new("/hooks", "View hook configurations for tool events"),
        new("/ide", "Manage IDE integrations and show status"),
        new("/init", "Initialize the project with a CLAUDE.md guide"),
        new("/insights", "Generate a report analyzing your sessions"),
        new("/install-github-app", "Set up the Claude GitHub Actions app"),
        new("/install-slack-app", "Install the Claude Slack app"),
        new("/keybindings", "Open or create your keybindings configuration"),
        new("/login", "Sign in to your Anthropic account"),
        new("/logout", "Sign out from your Anthropic account"),
        new("/logs", "Open the Agent Dock logs folder (Agent Dock local)"),
        new("/loop", "Run a prompt repeatedly while the session is open (alias: /proactive)"),
        new("/mcp", "Manage MCP server connections and OAuth authentication"),
        new("/memory", "Edit CLAUDE.md memory files and view auto-memory"),
        new("/mobile", "Show QR code to download the mobile app (aliases: /ios, /android)"),
        new("/model", "Select or change the AI model"),
        new("/passes", "Share a free week of Claude Code with friends"),
        new("/permissions", "Manage tool permission rules (alias: /allowed-tools)"),
        new("/plan", "Enter plan mode directly from the prompt"),
        new("/plugin", "Manage Claude Code plugins"),
        new("/powerup", "Discover features through interactive lessons"),
        new("/privacy-settings", "View and update your privacy settings"),
        new("/recap", "Generate a one-line summary of the current session"),
        new("/release-notes", "View the changelog"),
        new("/reload-plugins", "Reload all active plugins"),
        new("/remote-control", "Make this session available for remote control (alias: /rc)"),
        new("/remote-env", "Configure the default remote environment for web sessions"),
        new("/rename", "Rename the current session"),
        new("/resume", "Resume a conversation by ID or name (alias: /continue)"),
        new("/review", "Review a pull request locally in your session"),
        new("/rewind", "Rewind conversation or code to a previous point (aliases: /checkpoint, /undo)"),
        new("/sandbox", "Toggle sandbox mode"),
        new("/schedule", "Create, update, list, or run routines (alias: /routines)"),
        new("/security-review", "Analyze pending changes for security vulnerabilities"),
        new("/setup-bedrock", "Configure Amazon Bedrock authentication"),
        new("/setup-vertex", "Configure Google Vertex AI authentication"),
        new("/simplify", "Review changed files for reuse, quality, and efficiency"),
        new("/skills", "List available skills"),
        new("/stats", "Visualize daily usage, session history, and streaks"),
        new("/status", "Open the Settings interface (Status tab)"),
        new("/statusline", "Configure Claude Code's status line"),
        new("/stickers", "Order Claude Code stickers"),
        new("/stop", "Stop the Claude Code session (Agent Dock local)"),
        new("/tasks", "List and manage background tasks (alias: /bashes)"),
        new("/team-onboarding", "Generate a team onboarding guide"),
        new("/teleport", "Pull a web session into this terminal (alias: /tp)"),
        new("/terminal-setup", "Configure terminal keybindings"),
        new("/theme", "Change the color theme"),
        new("/tui", "Set the terminal UI renderer (default or fullscreen)"),
        new("/ultraplan", "Draft a plan in an ultraplan session"),
        new("/ultrareview", "Run a deep, multi-agent code review"),
        new("/upgrade", "Open the upgrade page to switch to a higher plan"),
        new("/usage", "Show plan usage limits and rate limit status"),
        new("/voice", "Toggle push-to-talk voice dictation"),
        new("/web-setup", "Connect your GitHub account to Claude Code on the web"),
    ];

    public static IReadOnlyList<ClaudeSlashCommand> Filter(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == "/")
            return All;

        return All
            .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
