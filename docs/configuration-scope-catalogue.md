# Configuration Scope Catalogue

**Status:** Report / reference. Nothing built.
**Companion to:** [`configuration-architecture-report.md`](configuration-architecture-report.md)
**Purpose:** capture every setting AgentDock has or plausibly wants, and pin down which scope is
allowed to set each one.

---

## 1. Direct answers to the questions asked

### 1.1 "Themes wouldn't fall into the same cascade model — what do you think?"

**You're right, and the reason is worth naming precisely: there are two different things here and
only one of them is a setting.**

| Concept | What it is | Where it lives | Cascades? |
|---|---|---|---|
| **Theme definition** | 140 name→colour pairs forming one coherent artefact | `%LOCALAPPDATA%/AgentDock/themes/*.json`, one file per theme | **No. Never.** |
| **Theme selection** | "which of the installed themes am I using" | a single string key in the settings cascade | Yes |

Definitions stay out because a theme is a *composition*, not a set of independent knobs. Merging
40 keys of one theme over 140 of another produces mud, and the whole point of a theme is that
someone can hand the file to a friend. That's a document, not a config fragment. Everything you
described — a themes folder, drop a file in, detected at startup, appears in the settings picker —
follows from treating them as documents, and it's exactly right.

Selection stays in because "which theme" is one string, and cascading a string is free.

**Where I'd differ slightly from what you said.** You suggested themes could never be
workspace-specific. Two things to weigh:

1. **It already is a workspace feature.** `WorkspaceFile.Theme` exists and `OpenWorkspaceFile`
   applies it (`MainWindow.xaml.cs:3805`). Making theme user-only removes shipped behaviour.
2. It's a genuinely popular pattern — work workspace in Obsidian, personal in Sakura, so you can
   tell at a glance which window you're in.

**Recommendation:** keep `theme.id` settable at **user + workspace**, and add one user-scope key
`theme.allowWorkspaceOverride` (default `true`). If you personally never want a workspace touching
your theme, set it false once and you're immune. Nobody loses a capability, and the ad-hoc
write-back bug (§2.1 of the companion report — opening a workspace currently overwrites your
app-level theme permanently) gets fixed either way.

**Never at project scope.** A repo restyling your entire application on clone-and-open is wrong
regardless of intent. Agreed with you completely there.

### 1.2 "Groups have no location — app data or the workspace file?"

**The workspace file, and it already is there.** `WorkspaceFile.Groups` holds each group's name,
icon and icon colour today. That's the correct home, and not by accident: **a group has no
filesystem location**, so it *cannot* have repo-scoped settings. It's a purely workspace-level
construct. There is nothing to decide — the answer falls out of what a group is.

The interesting follow-on: should a group be able to **override settings for the projects inside
it**? I think yes, and it's cheap — a `settings` block per group in the workspace file, inserting
one rung between workspace and project:

```jsonc
"groups": [
  {
    "id": "…", "name": "Client A", "icon": "briefcase",
    "settings": { "theme.id": "Ember", "font.chat.markdown.size": 14 }
  }
]
```

No new file, no new storage location, no discovery problem. And "this group of projects looks
different so I know where I am" is the natural extension of what groups are already for.

### 1.3 "The app data file should get generated after install"

**Generate the file, but generate it _empty_.** This one is a trap that's easy to walk into.

If you materialise all defaults into the user's file at install, every default is now **pinned
forever**. You ship a better default next year, and no existing user ever sees it, because their
file explicitly specifies the old one. You can't distinguish "the user chose 12" from "the
installer wrote 12". VS Code and Windows Terminal both refuse to do this, and this is why.

Instead, on first run write a file that is a *good invitation to edit*:

```jsonc
// AgentDock settings — %LOCALAPPDATA%/AgentDock/settings.json
// Anything not listed here uses the built-in default.
// Full list of defaults: Settings → Open Default Settings (read-only)
{
  "$schema": "https://agentdock.dev/schemas/settings-1.json"
}
```

Then make the defaults **visible as a read-only document** (Settings → "Open Default Settings"),
which is where discoverability actually comes from. The `$schema` line gives completion and hover
docs the moment they open the file in VS Code. Same for the workspace and project files.

### 1.4 "At project scope: which windows are visible, file preview, widths"

**This is the one place your model needs adjusting, and it's worth doing before anything is
built.** Two separate problems with putting layout at project scope.

