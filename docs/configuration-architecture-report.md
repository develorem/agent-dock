# Configuration & Custom Themes — Design Report

**Status:** Report for discussion. Not a plan, not a commitment, nothing built.
**Date:** 2026-08-06
**Scope:** How AgentDock should handle user configuration end to end, and how user-authored
themes fit inside that answer.
**Companion:** [`configuration-scope-catalogue.md`](configuration-scope-catalogue.md) — the
per-setting inventory and scope rules, plus refinements to §5.3 (a **group** layer, since groups
have no filesystem location) and §6.1 (theme *definitions* leave the cascade entirely; only theme
*selection* cascades).

---

## 1. What's actually being asked

Three things, which sound like one thing but aren't:

1. **A cascading configuration system** — app → workspace → project, where the nearest scope
   that defines a value wins and anything undefined falls through to the scope above.
2. **A much larger configurable surface** — usage popup contents/order/orientation, whether the
   title bar shows cost and tokens, which side the group `+` sits on, the markdown font, and so
   on down to "absolutely everything".
3. **User-authored themes** — which is where this conversation started, and which turns out to
   be the *easiest* of the three, provided decision (1) is made first.

The instinct to settle (1) before (3) is right, but not for the reason it might appear. It isn't
that themes need the cascade. It's that **themes must be kept _out_ of the cascade**, and that's
only an obvious conclusion once you've looked at what the cascade is for. More in §6.

---

## 2. Where AgentDock is today

There are already **four** independent configuration stores, each with its own format, scope,
accessor, and failure mode.

| Store | Location | Format | Scope | Accessor |
|---|---|---|---|---|
| App settings | `%LOCALAPPDATA%/AgentDock/settings.json` | flat `JsonObject`, strings + string arrays only | machine/user | `AppSettings` (`Services/AppSettings.cs`) |
| Workspace file | user-chosen `*.agentdock` | typed POCO, `Version = 2` | workspace | `WorkspaceManager` + `Models/WorkspaceFile.cs` |
| Project settings | `<project>/.agentdock/settings.json` | typed POCO, camelCase | project | `ProjectSettingsManager` + `Models/ProjectSettings.cs` |
| Themes | `Themes/*.xaml`, compiled into the assembly | WPF `ResourceDictionary` | app | `ThemeManager` / `ThemeRegistry` |

**What's in them right now.** App settings hold five keys: `Theme`, `ToolbarPosition`,
`ClaudePath`, `UpdateChannel`, a recent-workspaces list, plus an `accounts` entry. The workspace
file holds `Theme`, `ToolbarPosition`, `ActiveProjectPath`, `Projects[]`, `Groups[]`,
`ActiveGroupId`, `ShowActiveProjectsGroup`. Project settings hold name, icon, icon colour,
description, description font size, todo items, three sound toggles, and last-used account id.

That's roughly **25 user-facing settings across three scopes** — and it already has the problems
a bigger surface makes fatal.

### 2.1 The cascade already exists, informally, and it already leaks

`Theme` and `ToolbarPosition` live in **both** app settings and the workspace file. There is no
merge model, so precedence is whatever load order happens to be, and the resolution is
destructive in both cases:

- `MainWindow.xaml.cs:3811` — opening a workspace writes that workspace's `ToolbarPosition`
  **back into app settings**. Your app-level default is now silently gone, replaced by whatever
  the last workspace you opened preferred.
- `ThemeManager.ApplyTheme` unconditionally calls `SaveThemePreference`
  (`ThemeManager.cs:73, 122-125`), so the same happens for theme: open a workspace pinned to
  Ember and your app default has been overwritten.

Neither is a coding mistake as such. They're the inevitable consequence of having two scopes and
no concept of "effective value" separate from "stored value". **A real cascade is worth building
mainly because it makes this class of bug impossible**, not because it adds features. With 25
settings you can paper over it; with 250 you cannot.

### 2.2 Configuration and state are conflated

`ProjectSettings` mixes genuine settings (icon, sounds, font size) with pure application state
(todo items, description text, last-used account id). `AppSettings` mixes preferences with the
recent-workspaces MRU list. `WorkspaceFile` mixes preferences with per-project docking layout XML
and the last active project.

This matters more than it sounds. Settings files are **documents the user edits and commits**.
State churns on every interaction. Put them in one file and you get: a settings file that changes
whenever someone opens a tab, unreadable diffs, merge conflicts on shared repos, and no safe way
to hand-edit anything. Every mature tool separates these — VS Code keeps state in a SQLite blob
(`state.vscdb`) precisely so `settings.json` stays a clean, human-owned document.

### 2.3 The accounts registry is double-encoded

