# Project Features

[Features](features.md) | [Workspace](workspace.md) | [Projects](projects.md) | **Project Features** | [AI Chat](ai-chat.md)

---

Each project in Agent Dock comes with three utility panels alongside the [AI Chat](ai-chat.md). These panels are read-only views that help you navigate your codebase and review changes.

## File Explorer

A hierarchical tree view of your project folder.

### Browsing Files

Expand folders to navigate the file tree. Click any file to open it in the [File Preview](#file-preview) panel with syntax highlighting.

### .gitignore Filtering

The file explorer automatically respects your project's `.gitignore` file. Hidden and ignored files are filtered out of the tree.

### Auto-Refresh

A file watcher monitors your project folder for changes. When files are added, removed, or renamed, the tree updates automatically. If the watcher encounters an issue, Agent Dock falls back to periodic polling.

### Toolbar

The File Explorer toolbar provides quick access to external tools:

- **Open in VS Code** — opens the project folder in Visual Studio Code (if installed)
- **Open in Explorer** — opens the folder in Windows Explorer
- **Open Terminal** — opens a command prompt at the project root
- **Project Settings** — opens the [project settings dialog](projects.md#project-settings)

## File Preview

Displays file contents with appropriate formatting based on file type.

### Code Preview

Source code is displayed with syntax highlighting via AvalonEdit. Supported languages include:

C#, Python, JavaScript, TypeScript, JSON, YAML, XML, HTML, CSS, Markdown, Rust, Go, Ruby, Java, C, C++, PowerShell, Bash, SQL, and more.

Line numbers are shown in the left gutter. Horizontal and vertical scrolling is available for large files.

### Markdown Preview

Markdown files (`.md`) open in rendered mode by default, with proper heading sizes, formatting, code blocks, tables, and links. A toggle button in the top-right corner switches between:

- **Rendered view** — formatted markdown with GitHub-like styling
- **Source view** — syntax-highlighted raw markdown text

### Image Preview

Image files (PNG, JPG, GIF, SVG) are displayed centered in the preview area with uniform scaling.

### Diff Preview

When you click a changed file in the [Git Status](#git-status) panel, the preview shows a unified diff with color-coded lines:

- **Green** — added lines
- **Red** — removed lines
- **Purple** — hunk headers

For untracked files, the entire file content is shown as an addition.

### Unsupported Files

Binary files and other unsupported formats show a "No Preview" message.

## Git Status

Shows the current git state of your project — the active branch and any changed files.

### Branch Display

The current branch name is shown at the top of the panel. Next to it:

- **Copy button** — copies the branch name to your clipboard
- **Branch switcher** ("..." button) — opens a popup to switch or create branches

### Branch Switcher

Click the "..." button to open the branch picker:

- **Filter** — type in the textbox to filter the list of local branches
- **Switch** — double-click a branch or select it and press Enter to check it out
- **Current branch** — marked with a checkmark icon
- **Create new** — if your typed name doesn't match any existing branch, a "+" button appears to create and switch to a new branch

Keyboard navigation: **Down arrow** moves to the list, **Enter** selects or creates, **Escape** closes the popup.

### File Status List

Changed files are listed below the branch name, sorted with staged files first:

| Label | Meaning |
|-------|---------|
| **M** | Modified |
| **A** | Added |
| **D** | Deleted |
| **R** | Renamed |
| **?** | Untracked |

Staged files appear in one color and unstaged files in another. A "(staged)" label marks staged entries.

### Viewing Diffs

Click any file in the status list to view its diff in the [File Preview](#file-preview) panel.

### Auto-Refresh

Git status refreshes automatically when files change, using the same file watcher as the File Explorer. Changes are debounced to avoid excessive refreshes during rapid file operations.

### Empty States

- **"Not a git repository"** — shown when the project folder isn't a git repository
- **"No changes"** — shown when all files are committed