**First — it's already stored, somewhere else.** Panel visibility, sizes and dock positions are
persisted today as opaque AvalonDock XML in `WorkspaceProject.DockingLayout`
(`WorkspaceFile.cs:42`, written at `MainWindow.xaml.cs:3735` via `XmlLayoutSerializer`). So layout
lives **per project, inside the workspace file** — meaning the same repo opened in two different
workspaces already keeps two independent layouts. That's arguably correct: layout is a property of
*how you're working right now*, not of the repo.

**Second — it's state, not settings.** Panel widths change every time you drag a splitter. Putting
that in a committed repo file means every splitter drag is an uncommitted diff, and a teammate
pulling your changes gets their panels rearranged. That's the config/state conflation problem from
the companion report, in its most annoying form.

**Recommendation: split the concept.**

| | Kind | Scope | Storage |
|---|---|---|---|
| `panels.defaultVisible` — which panels a *fresh* open shows | setting | project OK | project settings file |
| `panels.<id>.defaultWidth` — starting size | setting | project OK | project settings file |
| current layout (live positions, sizes, dock state) | **state** | workspace | `DockingLayout` XML, as today |

So a repo can legitimately say "this project doesn't use the Todo panel, and the file explorer
should start narrow" — a genuine, shareable statement about the project. What it can't do is
overwrite the layout you personally arranged five minutes ago. Fresh open reads the defaults;
after that, your live layout wins and is remembered per workspace.

---

## 2. The scope ladder

Six layers, lowest precedence first. Each is justified; if scope needs cutting, **cut 4 and 6
first** — they're the optional refinements.

| # | Layer | Location | Committed? | Answers |
|---|---|---|---|---|
| 0 | **System defaults** | compiled into the assembly, from the settings registry | n/a | "how it behaves on a fresh install" |
| 1 | **User** | `%LOCALAPPDATA%/AgentDock/settings.json` | no | "how I like AgentDock" |
| 2 | **Workspace** | inside the `*.agentdock` file | usually | "how this workspace is set up" |
| 3 | **Group** | `groups[].settings` in the `*.agentdock` file | usually | "this cluster of projects looks different" |
| 4 | **Project-in-workspace** | `projects[].settings` in the `*.agentdock` file | usually | "restyle this project *here only*, without touching its repo" |
| 5 | **Project** | `<project>/.agentdock/settings.json` | usually | "how this repo wants to present itself" |
| 6 | **Local overrides** | `*.agentdock.local.json` / `.agentdock/settings.local.json`, gitignored | **no** | "my personal tweaks to a shared file" |

Which matches the walkthrough described: open AgentDock with no workspace → layers 0+1. Open a
workspace → 2 and 3 apply. A project opens → 4 and 5 apply. Layer 6 is the escape valve that keeps
2–5 safely shareable.

**Two rules that make the ladder trustworthy:**

- **Resolution never writes.** Reading an effective value must not persist anything anywhere. Both
  current write-back bugs (theme, toolbar position) come from resolution and persistence sharing a
  code path.
- **`"root": true`** in any file means "resolve from defaults, ignore every layer above me" —
  borrowed from `.editorconfig`. Needed the first time a project must be immune to a workspace's
  opinions.

**Why layer 4 beats layer 5:** layer 4 is *your* explicit choice about that project in this
workspace; layer 5 is the *repo author's* suggestion. The local, deliberate choice should win.

---

## 3. The scope-capability matrix

The core rule. Each setting declares its **maximum** permissible scope; a file at a deeper scope
attempting a restricted key is **ignored and visibly reported** (greyed in the UI with a reason,
plus a log line) — never silently dropped, or legitimate authors get mystery no-ops.

| Class | May be set at | Rationale |
|---|---|---|
| **`user-only`** | 1, 6 | Anything naming an executable, filesystem path outside the project, network endpoint, credential, or agent permission level. A repo must never influence these. |
| **`window`** | 1–4, 6 | Global application chrome — title bar, group bar, toolbar, usage popup, theme. A repo shouldn't restyle the whole window on open. |
| **`project`** | 1–6 | The project's own identity and its own panels. Cosmetic and locally scoped; harmless if a repo author sets it. |

This is VS Code's `machine` / `window` / `resource` distinction, renamed. The load-bearing detail:
**a new setting defaults to `user-only` until deliberately widened**, so forgetting to think about
scope fails safe rather than open.

### 3.1 The `user-only` list is a security boundary, not a preference

Restated from the companion report because it's the thing that must not be got wrong:
`<project>/.agentdock/settings.json` is **inside the repo** and arrives with `git clone`.