`AccountManager.cs:37, 54` serialises the account list to a JSON **string** and stores that string
as a JSON string value inside `settings.json`. So the file contains an escaped JSON blob rather
than a JSON object. This is a direct consequence of `AppSettings` only supporting `string` and
`List<string>` — the API shape forced it. Any real settings model makes this a first-class node.

### 2.4 `AppSettings` will not scale mechanically

Every `GetString` re-reads and re-parses the entire file from disk under a global lock
(`AppSettings.cs:25-40`); every `SetString` re-serialises and rewrites the whole file
(`AppSettings.cs:116-121`). At five keys read a handful of times at startup this is completely
fine. At 250 keys read during layout and paint, it is not. Any real system needs a single load
into an in-memory model, reads served from memory, and debounced/coalesced writes.

### 2.5 The theme contract is in unexpectedly good shape

The single most encouraging finding. All six theme dictionaries define **exactly 140 keys**, and
I verified the key sets are **byte-identical across all six files** — no theme is missing a key,
none has an extra. Every value is a `SolidColorBrush`. Consumption is already uniform: 105
distinct `DynamicResource` keys referenced from XAML, 39 distinct `ThemeManager.GetBrush(...)`
keys from code.

That means the theme contract is *already* a flat `name → colour` map of 140 entries, and because
everything resolves through `DynamicResource` / `GetBrush`, **swapping a dictionary at runtime
already repaints the app live**. `ThemeManager.ApplyTheme` does exactly this today
(`ThemeManager.cs:60-67`). User themes are far closer than the absence of the feature suggests.

### 2.6 …but typography and layout aren't themeable at all

There is no equivalent contract for anything that isn't a colour:

- **228** hardcoded `FontSize="n"` literals across the XAML files
- **43** hardcoded `FontSize = n` assignments in C#
- Fonts hardcoded at the point of use — e.g. `MarkdownHelper.cs:76`,
  `new("Cascadia Code, Consolas, Courier New")`, plus `Segoe MDL2 Assets` glyph fonts scattered
  through both XAML and code
- Sizes, paddings and margins as literals everywhere (the tab work we just did is a good example:
  `Height = 32`, `TabCornerRadius = 5`, `Padding(12,0,12,0)` are all inline constants)

So "let me set the markdown font" has nowhere to land right now. Not because it's hard, but
because there is no *token layer* — no named, resolvable, overridable "the UI font size" that 228
call sites read from. §7 covers this; it's the single largest cost in the whole ambition, and it
dwarfs the cascade and the themes combined.

### 2.7 Naming collision worth fixing while you're in here

`.agentdock` is both the **workspace file extension** (`mywork.agentdock`) and the **per-project
config folder** (`<project>/.agentdock/settings.json`). Two different concepts, same token. VS
Code has the same pairing but names them distinctly (`*.code-workspace` file vs `.vscode/`
folder), which is why nobody is ever confused by it.

---

## 3. How the tools you named actually do it

### 3.1 VS Code

The reference implementation, and worth understanding in detail because most of its decisions are
scar tissue from getting it wrong first.

**Flat, dot-namespaced keys.** `settings.json` is a flat map: `"editor.fontSize": 14`,
`"workbench.colorTheme": "Default Dark+"`. The dots are *namespacing convention*, not nesting.
This is the most important and least obvious decision in the whole design — see §5.2.

**JSONC.** Comments and trailing commas allowed. Users annotate their configs; a format that
strips comments on write is a format users won't hand-edit.

**A scope ladder, with per-setting scope restrictions.** Precedence runs
Default → User → Remote → Workspace → Workspace Folder, with an orthogonal
language-override layer at each level (`"[markdown]": { "editor.wordWrap": "on" }`).

Critically, **each setting declares in its schema which scopes may set it**:

| Scope | Meaning |
|---|---|
| `application` | User settings only. Never per-workspace. |
| `machine` | User/machine only — cannot be set by a workspace at all. |
| `machine-overridable` | Machine-scoped but a trusted remote may override. |
| `window` | Workspace-settable. |
| `resource` | Settable per file/folder. |
| `language-overridable` | Can additionally be overridden per language. |

**Workspace Trust.** Because `.vscode/settings.json` is *inside the repo* and travels with a
`git clone`, VS Code shipped an entire trust subsystem: untrusted folders run in Restricted Mode
where a documented list of "restricted settings" is ignored. The canonical examples are executable
paths — `git.path`, `php.validate.executablePath`. This exists because those settings were used
for remote code execution: clone a repo, open it, the repo's own settings file redirects your
"git" to a payload. **This is the single most important thing to carry across to AgentDock**, for
reasons in §5.4.

