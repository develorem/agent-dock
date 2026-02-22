# Agent Dock — Task List

## Status Key

- `[ ]` — Not started
- `[~]` — In progress
- `[x]` — Complete
- `[!]` — Blocked

---

## Task 1: Project Scaffolding
**Status:** `[x]`

Set up .NET 10 WPF solution structure, NuGet packages (AvalonDock, AvalonEdit, etc.), project files, basic build configuration. Add Develorem branding metadata (assembly info, app icon placeholder).

**Done criteria:**
- Solution builds and runs with `dotnet build` / `dotnet run`
- Shows an empty window with "Agent Dock" in the title bar
- NuGet packages referenced: AvalonDock (Dirkster), AvalonEdit
- Solution structure is clean and follows .NET conventions

---

## Task 2: Main Window Shell + Menu
**Status:** `[x]`

Implement the main window with a standard Windows menu bar (File, Settings, Help). File menu has: Add Project, Open Workspace, Save Workspace, Recent Workspaces, Exit. Settings has: Theme toggle, Toolbar position. Help has: Getting Started, About. Add the configurable toolbar (top by default) with a `+` button to add projects.

**Done criteria:**
- App launches with menu bar and toolbar visible
- All menu items exist (can be non-functional stubs except Exit)
- Toolbar shows `+` button
- Toolbar position can be changed via Settings menu (top/left/right/bottom)
- Position change takes effect immediately for the session

---

## Task 3: Project Tab System
**Status:** `[x]`

Clicking `+` (or File > Add Project) opens a folder picker dialog. Selecting a folder creates a project tab in the toolbar with a generic project icon and the folder name. Tabs can be clicked to switch between projects. Right-click tab to close/remove project. Closing a project tab removes it from the toolbar and disposes its content. The main content area switches to show the selected project.

**Done criteria:**
- Can add multiple project folders via `+` button or File menu
- Each project appears as a tab in the toolbar with folder name
- Clicking a tab switches the main content area
- Right-click tab shows context menu with Close option
- Closing a tab removes it and cleans up
- Adding duplicate folder path is prevented (shows warning)
- Main content shows placeholder per project (e.g., "Project: C:\path\to\folder")

---

## Task 4: Per-Project Docking Layout
**Status:** `[x]`

Each project tab gets its own AvalonDock DockingManager with the default 3-column layout: left column (File Explorer placeholder + Git Status placeholder stacked), center (File Preview placeholder), right (AI Chat placeholder). Panels can be rearranged, tabbed, and floated within the project's docking area. Each project's layout is independent.

**Done criteria:**
- Adding a project shows 4 placeholder panels in the default 3-column layout
- Panels can be dragged, docked, tabbed, and floated
- Switching between project tabs preserves each project's layout independently
- Panel titles are: "File Explorer", "Git Status", "File Preview", "AI Chat"
- Layout changes in one project don't affect other projects

---

## Task 5: File Explorer Panel
**Status:** `[x]`

Read-only tree view of the project folder. Lazy-loads directory contents on expand (no loading entire tree upfront). Click a file to send a "preview this file" event. Folder and file type icons. Respects .gitignore (hide ignored files/folders). Handles large directories without UI freeze (async loading).

**Done criteria:**
- File explorer shows the project's folder tree with expand/collapse
- Directories lazy-load contents on first expand
- Clicking a file raises a preview event (wired up in Task 6)
- .gitignore patterns are respected (node_modules, bin, etc. are hidden)
- No UI freeze on large directories
- Folder/file icons are present

---

## Task 6: File Preview Panel
**Status:** `[x]`