Two keys make this concrete:

- **`claude.path`** — already exists (`MainWindow.xaml.cs:161`, `ClaudeSession.cs:31`), a full
  override of the Claude executable path. At project scope: clone repo, open it, AgentDock runs an
  attacker-chosen binary. No prompt.
- **`claude.dangerousMode`** — `bypassPermissions` today is a per-start runtime choice
  (`ClaudeSession.cs:161-177`), not persisted. If it ever becomes a persisted setting, project
  scope would let a repo declare *"run your agent against me with all permission checks
  disabled"*. That is the worst possible key to cascade.

Both are the CVE class that forced VS Code to build Workspace Trust.

### 3.2 The "per-project, stored at user scope" pattern

Some settings are *about* a project but must not be *stored in* it. `claude.dangerousMode` is the
example: it's a per-project decision, and it's a trust decision, so the repo can't be the one
making it.

The pattern — a **user-scope map keyed by project path**, which is how VS Code stores trusted
folders:

```jsonc
// user settings.json — user-only scope
"claude.projectOverrides": {
  "C:\\me\\dev\\github\\agent-dock": { "dangerousMode": true }
}
```

Worth having in the vocabulary, because the intuitive answer ("put it in project settings") is the
wrong one for anything trust-related.

---

## 4. The catalogue

`✓` = exists today · `+` = proposed/discussed · scope = **maximum** permissible layer.

### 4.1 Environment & integrations — `user-only`

| Key | Type | | Notes |
|---|---|---|---|
| `claude.path` | path | ✓ | Executable override. **Security-critical.** |
| `claude.defaultAccountId` | string | ✓ | Currently `ClaudeAccountId` in project settings — see §5.3 |
| `claude.dangerousMode` | bool | + | Currently runtime-only. If persisted: user-only, per-project map (§3.2) |
| `claude.defaultModel` | enum | + | |
| `updates.channel` | enum | ✓ | stable / beta |
| `updates.checkOnStartup` | bool | + | |
| `diagnostics.logLevel` | enum | + | `Log` has no level control today |
| `diagnostics.perfDiagnostics` | bool | + | `PerfDiagnostics` exists, no toggle |
| `accounts.*` | records | ✓ | Registry. Currently double-encoded as a JSON string (`AccountManager.cs:37,54`) |

### 4.2 Window chrome & shell — `window`

| Key | Type | | Notes |
|---|---|---|---|
| `toolbar.position` | enum | ✓ | top/left/right/bottom. Exists at both user + workspace with the write-back bug |
| `titleBar.showUsageIndicator` | bool | + | |
| `titleBar.showTotalCost` | bool | + | Your example. `TotalCostText`, fixed `Grid.Column="6"` |
| `titleBar.showTokenCounts` | bool | + | Your example |
| `titleBar.showWorkspaceName` | bool | + | |
| `usage.popup.orientation` | enum | + | vertical / horizontal — your example |
| `usage.popup.items` | **map** | + | Per-item `visible` + sparse `order`. Map not array, so scopes merge per key |
| `usage.refreshIntervalMinutes` | int | + | |
| `groupBar.addButtonPosition` | enum | + | left / afterTabs / farRight — your example |
| `groupBar.showActiveProjectsGroup` | bool | ✓ | Currently workspace-only (`ShowActiveProjectsGroup`) |
| `groupBar.activeProjectsLimit` | int | + | Hardcoded const 5 (`ActiveProjectsLimit`) |
| `groupBar.showStatusDiamond` | bool | + | |
| `projectBar.addButtonPosition` | enum | + | |
| `projectBar.showStatusDiamond` | bool | + | |
| `tabs.height` / `tabs.cornerRadius` | int | + | Hardcoded `Height = 32`, `TabCornerRadius = 5` |
| `ui.density` | enum | + | compact / comfortable — drives paddings and tab height together |

### 4.3 Appearance & typography

| Key | Type | Scope | | Notes |
|---|---|---|---|---|
| `theme.id` | string | window | ✓ | §1.1 |
| `theme.allowWorkspaceOverride` | bool | **user-only** | + | Lets you pin your theme globally |
| `theme.colorOverrides` | map | **user-only** | + | Per-key nudges, optionally per-theme scoped |
| `font.ui.family` / `.size` | font | window | + | |
| `font.code.family` / `.size` | font | window | + | Hardcoded `MarkdownHelper.cs:76` |
| `font.chat.markdown.family` / `.size` | font | window | + | Your example |
| `font.projectDescription.size` | double | **project** | ✓ | Already project-scoped (`DescriptionFontSize`) — the precedent for "a project may set fonts for *its own* panels but not global chrome" |
| `ui.cornerRadius` | double | window | + | |