**The UI is generated from a schema.** Extensions contribute a `configuration` block — a JSON
schema per setting with `type`, `default`, `enum`, `enumDescriptions`, `markdownDescription`,
`scope`, `order`, `tags`. From that one declaration VS Code derives the Settings UI, the search
index, the JSON IntelliSense, the hover docs, the validation squiggles, the "Modified" filter and
the per-scope tabs. **One declaration, five consumers.** With hundreds of settings this isn't a
nicety, it's the only reason the thing is maintainable.

**Defaults are a viewable document.** "Open Default Settings (JSON)" shows the full generated
defaults read-only, side by side with your overrides. Enormously effective for discoverability —
the docs are the artifact.

**Themes are not settings.** `workbench.colorTheme` is a setting that names a theme; the theme
itself is a separate `*.color-theme.json` document with `type` (`dark`/`light`/`hc`), `colors`
(several hundred documented workbench keys), `tokenColors`, `semanticTokenColors`, and an
`include` for inheriting another theme. Themes can be partial — anything unspecified falls back
to the base implied by `type`.

**But individual theme colours *are* overridable from settings**, via
`workbench.colorCustomizations`, optionally scoped per theme:

```jsonc
"workbench.colorCustomizations": {
  "[Default Dark+]": { "tab.activeBorderTop": "#A371F7" }
}
```

That hybrid — **themes as documents, plus a per-key override map inside the settings cascade** —
is precisely the shape to copy. It's what lets someone nudge one colour without forking a whole
theme.

### 3.2 Visual Studio

Two lessons, one positive and one cautionary.

**`.editorconfig` (positive).** Directory-hierarchy cascade: the effective config for a file is
computed by walking up from the file, with `root = true` as an explicit sentinel to stop
climbing. That sentinel is the useful idea. Without it, a cascade rooted in the filesystem has no
way to say "stop here, ignore anything above me" — which you will want the first time a project
sits inside a workspace whose defaults it needs to fully reject.

**`.vssettings` (cautionary).** The legacy model: one giant opaque XML blob, exported and imported
whole, not diffable, not partially shareable, not hand-editable. It's the thing to not build. It's
also worth noting Visual Studio has since been migrating toward a unified, JSON-backed settings
store with a schema-driven UI and user/solution/folder scopes — i.e. converging on the VS Code
model. Two independent teams arriving at the same design is a reasonably strong signal.

Also worth stealing from VS: the clean split between `.editorconfig` (**shared, committed, team
policy**) and the `.vs/` folder (**personal, machine-local, gitignored**). Same repo, two files,
two audiences, no conflict. See §5.3.

### 3.3 Windows Terminal

The closest structural analogue to AgentDock — a Windows app with a `settings.json`, a settings UI
that round-trips that JSON, `$schema` at the top of the file for editor IntelliSense, and a
`schemes` array of named colour schemes referenced by name from profiles.

Two things worth taking:

- **`profiles.defaults` + `profiles.list`** — a defaults block that applies to every profile, with
  per-profile overrides on top. That's a cascade *within* one file, and it's the right pattern for
  AgentDock's per-project overrides that live inside a workspace file.
- **A viewable read-only `defaults.json`.** Same idea as VS Code's default settings document, same
  payoff.

### 3.4 git config

Worth one line for a single idea: `git config --list --show-origin` prints every effective value
**with the file that supplied it**. Once you have four scopes, "why is this value what it is" is
the most common support question you will get, and this is the cheapest possible answer to it.
§5.7.

### 3.5 Others, briefly

**Zed** — `settings.json` plus `.zed/settings.json` per project, themes as JSON documents. The
modern minimal version of the VS Code model; confirms the shape.
**JetBrains** — partially-committed `.idea/`, separate settings-sync mechanism. Mostly a
cautionary tale about committing a directory whose contents are half config and half state (see
§2.2 — same trap AgentDock is currently in).

---

## 4. The recommendation in one paragraph

**Build one cascading settings system with a single flat dotted-key namespace, one file per scope,
JSONC format, driven by a central settings registry in C# that is the sole source of truth for
defaults, types, descriptions and per-setting scope restrictions.** Cascade
Defaults → User → Workspace → Workspace-local → Project, resolved per key, never destructively
written back. Keep **state out of it entirely** in separate sidecar files. Ship **themes as
separate JSON documents** outside the cascade — selected by a setting, inheriting from a built-in
base via `extends`, with a `theme.colorOverrides` map inside the cascade for one-off tweaks.
Introduce a small **design-token layer** (fonts, sizes, density, radii) exposed as WPF dynamic
resources so the 271 hardcoded font literals can migrate incrementally rather than in one heroic
pass. Restrict anything that names an executable, a path, or a network endpoint to user scope
only, permanently and by design.

The rest of this document is the reasoning behind each of those choices.

---

## 5. The seven decisions that actually matter

### 5.1 One file per scope, or many?

