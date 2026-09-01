# Panels

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · **Panels** · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

Alongside the [AI Chat](ai-chat.md), each project has five panels for navigating the codebase, reviewing changes and keeping notes.

**Contents**

- [File Explorer](#file-explorer)
- [File Preview](#file-preview)
- [Git Status](#git-status)
- [Project Description](#project-description)
- [Todo List](#todo-list)

---

## File Explorer

A tree view of the project folder.

### Browsing

Expand folders to navigate. Click any file to open it in the [File Preview](#file-preview) with syntax highlighting. Folder icons are tinted so they read apart from files at a glance.

### .gitignore filtering

The tree respects the project's `.gitignore`. Hidden and ignored files are filtered out.

### Auto-refresh, without the flicker

A file watcher monitors the folder. When files are added, removed or renamed, the tree updates automatically; if the watcher hits trouble, Agent Dock falls back to periodic polling.

The tree is **patched in place** rather than cleared and rebuilt, so **expanded folders, selection and scroll position all survive a refresh**. This matters because the refresh is driven by the same watcher that drives git status — and an agent editing files fires it constantly.

### The explorer toolbar

| Button | What it does |
|--------|--------------|
| **Open in VS Code** | Opens the project folder in VS Code. Hidden if VS Code isn't detected |
| **Open in Explorer** | Windows Explorer at the project root |
| **Open in Console** | A command prompt at the project root |
| **Project Settings** | Opens the [project settings dialog](projects.md#project-settings) |
| **Refresh** | Forces a re-scan |

The same actions are on the [project tab right-click menu](projects.md#the-tab-right-click-menu), which also offers Cursor when it's detected.

### Reveal from elsewhere

Clicking a [file reference in a chat reply](ai-chat.md#file-references), or the reveal button on a diff, expands the tree to that file, selects it, and shows it in the preview.

---

## File Preview

Shows file contents formatted for the file type.

### Code

Syntax highlighting via AvalonEdit, with line numbers in the gutter. Supported languages include C#, Python, JavaScript, TypeScript, JSON, YAML, XML, HTML, CSS, Markdown, Rust, Go, Ruby, Java, C, C++, PowerShell, Bash and SQL.

**JSON** gets its own theme-aware colouring: object keys, string values, numbers, booleans, `null` and punctuation each get their own brush, and it follows theme changes live without reloading the file.

### Markdown

`.md` files open **rendered** by default — proper heading sizes, formatting, code blocks, tables and clickable links. A toggle in the top-right switches between:

- **Rendered view** — GitHub-like styling
- **Source view** — syntax-highlighted raw markdown

### Images

PNG, JPG, GIF and SVG are displayed centred with uniform scaling.

### Diffs

Clicking a changed file in [Git Status](#git-status) shows a unified diff:

| Colour | Meaning |
|--------|---------|
| Green | Added lines |
| Red | Removed lines |
| Purple | Hunk headers |

For untracked files the whole file is shown as an addition.

### The preview toolbar

| Button | What it does |
|--------|--------------|
| **Show full file** | While viewing a diff, jumps to the full file: expands the File Explorer to it, selects it, and switches the preview from the diff to the contents |
| **Source / rendered toggle** | Markdown only |
| **Close preview** | Clears the pane back to its empty state |

### Unsupported files

Binary and unrecognised formats show a "No Preview" message.

---

## Git Status

The project's current git state — the active branch and any changed files.

### Branch header

The current branch name, with three buttons:

| Button | What it does |
|--------|--------------|
| **Copy branch name** | To the clipboard |
| **Switch branch** ("…") | Opens the branch picker |
| **Open repository in browser** | Opens the repo's web page in your default browser |

The browser button appears only when `origin` points at a recognised web host: **GitHub, GitLab, Bitbucket, Codeberg** and **Azure DevOps**, over both `https` and SSH remotes (`git@…`, `ssh://…`), including self-hosted GitHub Enterprise and GitLab. Embedded credentials and a trailing `.git` are stripped from the opened URL. With no `origin`, or a plain SSH/file git server that isn't a web host, the button stays hidden — you only see it when there's actually a page to open.

### Branch picker

| Action | How |
|--------|-----|
| **Filter** | Type in the box to filter local branches |
| **Switch** | Double-click a branch, or select it and press **Enter** |
| **Create** | If your typed name matches no existing branch, a **+** appears to create and switch to it |

The current branch is marked with a checkmark. **Down arrow** moves into the list, **Enter** selects or creates, **Esc** closes.

### File status list

Changed files, staged first:

| Label | Meaning |
|-------|---------|
| **M** | Modified |
| **A** | Added |
| **D** | Deleted |
| **R** | Renamed |
| **?** | Untracked |

Staged and unstaged files use different colours, and staged entries carry a "(staged)" label. Click any file to see its diff in the [File Preview](#file-preview).

### Auto-refresh

Git status refreshes when files change, using the same watcher as the File Explorer, debounced so a bulk operation doesn't cause a storm.

The git commands themselves run **off the UI thread**, and the panel is updated as a minimal delta — only changed rows are touched, so selection survives, and nothing is touched at all when nothing changed. A slow git call can't freeze the window.

The watcher's buffer is sized for busy repositories, so a large `node_modules` install or bulk file generation no longer overflows it and drops the panel into polling for the rest of the session.

Refreshes are **suspended for background tabs** — see [suspended background tabs](projects.md#suspended-background-tabs).

### Empty states

- **"Not a git repository"** — the folder isn't a git repo
- **"No changes"** — everything is committed

---

## Project Description

A short description of the project, shown in a panel so you don't have to remember what a folder was for.

By default it's an **auto-hidden tab on the right edge** of the project layout — click the tab to slide it out, or drag it into the layout to dock it permanently.

| Control | What it does |
|---------|--------------|
| **Edit in Project Settings** | Jumps to the field in [Project Settings](projects.md#project-settings) |
| **− / +** | Decrease / increase the font size |

Set the text in Project Settings (up to 500 characters). It's stored in `.agentdock/settings.json`, so it travels with the repository.

---

## Todo List

A simple per-project checklist, also an **auto-hidden tab on the right edge** by default.

| Action | How |
|--------|-----|
| **Add** | Click **+** |
| **Complete** | Tick the checkbox. Completed items are struck through |
| **Delete** | The item's delete button |

Items are stored in the project's `.agentdock/settings.json`. In a [remote session](remote-sessions.md) they come from — and are saved on — the hosting machine.