### 4.4 Panels & layout

| Key | Type | Scope | | Notes |
|---|---|---|---|---|
| `panels.defaultVisible` | map by panel id | project | + | Ids exist: `AiChatId`, `FilePreviewId`, `FileExplorerId`, `GitStatusId`, `ProjectDescriptionId`, `TodoListId` |
| `panels.<id>.defaultWidth` / `.defaultHeight` | double | project | + | Starting size only — §1.4 |
| `panels.<id>.defaultDock` | enum | project | + | |
| *current layout* | — | — | ✓ | **State**, not a setting. Stays as `DockingLayout` XML per project in the workspace file |

### 4.5 Chat

| Key | Type | Scope | | Notes |
|---|---|---|---|---|
| `chat.sendKey` | enum | window | + | Enter vs Ctrl+Enter |
| `chat.showSubagentActivity` | bool | window | + | Feature exists, no toggle |
| `chat.showTimestamps` | bool | window | + | |
| `chat.markdown.tableStyle` | enum | window | + | |
| `chat.images.maxDimension` | int | window | + | `ImageAttachmentHelper` |
| `chat.queue.autoSend` | bool | window | + | `SendQueue` |
| `chat.scheduled.defaultDelay` | duration | project | + | |

### 4.6 Explorer, git, preview

| Key | Type | Scope | | Notes |
|---|---|---|---|---|
| `explorer.respectGitignore` | bool | project | + | `GitIgnoreFilter` exists, no toggle |
| `explorer.showHiddenFiles` | bool | project | + | |
| `explorer.excludeGlobs` | array | project | + | Array **replaces**, never merges |
| `git.refreshIntervalSeconds` | int | project | + | |
| `git.showStagedSeparately` | bool | window | + | |
| `preview.wordWrap` | bool | project | + | |
| `preview.showLineNumbers` | bool | project | + | |
| `preview.maxFileSizeKb` | int | project | + | |
| `preview.syntaxTheme` | string | window | + | Currently derived from `BaseVariant` only |

### 4.7 Project identity — `project`

| Key | Type | | Notes |
|---|---|---|---|
| `project.name` | string | ✓ | Display override |
| `project.icon` | string | ✓ | Built-in name, relative or absolute path |
| `project.iconColor` | colour | ✓ | |
| `sounds.onSessionStart` | bool | ✓ | |
| `sounds.onAgentWaiting` | bool | ✓ | |
| `sounds.onSessionEnd` | bool | ✓ | |

Note on sounds: currently project-scoped, which means a repo can turn your sounds on. Benign, and
annoying at worst — but it's the reason `user-only` needs to be the *default* scope for anything
new, with widening as the deliberate act.

### 4.8 Group identity — layer 3 only

| Key | | Notes |
|---|---|---|
| `name`, `order`, `icon`, `iconColor` | ✓ | Already in `WorkspaceFile.Groups`. No other home is possible — groups have no filesystem location |
| `settings` block | + | §1.2 |

---

## 5. Things that look like settings and aren't

Keeping these out is what makes the settings files clean documents worth hand-editing and
committing.

### 5.1 State — never in a settings file, never committed

Window bounds and maximised state · current docking layout per project · active project ·
active group · recent workspaces MRU · last-used account per project · scroll positions ·
expanded tree nodes · session ids.

Currently misplaced: `ActiveProjectPath` and `DockingLayout` (workspace file — defensible, since a
workspace *is* a session document, but should be a clearly-marked state section);
recent-workspaces list (app settings); `ClaudeAccountId` (project settings).

### 5.2 Data — user content, its own storage

`TodoItems` and `Description`, both currently in `ProjectSettings`. These are things the user
*wrote*, not preferences. They belong in `.agentdock/todo.json` and `.agentdock/description.md`
(the latter also becoming editable in any editor, and diffable, which is a bonus).

### 5.3 Secrets — never in any settings file at any scope

OAuth tokens and credentials. Already handled correctly — `AccountManager` keeps
`.credentials.json` in a per-account config dir outside settings. Worth stating explicitly so it
stays that way.

---

## 6. Themes as a separate model

### 6.1 Storage and discovery