**Recommend: one `settings.json` per scope.** Not `themes.json` + `keybindings.json` +
`ui.json` + `chat.json`.

Multiple files per scope multiplies the merge matrix by the number of files, and users then have
to learn *which* file a setting lives in on top of *which scope*. VS Code splits out only
`keybindings.json` and `tasks.json`, and only because those are arrays of records with genuinely
different merge semantics — not preferences at all. If AgentDock ever grows keybindings, split
those out for the same reason and no other.

### 5.2 Flat dotted keys, or nested objects?

**Recommend: flat dotted keys.** `"chat.markdown.fontFamily": "Cascadia Code"`, not
`{ "chat": { "markdown": { "fontFamily": ... } } }`.

This looks like a cosmetic choice and is actually the load-bearing one. With a flat map, cascade
merging is **one unambiguous rule**: for each key, the lowest scope that defines it wins. Done.
There is no second rule, no special cases, and the implementation is a dictionary overlay.

With nested objects you must answer, for every node in the tree, whether a lower scope *replaces*
that node or *merges into* it — and the correct answer differs per node. Does a workspace setting
`{"usage": {"visible": true}}` wipe out the user's `{"usage": {"order": [...], "orientation": "vertical"}}`,
or merge with it? Both answers are defensible, which is exactly the problem: users can't predict
it, and you'll be explaining it forever. Deep-merge semantics are where cascading config systems
go to die.

Two deliberate exceptions, both borrowed:

- **Maps keyed by identity** merge per key, because the key *is* the identity:
  `theme.colorOverrides`, `usage.items`. This is well-defined precisely because it's a flat map
  one level down, not an arbitrary tree.
- **Scoped override blocks** — VS Code's `"[markdown]": {...}` pattern. If AgentDock ever needs
  "these settings but only for this theme / this project type", use this shape rather than
  nesting the whole namespace.

### 5.3 The scope ladder

**Recommend five layers**, highest precedence last:

| # | Layer | Location | Committed? | Purpose |
|---|---|---|---|---|
| 1 | **Defaults** | in-assembly, from the registry | n/a | every setting's default; viewable read-only |
| 2 | **User** | `%LOCALAPPDATA%/AgentDock/settings.json` | no | "how I like AgentDock" |
| 3 | **Workspace** | inside/next to the `*.agentdock` file | usually yes | "how this workspace is set up" |
| 4 | **Workspace-local** | `*.agentdock.local.json`, gitignored | **no** | personal overrides for a shared workspace |
| 5 | **Project** | `<project>/.agentdock/settings.json` | usually yes | "how this repo wants to look" |

Layer 4 is the one that isn't in the original request, and it's the one that prevents the most
future pain. The moment a workspace file is shared — committed to a repo, handed to a teammate —
every personal preference someone sets in it becomes a diff they must not commit. Claude Code
solved this with `settings.json` / `settings.local.json`; VS Code with the `.vs/` vs
`.editorconfig` split. It's a cheap layer that makes layer 3 genuinely shareable.

Two refinements:

- **An explicit `"root": true` sentinel** per `.editorconfig`, so a project or workspace can
  declare "resolve from defaults, ignore everything above me". You will want this the first time
  a project needs to be immune to a workspace's opinions.
