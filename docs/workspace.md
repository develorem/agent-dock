# Workspace

[Features](features.md) · **Workspace** · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

A workspace is a snapshot of your entire Agent Dock session: which projects are open, how their panels are arranged, how they're grouped, your theme and your toolbar position. Save it and pick up exactly where you left off.

**Contents**

- [Saving a workspace](#saving-a-workspace)
- [What a workspace stores](#what-a-workspace-stores)
- [Loading a workspace](#loading-a-workspace)
- [Workspace vs app settings](#workspace-vs-app-settings)
- [Closing Agent Dock](#closing-agent-dock)

---

## Saving a workspace

**File → Save Workspace** (**Ctrl+S**) writes a `.agentdock` file wherever you choose. It's plain JSON — keep it alongside your projects or anywhere convenient.

---

## What a workspace stores

- Every open project folder, and each project's **panel layout**
- Which project was active
- [Tab groups](groups.md): names, icons, colours, order, the active group, and each project's group assignment
- The [Active Projects](groups.md#active-projects) setting and its limit
- Theme
- Toolbar position

Note what a workspace does **not** store: agent conversation history, the send queue and scheduled messages all live with the session, not the file.

### File format versions

The format is at **v2**. Older v1 files still load — they come up with no groups defined, exactly as before, and pick up the Active Projects group with its default limit.

---

## Loading a workspace

- **File → Open Workspace** — browse for a `.agentdock` file
- **File → Recent Workspaces** — workspaces you've opened recently
- The **empty-state screen** also lists recent workspaces, so a fresh launch is one click from where you were

Loading closes any currently open projects first. If the current workspace has unsaved changes, you're prompted to save.

### Opening progress

A workspace with a lot of projects used to look like a hung window. Opening now shows a **modal overlay with a spinner, the project being opened, a counter and a progress bar**, and it swallows clicks while the restore runs so a stray double-click can't land somewhere unintended. The restore yields between projects rather than blocking, which is what lets the spinner actually spin.

A single-project workspace gets the spinner without the counter and bar — there's nothing to count.

---

## Workspace vs app settings

Agent Dock separates *defaults* from *this workspace*:

| | App Settings | Workspace Settings |
|---|---|---|
| Where | **Settings → App Settings…** | **Settings → Workspace Settings…** |
| Stored in | `%LOCALAPPDATA%\AgentDock\settings.json` | The `.agentdock` file |
| Theme | The default, used with no workspace open | Overrides it for this workspace |
| Toolbar position | The default | Overrides it |
| Also holds | Claude path override, release channel, logs | Active Projects group and limit |

Changing a workspace setting marks the workspace dirty. **File → Save Workspace** keeps it.

Full reference: [Settings](settings.md).

---

## Closing Agent Dock

- Every running agent session is stopped
- If the workspace has unsaved changes, you're prompted to save
- Send queues and scheduled messages are not preserved

If Agent Dock is [hosting for another machine](remote-sessions.md), closing it stops hosting too.