```
%LOCALAPPDATA%/AgentDock/themes/
    nord-dock.json
    solarized-dock.json
```

Scanned at startup, plus a `FileSystemWatcher` so a theme dropped in (or saved during authoring)
appears without a restart. Built-in themes stay compiled in and always available. A theme is
**one self-contained file** — which is the whole requirement for handing it to a friend.

### 6.2 File shape

```jsonc
{
  "$schema": "https://agentdock.dev/schemas/theme-1.json",
  "name": "Nord Dock",
  "author": "Steven",
  "extends": "Obsidian",       // inherit all 140 keys; override only what you name
  "baseVariant": "dark",       // required — drives AvalonDock + syntax highlighting
  "colors": {
    "TabButtonActiveBorderBrush":  "#88C0D0",
    "ProjectTabActiveBorderBrush": "#B48EAD"
  }
}
```

- **`extends` is essential.** Requiring 140 keys means nobody authors a theme. It also fixes the
  partial-theme experience: `GetBrush` returns `Brushes.Magenta` for missing keys
  (`ThemeManager.cs:88-93`) — right for catching developer bugs, disastrous for a user's 40-key
  theme.
- **`baseVariant` is required**, not cosmetic: it selects the AvalonDock theme
  (`MainWindow.xaml.cs:2388-2390`) and the syntax highlighting set (`ThemeManager.cs:70-71`).
- **JSON, not XAML.** Themes are precisely the file type people download from strangers, and
  loading untrusted XAML means instantiating arbitrary CLR types. A validated colour map costs
  nothing in expressiveness — the contract is already 140 flat colours.
- **All-or-nothing application.** A malformed theme reports the offending key and leaves the
  previous theme in place. Never half-applied.

### 6.3 Should a theme carry fonts?

Open question worth deciding early, because it determines whether typography tokens are in the
theme file or only in settings.

**Recommendation: a theme may _suggest_ typography; an explicit user setting always wins.** So
Parchment can ship a serif UI font as part of its identity, but `font.ui.family` in your settings
overrides it. Resolution order for typography becomes:
defaults → theme suggestion → settings cascade.

That keeps themes expressive without making them able to override things you deliberately chose.

### 6.4 Theme builder UI

The feature that makes user themes real, since the honest blocker isn't loading — it's that
**140 keys named things like `TabIconInactiveDiamondForeground` are undiscoverable**.

What it needs:

- All 140 keys **grouped by area** (title bar, group bar, project tabs, chat, explorer, git,
  preview, diagnostics) with a plain-English description of what each paints
- A colour picker per key, with the inherited value from `extends` shown as the swatch's starting
  point and a clear "inherited vs overridden" distinction
- **Live preview** — apply on change. Nearly free: everything resolves through `DynamicResource` /
  `GetBrush` and `ApplyTheme` already hot-swaps the merged dictionary
- Start-from: duplicate an existing theme, which is how essentially every theme gets made
- Export / Save As, writing a clean file with only the keys that differ from `extends`
- Import, with validation errors surfaced against the offending key

Two prerequisites regardless of the builder: a **generated reference doc** for the 140 keys
(swatches per built-in theme), and a **key-set audit** — the recent tab work needed a brand-new
`ProjectTabActiveBorderBrush`, which suggests organic gaps. Better to find them *before* third
parties author against the contract, because after that it's a compatibility surface.

---

## 7. What's still open

1. **Does `theme.id` stay workspace-settable?** §1.1 — I'd keep it plus an opt-out, since it's
   shipped behaviour, but this is your call and it's a small deliberate regression either way.
2. **Do themes carry typography?** §6.3.
3. **Is layer 4 (per-project overrides inside the workspace file) worth it?** It's the cleanest
   answer to "restyle this project here only, without committing to its repo" — but it's also the
   rung most likely to confuse, since two places then configure the same project.
4. **Does the project settings file stay inside the repo at all?** It's the shareable option *and*
   the attack surface *and* the source of team churn. The alternative — project settings only ever
   in the workspace file (layer 4) — has none of those problems, at the cost of not travelling
   with the repo.
5. **Keybindings.** Not raised, but it's the other large configurable surface in tools like this,
   and the one thing that genuinely warrants its own file. Worth deciding now whether to leave
   room in the namespace.
6. **Do arrays ever need to merge?** Recommendation is no, never (§4.6 `explorer.excludeGlobs`
   replaces wholesale). If a real "add to the inherited list" case appears, it needs an explicit
   token, and those get ugly fast.
