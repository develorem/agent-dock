# AI Chat

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · **AI Chat** · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

The AI Chat panel is where you talk to the agent. Each project gets its own independent session.

**Contents**

- [Starting a session](#starting-a-session)
- [The panel title](#the-panel-title)
- [Session states](#session-states)
- [Sending messages](#sending-messages)
- [Attaching images](#attaching-images)
- [The send queue](#the-send-queue)
- [Scheduling a message](#scheduling-a-message)
- [Slash commands](#slash-commands)
- [What a turn looks like](#what-a-turn-looks-like)
- [Subagents and background work](#subagents-and-background-work)
- [When a turn seems stuck](#when-a-turn-seems-stuck)
- [Permission prompts](#permission-prompts)
- [Question prompts](#question-prompts)
- [Markdown, links and copying](#markdown-links-and-copying)
- [Ending a session](#ending-a-session)
- [Troubleshooting](#troubleshooting)

---

## Starting a session

An empty panel shows the **agent picker**:

| Option | Behaviour |
|--------|-----------|
| **Claude** | Standard session. The agent asks permission before running commands or editing files |
| **Claude Unrestricted** | `--dangerously-skip-permissions`. No permission prompts — the agent can run any command and edit any file without asking. Use with caution |

If you have more than one [Claude account](accounts.md) configured, a **LOGIN** picker appears above the buttons. Your choice is remembered against the project and is fixed once the session starts.

Dangerous mode is not hidden once you're in it: the session status line carries a warning glyph for as long as permissions are bypassed, and the project tab shows a red badge.

---

## The panel title

The AI Chat panel's own title bar carries the session's live numbers, so nothing competes for space inside the chat:

- the session state (Idle, Working, and so on)
- the **active model**, shortened to family and version — `claude-sonnet-4-5-20250929` shows as `Sonnet 4.5`. Reads `AI Chat` until the session reports one
- which [account](accounts.md) the session signed in as
- **cost** for the session so far
- **token counts** — input, output, cache read, cache creation

The window title bar shows the **total** cost across all open projects, plus elapsed time.

---

## Session states

| State | Meaning |
|-------|---------|
| **Idle** | Ready for your next message |
| **Working** | Processing. The activity bubble animates while it runs |
| **Waiting** | Blocked on a permission or question prompt |
| **Error** | Something went wrong. The reason is shown |
| **Exited** | The session has ended |

"Idle" is not the same as "finished". If subagents or background tasks are still running, the status says so and the tab's status diamond keeps pulsing — see [Subagents and background work](#subagents-and-background-work).

---

## Sending messages

Type in the composer and press **Enter**. **Shift+Enter** inserts a newline.

The box starts about three rows tall and grows as you type, scrolling once it gets large.

**You can keep typing while a turn is running.** Drafting a follow-up mid-turn is fine — pressing Enter adds it to the [send queue](#the-send-queue) rather than dropping it.

### The composer

| Control | What it does |
|---------|--------------|
| **`>` marker** | Part of the input chrome. Click anywhere in the chrome to focus the box |
| **Send** | Sends now, or queues if the agent is busy |
| **Send dropdown (⌄)** | **Send now** · **Schedule for later…** |
| **✕ (floating, top-right of the box)** | Appears when there's something to discard. Clears the draft and any attachments in one click |
| **End Session** | Stops the agent process and returns to the picker |

---

## Attaching images

- **Paste** a screenshot or copied image with **Ctrl+V**
- **Drag and drop** image files onto the input area

Queued images appear as **thumbnail chips** above the box. Click a chip to open it larger in a preview window (where you can **Remove** it), or use the chip's **✕** for a quick remove.

Supported: PNG, JPEG, GIF, BMP, TIFF, WebP. Large images are downscaled to the recommended size and normalized to PNG (or JPEG when smaller) so they stay within the API limit.

Images travel with your message as native multimodal content — no temp files, no extra tool round-trip — and show as thumbnails in your message bubble. A message can be images with no text, or both.

---

## The send queue

Type a follow-up and press **Enter** while the agent is busy, and it joins a **send queue** instead of being dropped. The input clears so you can immediately queue the next one. Each queued message is sent automatically as each turn returns.

A **"Queued to send"** panel appears above the composer listing everything waiting, with a live status for each (**Queued**, or a countdown for scheduled items) and a **✕** to remove any entry.

Messages run in **strict order**, first in first out. A message scheduled for later holds its place in line: everything behind it waits until it fires, then the rest follow. So you can schedule a message for +1h, queue a follow-up to run right after it returns, and build up a to-do list for the session.

The queue is **in-memory** for the session. It's cleared if you end the session, and it isn't saved across app restarts.

---

## Scheduling a message

**Send dropdown → Schedule for later…**

The dialog holds an editable message field and a delay in **hours and minutes** (defaults to 1 hour), and previews exactly when it will send. It works from an empty composer — click it any time, type the message, pick a delay, and schedule.

Scheduled messages go into the same [queue](#the-send-queue) as everything else, so you can keep typing and queueing while one waits.

When the timer elapses the message is sent. If the agent is mid-turn at fire time, the send waits and dispatches the moment the session returns to idle. Scheduled slash commands (e.g. `/compact`) run exactly as if typed by hand.

A session with a parked scheduled message shows a **cyan clock glyph (◷)** in its tab instead of the usual green diamond, and the tab's tooltip says when it's due. More urgent states still win: working, waiting, errored, or an unseen completion shows that instead.

Each project has its own independent schedule. Ending a session clears any pending message for it.

---

## Slash commands

Type `/` in the composer to get an **autocomplete list** of every known command with its description. It filters as you type — `/com` narrows to `/compact`.

| Key | Action |
|-----|--------|
| **↑ / ↓** | Move through the list |
| **Enter** or **Tab** | Insert the highlighted command |
| **Esc** | Dismiss the list |

The list covers the installed Claude Code version's commands plus Agent Dock's own. Primary names only — aliases are mentioned in the description.

Most commands are passed straight to the agent. A few are handled by Agent Dock itself:

| Command | Handled by | What it does |
|---------|-----------|--------------|
| `/clear` | Agent Dock | Clears the chat display |
| `/stop` | Agent Dock | Ends the session |
| `/logs` | Agent Dock | Opens the logs folder |

The **`>` prompt-menu button** at the left of the composer offers the same local actions as a menu: **Clear Chat**, **Compact History**, **End Session**, **Open Logs Folder**.

In a [remote session](remote-sessions.md), `/logs` is unavailable (the folder is on the other machine) and `/clear` clears both sides.

---

## What a turn looks like

A typical interaction is three bubbles: **your message → one activity bubble → the answer.**

### Your message

A distinct right-aligned bubble, with thumbnails for any attached images.

### The activity bubble

One collapsible bubble per turn holds everything the agent did, interleaved in the order it happened:

- **thinking and commentary render grey**
- **tool executions render green**
- **subagent lines and their reports** are set apart with an accent border

While the turn runs, the header animates a whimsical verb with cycling dots (`Pondering…`, `Finagling…`, `Conjuring…`) so it doubles as a live "still working" indicator. When the turn finishes the header reads **`Worked for N.Ns`** and the bubble auto-collapses to just that header — click it to expand and review.

The bubble body has a **bounded height with its own scrollbar**, so expanding it scrolls *within* the bubble instead of shoving the whole chat pane around. The mouse wheel scrolls the inner area while the pointer is over it, and falls through to the page once the inner scroller hits its edge.

**Intermediate commentary folds into the activity bubble.** Text that arrives alongside tool calls ("Let me check that file…") isn't the answer, so it's collapsed. Only executions and the **final answer** — the last assistant text, with no tool calls after it — stay expanded.

Content is **classified before it's placed**, so nothing appears and then jumps somewhere else. The trade-off, by design: the final answer appears complete when the turn finishes rather than typing out token by token. Tool executions still appear as they happen.

### The answer

A standalone, always-shown bubble. Rendered as markdown by default, with a toggle to see the raw source.

### Scrolling

The chat follows new content when you're at the bottom, and stops following the moment you scroll up to read history. Scroll back to the bottom — or send a message — and it follows again.

---

## Subagents and background work

When the agent spawns a **subagent** or a background task, the chat shows it:

- A **◆ Subagent** line appears in the activity bubble, naming the subagent type (e.g. *Explore*) and what it was asked to do
- The status line gains a live running-work suffix next to *Working…*, counting **subagents**, **background tasks** and **workflows separately** (e.g. *"2 subagents · 1 background task running"*)
- When a subagent finishes, its **final report** is captured in the transcript as a distinct **◆ report** block, tagged with the model it ran on — so mixed-model runs are visible at a glance
- A subagent's *internal* tool calls don't leak into the main transcript as if the top-level agent had run them

A session with background work still running does not read as finished: the status and the tab's status diamond both reflect it. A background tab that finishes work while you're elsewhere flashes its diamond green until you look at it.

---

## When a turn seems stuck

After a long stretch with no response, a warning bubble appears: *"Claude hasn't responded for a while — it may be stuck."* It shows the elapsed time since your message was sent, ticking every second, with two buttons:

| Button | What it does |
|--------|--------------|
| **Wait longer** | Dismisses the warning and resets the inactivity timer, so the agent can keep working |
| **Kill process** | Terminates the agent process |

A long research or build task legitimately goes quiet for minutes at a time, so this is a prompt, not a verdict.

---

## Permission prompts

In standard mode, when the agent wants to run a command or edit a file, a prompt **replaces the composer** — no pop-up dialogs. You see:

- the tool name (e.g. Bash, Edit)
- what the agent wants to do
- **Allow** and **Deny**

The status becomes *Waiting*, the tab shows an orange diamond, and — if sounds are on — a notification plays that's deliberately distinct from the "turn finished" chime.

---

## Question prompts

When the agent asks a question (`AskUserQuestion`), the composer is replaced with:

- the question text, which is **selectable and copyable**
- option buttons, if the agent offered choices
- a free-form text box for a custom answer, which accepts **multiple lines**

Your answer is added to the transcript as a user message, so there's a record of what you chose.

---

## Markdown, links and copying

### Rendering

Assistant messages render as markdown — headings, code blocks with syntax highlighting, tables, lists and links — with a toggle for raw source. Rendering happens once, lazily, the first time a message is actually on screen, and is cached.

### File references

File paths in a reply (e.g. `src/AgentDock/Services/ClaudeSession.cs` or `./docs/TASKS.md`) become clickable links. Clicking one **reveals the file in the File Explorer** (expanding parent folders) and shows it in the [File Preview](project-features.md#file-preview).

Only paths that actually resolve to a file under the project root are linked. Bare filenames with no directory component are left as plain text to avoid wrong matches, and paths inside fenced code blocks aren't linkified — they're usually code, not navigation.

### Web links

Markdown links and bare URLs both render as proper links and open in your default browser. `mailto:` links work too. URLs inside fenced code blocks and inline code are left alone, and trailing sentence punctuation is excluded from the link target.

### Copying

- **Select and copy anywhere**, including inside rendered tables — each cell keeps its formatting while staying selectable
- **Copying a selection produces markdown.** Links come out as `[text](url)` rather than losing their target; internal file links, whose URIs mean nothing outside Agent Dock, come out as plain text
- **Rendered tables get a "Copy table" button** in their top-right corner, plus a right-click → **Copy table**. Pasting lands cleanly: Excel and Google Sheets split it back into rows and columns; Word keeps the grid

---

## Ending a session

**End Session** in the composer, `/stop`, or closing the project tab.

Ending a session clears its send queue and any pending scheduled message. Closing Agent Dock stops every running session.

---

## Troubleshooting

### "Agent not found" or the session won't start

The Claude CLI must be available. Open a terminal and check:

```
claude --version
```

If that doesn't print a version, [install Claude Code](https://docs.anthropic.com/en/docs/claude-code) first.

If it's installed somewhere unusual, set **Settings → App Settings… → Integrations → Claude Path Override**. Agent Dock prefers a `.cmd`/`.bat` wrapper over a bare `.exe`, which is what an npm install produces.

Also try **Help → Check Prerequisites**, which reports what it can find: Claude Code, Git, VS Code and others.

### A turn finished instantly with nothing in it

Usually an auth failure. See [Accounts troubleshooting](accounts.md#a-turn-finished-instantly-with-no-answer-and-no-cost).

### The composer is greyed out with a "Server mode" notice

This machine is [hosting its sessions](remote-sessions.md) for another one, so input belongs over there. Click **Stop** on the title-bar badge or the pill beside the Active tab to take it back.

### Session errors or unexpected behaviour

1. **Settings → App Settings… → Diagnostics → Open Logs Folder** (or type `/logs`)
2. Open the most recent log file
3. Look for `[WARN]` and `[ERROR]`

Include the log file when reporting an issue.

### A panel disappeared

Panels can't be closed, only hidden or floated. Drag it back from the edge, or load a saved workspace with the layout you want. Closing and re-adding the project folder restores the default layout.

### Typing feels laggy

Check the startup log line for the **WPF render tier**. Tier 0 means software rendering — common over Remote Desktop — and makes all UI sluggish regardless of what Agent Dock does. See [Logs](settings.md#logs).
