# AI Chat

[Features](features.md) | [Workspace](workspace.md) | [Projects](projects.md) | [Project Features](project-features.md) | **AI Chat**

---

The AI Chat panel is a terminal-style interface for interacting with your AI coding agent. Each project gets its own independent session.

## Starting a Session

When you open a project, the AI Chat panel shows two options:

- **Claude** — launches the agent in normal mode. The agent asks for permission before running commands or editing files.
- **Claude Unrestricted** — launches with full autonomy (`--dangerously-skip-permissions`). The agent can run any command and edit any file without asking. Use with caution.

Once you select a mode, the session starts and the chat input becomes available.

## Status Bar

The status bar at the top of the AI Chat panel shows the session's current state:

| State | Meaning |
|-------|---------|
| **Idle** | Agent is ready for your next message |
| **Working** | Agent is processing (animated spinner) |
| **Waiting** | Agent is waiting for your permission or answer |
| **Error** | Something went wrong |
| **Exited** | Session has ended |

In dangerous mode, the status bar shows "Idle (dangerous mode)" as an additional reminder.

## Sending Messages

Type your prompt in the input area at the bottom and press **Enter** to send. The input uses a terminal aesthetic with a `>` prompt and monospace font.

The input area is only active when the session is idle. While the agent is working, the input is disabled.

## Message Types

### User Messages

Your messages appear in a distinct bubble style so they're easy to distinguish from agent responses.

### Assistant Messages

Agent responses stream in character by character in real-time. Long responses are fully scrollable.

### Thinking Blocks

When the agent shows its reasoning, it appears in a collapsible "Thinking" block. These are expanded while streaming and collapse automatically when the agent moves on.

### Execution Blocks

Tool use and command execution are grouped into collapsible "Execution" blocks. The header shows a summary with tool names and count. Expand to see full execution details.

## Markdown Rendering

Assistant messages can be toggled between raw text and rendered markdown. The rendered view supports headings, code blocks, tables, links, and other GitHub-style markdown formatting.

## Message History

To keep the chat tidy, past messages collapse to show just their first line. Click the expand button on any collapsed message to see the full content. The most recent exchange always stays expanded.

## Permission Prompts

In normal mode, when the agent wants to run a command or edit a file, a permission prompt replaces the input area. You'll see:

- The tool name (e.g., "Bash", "Edit")
- A description of what the agent wants to do
- **Allow** and **Deny** buttons

No pop-up dialogs — everything stays inline in the chat panel.

## Question Prompts

When the agent asks you a question (via `AskUserQuestion`), the input area is replaced with:

- The question text
- Predefined option buttons (if the agent provided choices)
- A free-form text input for custom responses

Select an option or type your answer and click send.

## Session Cost

The cumulative API cost for all active sessions is displayed in the title bar as a USD amount. This updates in real-time as the agent processes messages.

## Stop Button

While the agent is working, a stop button appears in the status bar. Click it to interrupt the current operation.

## Troubleshooting

### "Agent not found" or session won't start

The Claude CLI must be available in your system PATH. Open a terminal and verify:

```
claude --version
```

If this doesn't print a version number, [install Claude Code](https://docs.anthropic.com/en/docs/claude-code) first.

If Claude is installed but not in your PATH, set a custom path via **Settings > Claude Path Override**.

### Check Prerequisites

Use **Help > Check Prerequisites** to verify your system setup. This checks for Claude Code, Git, VS Code, and other tools, and reports what it finds.

### Session errors or unexpected behavior

Check the log files for details:

1. Go to **Settings > Open Logs Folder**
2. Open the most recent log file (named with a timestamp)
3. Look for `[WARN]` or `[ERROR]` entries

When reporting issues, include the relevant log file — it helps with diagnosis.

### Panels disappeared

If a panel goes missing, you can restore the default layout by closing the project tab and re-adding the folder. Alternatively, load a saved workspace that has the layout you want.