- **Project scope should be reachable in two ways** — a `.agentdock/settings.json` inside the
  repo (travels with the repo, committed, shared) *and* a per-project override block inside the
  workspace file (Windows Terminal's `profiles.defaults` + per-profile pattern), for when you
  want to restyle a project *in this workspace only* without touching its repo. These are
  different intents and both are legitimate; the workspace-level block should win, since it's
  more specific to the current session.

**Non-destructive resolution is the rule.** Reading the effective value must never write
anywhere. The bugs at §2.1 both stem from resolution and persistence being the same code path.
Separating "effective value" (computed, read-only, cascaded) from "stored value at scope N"
(explicit user action, one file) eliminates that entire bug class.

### 5.4 Which settings must be scope-restricted — the security decision

**This is the finding to act on regardless of what else gets built.**

`<project>/.agentdock/settings.json` lives **inside the project repo**. It arrives with
`git clone`. AgentDock's own copy of it is a tracked file in this very repository. So any setting
readable at project scope is a setting an **arbitrary repo author controls** the moment someone
opens that folder in AgentDock.

Today this is benign: project settings hold an icon, a description, sound toggles, a todo list, an
account id. Nothing dangerous. **But `ClaudePath` is already an app setting** — a full override of
the Claude executable path (`MainWindow.xaml.cs:161-163`, `ClaudeSession.cs:31`). If a
"configure everything, everywhere" cascade sweeps that key into the project scope without a scope
restriction, then cloning a hostile repo and opening it in AgentDock silently executes an attacker-chosen
binary with the user's privileges, with no prompt. That is the exact CVE class that forced VS Code
to build Workspace Trust.

**Recommend: bake per-setting scope restriction into the registry from day one**, before there
are enough settings for an exception to slip through. Each setting declares its maximum
permissible scope:

| Restriction | Meaning | Examples |
|---|---|---|
| `user-only` | ignored (with a warning) if seen at workspace/project scope | `claude.path`, `updates.channel`, account config, telemetry, anything naming an executable, filesystem path outside the project, or network endpoint |
| `workspace` | user + workspace, not project | toolbar position, group-bar layout, window chrome |
| `project` | settable anywhere | colours, fonts, sizes, sounds, icons, per-project cosmetics |

Two supporting notes. First, the mechanism must be **allowlist-shaped**: a new setting defaults to
the *most* restrictive scope until explicitly widened, so forgetting to think about it fails safe.
Second, a lower-scope file attempting a restricted key should be **visibly reported**, not
silently dropped — otherwise a legitimate project author gets mysterious no-ops. VS Code shows
these greyed with an explanatory hover; the same information in the settings UI, plus a log line,
is enough.

If the appetite is there later, full Workspace Trust (prompt on first open of an unknown folder,
restricted mode until trusted) is the complete answer. The scope allowlist is 5% of the work and
covers the realistic threat.

### 5.5 Arrays, ordering, and "let me reorder the usage popup"

The usage-popup request is the interesting one architecturally, because it's the case where the
flat-key rule needs help. "Which items show, in what order, laid out horizontally" is a list, and
**lists are where cascades get ugly**: if user scope says `[A, B, C]` and workspace says `[B]`,
did the workspace mean "only B" or "B first, then the rest"?

**Recommend two rules:**

1. **Arrays replace wholesale. Never merge.** Simple, predictable, matches VS Code. If a lower
   scope specifies an array, that array *is* the value.
2. **Where per-item customisation matters, model it as a map keyed by item id, not an array** —
   because maps merge cleanly per key (§5.2), so a workspace can adjust one item without
   restating the list.

For the usage popup concretely, given `UsageSummary` exposes `five_hour`, `seven_day`,
`seven_day_opus`, `seven_day_sonnet`, `extra_usage`:

```jsonc
{
  "usage.popup.orientation": "vertical",        // vertical | horizontal
  "usage.popup.items": {                        // map — merges per key across scopes
    "fiveHour":      { "visible": true,  "order": 10 },
    "sevenDay":      { "visible": true,  "order": 20 },
    "sevenDayOpus":  { "visible": false },      // hides one item; other keys untouched
    "sevenDaySonnet":{ "visible": true,  "order": 30 },
    "extraUsage":    { "visible": true,  "order": 40 }
  },
  "titleBar.showTotalCost":   true,
  "titleBar.showTokenCounts": false
}
```

Sparse integer `order` values (10, 20, 30) rather than array position: a lower scope can insert at
15 without restating anything. Unknown ids are ignored rather than fatal, so a settings file
written against a newer version degrades gracefully — non-negotiable once these files are
committed to repos and shared across versions.

One caveat on scope for this specific case: `UsageDetailPanel` is populated in code, and the title
bar is a fixed XAML `Grid` with hardcoded column indices (`MainWindow.xaml:205-285` — the usage
button is `Grid.Column="5"`, total cost `Grid.Column="6"`). Making elements optional and reorderable
means those regions need to become generated from a list rather than hand-placed. That's a real
refactor of those two regions, not a config read. Worth knowing before it's assumed cheap.

### 5.6 Separate configuration from state — hard line

**Recommend three distinct file classes, never mixed:**

| Class | Contents | Human-edited? | Committed? |
|---|---|---|---|
| **Settings** | preferences, cascading, from the registry | yes — it's a document | often |
| **State** | window bounds, docking layout XML, active tab, MRU lists, last-used account | no | never |
| **Data** | todo items, project description | via the app UI, sometimes by hand | usually |

Current placements to revisit under this rule: `TodoItems` and `Description` in `ProjectSettings`
are **data**; `ClaudeAccountId` is **state**; `DockingLayout` and `ActiveProjectPath` in
`WorkspaceFile` are **state**; the recent-workspaces list in app settings is **state**.

The payoff is that the settings file stops changing when you merely *use* the application. That's
what makes it reviewable, diffable, hand-editable, and safe to commit — and it's the difference
between a config system people adopt and one they resent.

### 5.7 A settings registry as the single source of truth

**Recommend: one declarative registry in C#, from which everything else is derived.** This is the
decision that determines whether 250 settings is maintainable or a swamp. Sketch:

```csharp
Setting.Define("chat.markdown.fontFamily")
    .Type(SettingType.FontFamily)
    .Default("Cascadia Code, Consolas, Courier New")
    .Scope(SettingScope.Project)          // settable anywhere
    .Category("Chat", "Markdown")
    .Description("Font used for rendered markdown in the chat panel.")
    .LiveApply();                          // no restart required
```

From that single declaration, generate:

1. **Effective-value resolution** with correct types and defaults
2. **The settings UI** — categories, controls by type, search, "modified" filter, reset-to-default,
   per-scope tabs. A settings dialog hand-built for 250 settings is not maintainable; the current
   three dialogs (`AppSettingsDialog`, `WorkspaceSettingsDialog`, `ProjectSettingsDialog`) are
   already hand-built XAML and will not survive a 10× increase.
3. **A JSON Schema** shipped with the app, referenced via `$schema` at the top of every settings
   file, so VS Code gives completion, hover docs and validation while editing them. Free
   discoverability, and it's how users will actually explore 250 settings.
4. **The read-only defaults document** — VS Code's and Windows Terminal's most effective
   discoverability feature.
5. **The reference docs page**, generated so it can't drift.
6. **The provenance view** — per §3.4, show each effective value with the file that supplied it.
   With five scopes this will pay for itself within a week.

The rule that makes it work: **a setting that isn't in the registry doesn't exist.** No
`AppSettings.GetString("SomeKey")` call sites scattered through the app. One registry, one lookup
path, and adding a setting is one declaration plus one read.

---

## 6. Themes, in light of all that

### 6.1 The core recommendation: themes are documents, not settings

Keep the VS Code split exactly:

- **A theme is a file.** `%LOCALAPPDATA%/AgentDock/themes/<name>.json`, a document the user
  authors, shares, and versions independently of their preferences.
- **A setting selects it.** `"theme.id": "My Theme"` — cascades normally, so app/workspace/project
  theme selection falls out of the cascade for free, and the §2.1 write-back bug disappears.
- **A settings map overrides individual keys.** `theme.colorOverrides`, optionally scoped per
  theme, so nudging one colour doesn't require forking 140 of them.

Why not put the whole colour map in the cascade? Because a theme is a **coherent artistic
whole** — 140 values that only make sense together, that people want to share as one unit, name,
and switch between. Merging half of one theme with half of another produces noise. Settings are
independent knobs; themes are compositions. Different lifecycle, different file.

### 6.2 JSON, not XAML — and this one is a security decision too

The obvious implementation is "let users drop a `.xaml` `ResourceDictionary` in a folder", since
that's what the built-in themes already are and `ThemeManager` already loads dictionaries by URI.
**Recommend against it.**

Loading XAML from an untrusted file is loading **markup that instantiates arbitrary CLR types**.
`XamlReader.Load` on hostile input is a well-known code-execution vector — the markup can
construct objects, invoke property setters, and reach types like `ObjectDataProvider` to call
methods. A theme, meanwhile, needs to express exactly one thing: 140 name→colour pairs. Handing
over a general-purpose object-graph deserialiser to express a colour map is an enormous and
entirely unnecessary attack surface, and themes are *precisely* the kind of file people download
from strangers on the internet.

**Recommend: a JSON theme document, parsed and validated, from which AgentDock constructs a
`ResourceDictionary` of `SolidColorBrush` objects in code** and hands it to the existing
`ApplyTheme` path. Values are validated as colours; anything else is a parse error. This
sidesteps the entire problem, and — because the theme contract is already exactly 140 flat
colour keys (§2.5) — costs almost nothing in expressiveness.

### 6.3 Shape of the theme file

```jsonc
{
  "$schema": "https://agentdock.dev/schemas/theme-1.json",
  "name": "Nord Dock",
  "author": "Steven",
  "extends": "Obsidian",          // inherit all 140 keys, override what you name
  "baseVariant": "dark",          // required — drives AvalonDock + syntax highlighting
  "colors": {
    "TabButtonActiveBorderBrush": "#88C0D0",
    "ProjectTabActiveBorderBrush": "#B48EAD",
    "ContentBackground": "#2E3440"
  }
}
```

Four details that matter, each grounded in the existing code:

**`extends` is essential, not a nicety.** Requiring all 140 keys means nobody authors a theme.
`GetBrush` currently returns `Brushes.Magenta` for a missing key (`ThemeManager.cs:88-93`) — a
good deliberate choice for catching developer mistakes, and a terrible experience for a user whose
40-key theme renders 100 magenta surfaces. With `extends`, partial themes are the normal case and
the magenta fallback only fires on genuine typos. Consider keeping magenta only in debug builds
and falling back to the base theme's value in release.

**`baseVariant` must be in the file.** It isn't cosmetic — `ThemeManager` uses it to pick the
AvalonDock theme (`Vs2013DarkTheme` / `Vs2013LightTheme`, `MainWindow.xaml.cs:2388-2390`) and the
syntax-highlighting set (`VS2019_Dark` / `Light`, `ThemeManager.cs:70-71`). A user theme without
it can't be applied correctly.

**Validation and reporting.** Unknown keys → warn and ignore (forward compatibility). Malformed
colours → clear error naming the key and line. Never fail to a half-applied theme; either the
theme applies or the previous one stays.

**Live reload.** A `FileSystemWatcher` on the themes folder, re-applying on save, makes theme
authoring pleasant instead of a restart-driven slog. Since everything resolves through
`DynamicResource` / `GetBrush`, this mostly already works — `ApplyTheme` swapping the merged
dictionary is the whole mechanism, and `OnThemeChanged` already handles the code-side rebuild
(`MainWindow.xaml.cs:4107+`).

### 6.4 The 140 keys need to become a documented contract

The real work in user themes isn't loading — it's that **140 keys named things like
`TabIconInactiveDiamondForeground` are undiscoverable**. Nobody can author a theme against them
today. Needed: a generated reference (key, what it paints, which base themes' values, a swatch),
the JSON schema for editor completion, and ideally an in-app preview. Same registry principle as
§5.7: generate the docs from the key list so they can't drift.

This is also the moment to **audit the key set for gaps** — the recent tab work needed a new
`ProjectTabActiveBorderBrush`, which suggests the existing 140 have organic holes. Better to find
them before third parties are authoring against the contract, because after that it's a
compatibility surface.

### 6.5 Typography and layout: the missing token layer

`theme.colorOverrides` handles colour. It does **not** handle "the markdown font", because §2.6:
there is no token for it. That needs a small, deliberately-scoped set of design tokens exposed as
dynamic resources so both settings and themes can drive them:

```jsonc
{
  "font.ui.family": "Segoe UI",
  "font.ui.size": 12,
  "font.code.family": "Cascadia Code, Consolas, Courier New",
  "font.code.size": 12,
  "font.chat.markdown.family": "Segoe UI",
  "font.chat.markdown.size": 13,
  "ui.density": "comfortable",   // compact | comfortable — drives tab height, paddings
  "ui.cornerRadius": 5
}
```

**Recommend: start deliberately small — about 8–12 tokens, not 271.** The 228 XAML font literals
migrate opportunistically, whenever a region is being touched for other reasons. A big-bang
conversion of every literal is high-risk, low-reward, and produces a visual regression surface
nobody can review. It should be explicit that this is a multi-release migration, and that most
literals will stay literals for a long time — because most of them are things nobody wants to
configure.

Whether tokens live in the theme file, the settings cascade, or both is a genuine open question
(§9). My inclination: **tokens are settings** (independent knobs, per §6.1), with themes permitted
to *suggest* defaults for them — since a theme like Parchment plausibly wants a serif UI font, but
the user's explicit setting must still win.

---

## 7. What "configure absolutely everything" actually costs

Worth stating plainly, because the cascade is the cheap part and the ambition is where the cost is.

**Cheap** (days, mostly mechanical): the cascade resolver itself; the registry; migrating the
existing ~25 settings; JSON theme loading with `extends` and live reload — the colour contract
already exists and is already uniform.

**Moderate** (weeks): the generated settings UI; the JSON schema and generated docs; provenance
view; splitting state/data out of the current three stores with migrations; replacing
`AppSettings`' read-whole-file-per-get with an in-memory model.

**Expensive, and the real cost** (open-ended, per surface): every individual thing someone wants
configurable. Reordering the usage popup means the popup builds from a list instead of a fixed
`StackPanel`. Moving the group `+` to the left means `RefreshMetaTabBar` places it by
configuration rather than always appending (`MainWindow.xaml.cs:1602`). Configurable title-bar
contents means that fixed `Grid` with hardcoded column indices becomes dynamic. Configurable
markdown fonts mean `MarkdownHelper`'s hardcoded families read tokens. **Each is small. There are
hundreds. That's the whole story of the ambition.**

The honest framing: the cascade and the registry are **infrastructure that makes each of those
individually cheap and consistent** — worth building for that reason alone. But "configure
absolutely everything" isn't a project with an end date; it's a property the codebase acquires
gradually, one surface at a time, once the infrastructure exists. The right measure of success is
*"could I make the next thing configurable in an hour?"*, not *"is everything configurable yet?"*.

A useful discipline borrowed from VS Code: **every setting is a permanent compatibility
commitment**, and each one is a combinatorial explosion in the state space you support. VS Code's
own guidance to extension authors is to add settings reluctantly. Being able to add settings
cheaply makes it *more* important to be choosy, not less.

---

## 8. Risks and things worth deliberately not doing

**Comment-preserving writes are harder than they look.** If settings files are JSONC that users
annotate, and the settings UI also writes to them, `System.Text.Json` round-trips will destroy
comments and formatting. Options: surgical text edits (what VS Code does, real work), a
JSONC-preserving editor library, or accepting that UI writes reformat. Recommend surgical edits
for the **user** scope at minimum, since that's the file people hand-tune most.

**Don't build a settings *language*.** No expressions, conditionals, variable interpolation, or
computed values. Every config system is under pressure to grow these and every one that yielded
regrets it. Static values, plus the scoped-override-block pattern (§5.2) where conditionality is
genuinely needed.

**Don't let the cascade reach into per-key theme colours across scopes.** `theme.colorOverrides`
merging per key is fine and useful. Whole themes cascading *and* being partially overridden at
four levels each produces a colour whose origin nobody can determine. Cap it: one selected theme,
plus one merged override map.

**Don't migrate all 271 font literals at once** (§6.5).

**Don't ship user themes as XAML** (§6.2), including as a "power user" escape hatch — the escape
hatch is the vulnerability.

**Watch out for perf on hot paths.** Settings resolution will end up inside layout and paint
code. Resolution must be an in-memory dictionary lookup with the cascade **pre-flattened on
change**, not walked per read. The current `AppSettings` pattern of re-reading the file per get
(§2.4) must not survive into the new system.

---

## 9. Open questions worth your opinion

1. **Do tokens (fonts/sizes/density) belong to themes, settings, or both?** My inclination is
   settings, with themes suggesting defaults (§6.5) — but a theme author would reasonably expect
   Parchment to ship its serif font as part of the theme's identity.
2. **Should project-scope settings be able to affect *global* chrome** (title bar, group bar), or
   only that project's own surfaces? A repo restyling your whole window on open is arguably
   hostile even when benign; VS Code's `window` vs `resource` scope split exists for exactly this.
3. **How much should a workspace be able to dictate to its projects?** Currently a workspace file
   holds per-project data; if it can also hold per-project *settings*, the ladder gains a rung
   (§5.3) and the precedence between "workspace's opinion about project X" and "project X's own
   `.agentdock/settings.json`" needs deciding. My inclination: the workspace wins, as the more
   specific context.
4. **Is `.agentdock/settings.json` inside a repo the right home for project config at all?** It's
   committed and shared, which is often what you want for icons and colours — but it's also the
   attack surface in §5.4 and the source of team churn in §2.2. A per-project block inside the
   workspace file has neither problem, at the cost of not travelling with the repo.
5. **Keybindings — in scope?** Not mentioned, but it's the other big configurable surface in every
   tool like this, and it's the one thing that genuinely warrants its own file (§5.1). Worth
   knowing now whether to leave room for it.
6. **Settings sync across machines?** Not asked for, but it strongly influences where the boundary
   between "settings" and "state" gets drawn (§5.6) — sync wants settings and refuses state.

---

## 10. Summary

- There are already four config stores, an informal two-level cascade, and **two live
  write-back bugs** where opening a workspace silently overwrites your app-level theme and toolbar
  position (§2.1). A real cascade is worth building primarily because it makes that class of bug
  structurally impossible.
- **Copy VS Code's model**, which two other vendors have independently converged on: flat dotted
  keys, JSONC, one file per scope, a schema-driven registry as single source of truth, and
  per-setting scope restrictions.
- **The security finding is the one to act on regardless**: `.agentdock/settings.json` lives inside
  the repo and arrives with `git clone`. `ClaudePath` already exists as a setting. Cascading it to
  project scope without a scope allowlist creates a clone-and-open code-execution path — the exact
  CVE class that forced VS Code to build Workspace Trust. Build the allowlist before there are
  enough settings for an exception to slip through (§5.4).
- **Separate settings from state** and stop committing churn (§5.6).
- **Themes are documents, not settings** — a JSON file with `extends` and `baseVariant`, selected
  by a cascading `theme.id`, with a `theme.colorOverrides` map for one-off tweaks. **JSON, not
  XAML**, for security reasons that also happen to cost nothing here (§6.2).
- **User themes are much closer than they look**: all six themes already define an identical
  140-key flat colour contract, consumption is already uniform through `DynamicResource`/
  `GetBrush`, and `ApplyTheme` already hot-swaps dictionaries. The missing pieces are a loader, an
  `extends` mechanism, and — the actual work — **documenting the 140 keys** so anyone can author
  against them (§6.4).
- **"Configure everything" is not a project, it's a property** the codebase acquires one surface
  at a time. The cascade and registry are what make each surface cheap; 228 hardcoded font
  literals are what make the ambition open-ended. Build the token layer small and migrate
  opportunistically (§6.5, §7).
