# Tab Groups

[Features](features.md) · [Workspace](workspace.md) · **Groups** · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

With a handful of projects open, one row of tabs is fine. With twenty, it isn't. **Tab groups** add a second strip above the project tabs: click a group and the project row filters down to that group's tabs.

The group strip is invisible until you create your first group, so the default look is unchanged.

**Contents**

- [Creating a group](#creating-a-group)
- [Working with groups](#working-with-groups)
- [Group settings](#group-settings)
- [Status roll-up](#status-roll-up)
- [Active Projects](#active-projects)
- [How tabs frame their work](#how-tabs-frame-their-work)

---

## Creating a group

**Right-click a project tab → Add to new group.**

The first time you do this the workspace splits into two groups:

- a new auto-named **Group 1** containing the tab you right-clicked
- an **Ungrouped** group containing every other tab

Subsequent uses create *Group 2*, *Group 3*, and so on.

You can also click the **+** at the end of the group strip to create a new empty group and switch to it.

---

## Working with groups

| Action | How |
|--------|-----|
| **Switch group** | Click its tab. The project row filters to that group |
| **Rename** | Click the *already active* group tab to open an inline editor. **Enter** or click away commits, **Esc** cancels |
| **Reorder** | Drag a group tab left or right. An insertion line shows where it will land |
| **Move a project into a group** | Drag the project tab onto the group tab. The drop target highlights while you hover it |
| **Delete** | Right-click → **Delete group**. Only enabled when the group is empty |

Deleting the second-to-last group dissolves grouping completely and puts the workspace back into its no-group-strip state.

**Switching to a group opens the project that needs you** rather than just the first one, using the same priority as the [status roll-up](#status-roll-up). The project that lit the group up is the one you land on.

### Group right-click menu

**Group Settings…** · **Rename** · **New group** · **Delete group**

---

## Group settings

**Right-click a group → Group Settings…**

| Setting | Notes |
|---------|-------|
| **Name** | Free text |
| **Icon** | Any of the built-in glyphs. Groups have no folder, so image files aren't offered — see [the icon library](projects.md#the-icon-library) |
| **Icon colour** | Preset swatches |

Groups, their names, icons, order, the active group, and each project's group assignment are all saved with the [workspace](workspace.md).

---

## Status roll-up

Every group tab carries its own status diamond that rolls up the highest-priority state among its projects, so a collapsed group whose projects need attention lights up the same way a project tab would.

Priority, highest first:

| Indicator | State |
|-----------|-------|
| Red ◆ with `!` | Error |
| Orange ◆ | Question or permission prompt waiting |
| Flashing green ◆ | New response you haven't looked at |
| Solid green ◆ | Ready / waiting for you |
| Flashing blue ◆ | Working |
| Cyan ◷ | A [scheduled message](ai-chat.md#scheduling-a-message) is parked, and nothing more urgent is happening |
| Hollow purple ◇ | Inactive |

---

## Active Projects

A dynamic **Active** tab sits on the right of the group strip, set apart from your real groups. It gathers every project with a **live agent session**, most recently active first — so the work that is actually running is always one click away no matter which group it lives in.

- It is a **view, not a group**. Projects stay in the group you filed them under
- It re-prunes itself as sessions come and go. Clicking it again while it is already open drops any project that has since gone quiet
- It is greyed out when nothing is running

**Workspace Settings → Appearance → Tab Groups** controls it:

| Setting | Default | Notes |
|---------|---------|-------|
| **Show the Active Projects group** | On | Only appears when two or more groups exist |
| **Limit** | 5 (range 1–20) | Projects past the limit are left out; they stay reachable from their own group |

Existing workspace files get the group switched on with the default limit.

### Active Projects and server mode

[Server mode](remote-sessions.md) publishes exactly the projects with a live session — the same set this tab shows. So while a machine is hosting, Agent Dock switches to this view and shows the tab regardless of the workspace setting, and puts the **Stop server** pill beside it.

---

## How tabs frame their work

The group strip and the project strip each carry an **accent rail along the edge facing the content**, which continues down the sides and across the bottom to box in the area that tab owns. The active tab leaves a gap in its own rail, so it reads as joined to the workspace below it rather than floating above it.

The two rails use **deliberately different hues** — one for the group, one for the project — so with grouping in use you can see at a glance which project sits inside which group, instead of two nested frames in the same colour.

The framing follows the project toolbar wherever you dock it: top, bottom, left or right.