Displays the file selected from either File Explorer or Git Status panel. Uses AvalonEdit for syntax highlighting of common languages (C#, JS/TS, Python, JSON, YAML, XML, HTML, CSS, Markdown, etc.). Image preview for PNG/JPG/GIF/SVG. Markdown rendering (or syntax-highlighted markdown as fallback). "No Preview" message for binary files. For git-changed files clicked from the Git Status panel, show inline/unified diff.

**Done criteria:**
- Clicking a file in explorer shows it with syntax highlighting (correct language detection)
- Images (PNG, JPG, GIF, SVG) display inline
- Markdown files render as formatted markdown (or at minimum syntax-highlighted)
- Binary files show "No Preview" placeholder
- Diff view works for git-changed files (inline/unified format)
- Large files handle gracefully (truncate or virtualize)

---

## Task 7: Git Status Panel
**Status:** `[x]`

Shows modified, added, deleted, and untracked files for the project's git repo. Staged files displayed in one color (e.g., green), unstaged in another (e.g., orange/yellow). Auto-refreshes on file system changes or on a reasonable polling interval. Clicking a file opens its diff in the File Preview panel. Non-git folders show a "Not a git repository" message.

**Done criteria:**
- Git status panel shows correct file statuses grouped by state
- Staged files are visually distinct from unstaged (different colors)
- Clicking a changed file shows its diff in the preview panel
- Panel refreshes when files change (within a few seconds)
- Non-git folders display informative message
- Deleted files are shown and handled (no crash)

---

## Task 8: Claude Code Subprocess Manager
**Status:** `[x]`

Core service that spawns `claude` CLI as a subprocess, communicates via the JSON-lines protocol over stdin/stdout. Handles: starting a session (normal and dangerous mode), sending user messages, receiving streaming responses, permission/control requests, detecting state (idle, working, waiting for input), graceful shutdown. One instance per project. Detects if `claude` is in PATH and reports error if not.

**Done criteria:**
- Can programmatically start a Claude Code session for a given directory
- Can send a user message and receive a streamed response
- Permission/control requests are captured and can be responded to
- State transitions (not-started → idle → working ↔ waiting) are emitted as events
- Graceful shutdown kills the subprocess cleanly
- Error handling: Claude not found, crash recovery, process exit detection
- All testable without UI (service layer tests or console harness)

---

## Task 9: AI Interaction Panel
**Status:** `[ ]`

Terminal-styled chat panel. Monospace font, themed dark background. Custom Agent Dock prompt icon for user input line. User messages and AI messages are visually distinct (different alignment or color). Streaming responses render in real-time as tokens arrive. Past messages are collapsed by default (show first line + expand/collapse button). Text input at the bottom with send button. Options to start Claude session (normal mode or dangerous mode). When Claude requests permission, the input area is replaced with an inline approval prompt showing context + Allow/Deny buttons.

**Done criteria:**
- Can start a Claude session (normal or dangerous mode) from the panel
- Can type and send messages; see streaming responses in real-time
- User vs AI messages are clearly visually distinct
- Past conversation messages collapse to first line with expand button
- Permission requests appear inline replacing the input area
- Allow/Deny buttons work and resume the Claude session
- Terminal aesthetic: monospace font, appropriate colors, custom prompt icon
- Panel is functional end-to-end with real Claude Code interaction

---

## Task 10: Toolbar Status Icons + Badges
**Status:** `[ ]`

Project tabs in the toolbar display dynamic state icons:
- Generic project icon when no Claude session is running
- Claude icon when session is active and idle
- Claude icon + red skull badge for dangerous mode
- Claude icon + spinner/activity overlay when Claude is working
- Claude icon + `?` overlay when waiting for user input

Icons sized appropriately to accommodate multiple badges. State updates driven by Claude subprocess manager events.

**Done criteria:**
- All 5 icon states render correctly
- State transitions happen in real-time as Claude works
- Badges are clearly visible even at toolbar icon size
- Dangerous mode skull is distinct and noticeable
- Multiple badges can coexist (e.g., skull + spinner for dangerous mode + working)

---

## Task 11: Light/Dark Theme System
**Status:** `[ ]`

Global theme toggle via Settings menu. Dark theme: dark backgrounds, light text, styled consistently across all panels. Light theme: standard light UI. AvalonDock panels, file explorer, git status, file preview, and AI panel all respect the theme. AI panel maintains terminal aesthetic in both themes. Theme persists across sessions (stored in app settings file).

**Done criteria:**
- Can toggle between light and dark mode via Settings menu
- All panels and app chrome update immediately on toggle
- AvalonDock frame/tabs are themed
- AI panel looks like a terminal in both themes
- Theme choice persists on app restart
- No visual glitches during theme switch

---

## Task 12: Workspace Save/Load
**Status:** `[ ]`

Workspace file (.agentdock extension) serializes: list of open project paths, per-project docking layouts, toolbar position, theme setting, per-project Claude session mode (not the session itself). File > Save Workspace and File > Open Workspace dialogs. Recent Workspaces submenu under File menu (last 5-10 workspaces). On close: if workspace has unsaved changes since last save, prompt "Save workspace?" with Yes/No/Cancel. Closing the app kills all running Claude instances gracefully.

**Done criteria:**
- Can save a workspace with 2+ projects to a .agentdock file
- Can load a workspace and see all projects restored with correct layouts
- Recent Workspaces submenu shows previously opened workspace files
- Closing with unsaved changes prompts the user
- All Claude instances are killed on app close
- Workspace file is human-readable (JSON)

---

## Task 13: Help Documentation
**Status:** `[ ]`

Help > Getting Started opens an in-app panel/document explaining: what Agent Dock is, prerequisites (Claude Code CLI installed and in PATH), how to add projects, how to start an AI session, keyboard shortcuts, links to Claude Code installation docs, link to Develorem GitHub. Help > About shows version, Develorem logo, license info, GitHub link.

**Done criteria:**
- Getting Started content is accessible and accurate
- About dialog shows Develorem branding, version, license
- External links open in default browser
- Content is helpful for a first-time user

---

## Task 14: Polish + Branding
**Status:** `[ ]`

Fetch and integrate Develorem logo. App icon for taskbar/title bar. Consistent styling across all panels. Keyboard shortcuts: Ctrl+N (new project), Ctrl+S (save workspace), Ctrl+W (close project tab), etc. Edge case handling: opening same folder twice (warning), invalid folder paths, Claude crash recovery (show error in AI panel, allow restart), empty project folder.

**Done criteria:**
- App icon is set (Develorem branded)
- About dialog shows logo
- Keyboard shortcuts work
- Edge cases don't crash the app
- Overall visual consistency and polish
- App feels professional and complete

---

## Workflow

1. Each task is implemented incrementally
2. After completing a task, **do not commit** — notify the user for testing
3. User tests and approves
4. On approval: commit the work, then proceed to next task
5. If issues found: fix before committing
