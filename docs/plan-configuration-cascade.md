# Implementation Plan — Cascading Configuration

**Status:** Plan, not executed.
**Background:** [`configuration-architecture-report.md`](configuration-architecture-report.md),
[`configuration-scope-catalogue.md`](configuration-scope-catalogue.md).
**Relationship to the themes plan:** independent. Either can go first. If themes go first, this
plan's Phase 1 absorbs `LoadThemePreference`/`SaveThemePreference` in a one-method change.

---

## Goal

One configuration system: a single flat dotted-key namespace, resolved across six scopes, driven by
a central registry that is the sole source of truth for defaults, types, descriptions and scope
restrictions — and from which the settings UI, JSON schema and docs are generated.

## Non-goals for this plan

- Making everything configurable. This plan builds the *machinery* plus a first slice of surfaces
  (Phase 6). "Configure everything" is a property acquired over releases, not a deliverable.
- Keybindings (warrants its own file and plan; leave namespace room).
- Settings sync across machines.
- Workspace Trust prompts. The scope allowlist (Phase 2) covers the realistic threat at ~5% of the
  cost.

## Decisions already taken (do not re-litigate)

1. **Flat dotted keys**, JSONC, one `settings.json` per scope.
2. Six layers: defaults → user → workspace → group → project-in-workspace → project(repo), plus a
   gitignored `*.local.json` escape valve.
3. **Arrays replace wholesale, never merge.** Per-item customisation is modelled as a map keyed by
   id (maps merge per key).
4. **Per-setting maximum scope** — `user-only` / `window` / `project` — and a new setting defaults
   to `user-only` until deliberately widened.
5. Resolution **never writes**. Effective value and stored value are separate concepts.
6. State and data are **not** settings and live in separate files.
7. The user's `settings.json` is generated **empty** (schema line + comment), never pre-populated
   with defaults.

---

## Phase 1 — Registry and typed store, no behaviour change

The foundation. Reimplements today's 5 keys on the new machinery so the rest of the plan builds on
something proven.

**Work**

1. `Services/Config/Setting.cs` + `SettingsRegistry.cs` — declarative definitions:
   ```csharp
   Setting.Define("toolbar.position")
       .Type(SettingType.Enum, "Top", "Left", "Right", "Bottom")
       .Default("Top")
       .Scope(SettingScope.Window)
       .Category("Appearance")
       .Description("Where the project tab toolbar sits in the main window.");
   ```
2. `SettingsStore` — loads a scope's file **once** into memory, serves reads from the dictionary,
   coalesces writes with a short debounce. This replaces `AppSettings`' read-and-parse-whole-file
   per `GetString` (`AppSettings.cs:25-40`) and rewrite-whole-file per `SetString`, which will not
   survive hundreds of keys read during layout.
3. Typed accessor: `Config.Get<T>("key")`, `Config.Set("key", value, scope)`.
4. Migrate the existing 5 keys — `Theme`, `ToolbarPosition`, `ClaudePath`, `UpdateChannel`,
   recent-workspaces — into registry definitions with the new dotted names, reading legacy key
   names once and rewriting in the new shape.
5. Un-double-encode the accounts registry (`AccountManager.cs:37,54` currently stores serialised
   JSON *inside* a JSON string).
6. **Fix both write-back bugs**, which are the concrete payoff of separating resolution from
   persistence:
   - `MainWindow.xaml.cs:3811` — opening a workspace writes its `ToolbarPosition` into app
     settings, destroying the user's default.
   - `ThemeManager.ApplyTheme` → `SaveThemePreference` — same for theme.
7. Generate the JSON Schema from the registry; write it beside the exe and reference it via
   `$schema` when creating settings files.
8. First-run: create `%LOCALAPPDATA%/AgentDock/settings.json` containing only a comment header and
   `$schema`. **Do not materialise defaults** — that pins them forever and blocks improved defaults
   on upgrade.

**Rule from here on:** a setting that isn't in the registry doesn't exist. No new
`AppSettings.GetString("SomeKey")` call sites.

**Done when:** identical behaviour, all existing settings served through the registry, `AppSettings`
raw API deleted, schema generating.

---

## Phase 2 — The cascade

**Work**

1. `SettingsCascade` — ordered list of `SettingsStore`s; `Resolve(key)` returns the value from the
   deepest layer that defines it, plus **which layer supplied it** (needed for Phase 4's provenance
   view; retrofitting it later is painful).
2. Pre-flatten the effective map on any layer change and serve reads from the flattened dictionary.
   Do not walk the cascade per read — these will land in layout and paint paths.
3. Wire the layers:
   - **user** — `%LOCALAPPDATA%/AgentDock/settings.json`
   - **workspace** — a `settings` block in the `*.agentdock` file (bump `WorkspaceFile.Version`)
   - **group** — `groups[].settings`
   - **project-in-workspace** — `projects[].settings`
   - **project** — `<project>/.agentdock/settings.json`
   - **local** — `*.agentdock.local.json` / `.agentdock/settings.local.json`
4. **Scope enforcement.** A file attempting a key beyond its permitted scope is ignored **and
   visibly reported** — greyed in the UI with a reason, plus a `Log.Warn`. Silent dropping produces
   mystery no-ops for legitimate authors.
5. **`"root": true`** in any file stops resolution climbing above it.
6. Change notification: a `SettingsChanged` event carrying changed keys. Prefer exposing settings
   as WPF resources where they drive visuals, so `DynamicResource` handles updates for free rather
   than every consumer subscribing.
7. `FileSystemWatcher` per layer so external edits apply live.

**Security gate — do not skip.** `<project>/.agentdock/settings.json` is **inside the repo** and
arrives with `git clone`. Before this phase merges, confirm: `claude.path` is `user-only`;
`claude.dangerousMode` is `user-only` (per-project decisions go in a user-scope map keyed by project
path, as VS Code does for trusted folders); nothing naming an executable, an out-of-project path, a
network endpoint or a credential is widened past `user-only`. This is the CVE class that forced VS
Code to build Workspace Trust.

**Done when:** a value set at each of the six layers demonstrably wins in the right order;
restricted keys are refused with a visible reason; external edits apply live.

---

## Phase 3 — Separate state and data from settings

**Work**

1. **State** → a dedicated store, never committed, never in a settings file: window bounds and
   maximised state, active project, active group, recent workspaces MRU, last-used account per
   project, current docking layout.
   - `ClaudeAccountId` moves out of `ProjectSettings`.
   - Recent workspaces moves out of user settings.
   - `DockingLayout` and `ActiveProjectPath` stay in the workspace file but under a clearly-marked
     state section — a workspace legitimately *is* a session document.
2. **Data** → its own files: `TodoItems` → `.agentdock/todo.json`; `Description` →
   `.agentdock/description.md` (bonus: editable anywhere, diffable).
3. Migrations, one-way, on first load, with the old fields read then dropped. `WorkspaceFile`
   already has `Version` so there's a precedent; add the same to `ProjectSettings`.
4. Keep secrets exactly where they are — `AccountManager`'s per-account `.credentials.json` outside
   settings. Never in any settings file at any scope.

**Done when:** using the app (dragging splitters, switching tabs) produces **no** diff in any
settings file.

---

## Phase 4 — Generated settings UI

The current hand-built `AppSettingsDialog` / `WorkspaceSettingsDialog` / `ProjectSettingsDialog` /
`GroupSettingsDialog` will not survive a 10× increase in settings.

**Work**

1. One settings window generated from the registry: categories in a nav list, controls chosen by
   `SettingType`, search over key + description, "modified only" filter, reset-to-default per key.
2. **Per-scope tabs** (User / Workspace / Group / Project) showing which are editable here, with
   restricted keys greyed and explained.
3. **Provenance**: each row shows the effective value and the layer that supplied it — git's
   `--show-origin` idea. With six layers, "why is this value what it is" will be the most common
   question, and this is the cheapest possible answer.
4. **"Open Default Settings"** — a read-only generated document of every default. This is where
   discoverability actually comes from.
5. "Edit in JSON" per scope, opening the real file.
6. Keep the identity-editing dialogs (project name/icon, group name/icon) as they are — those are
   object editors, not settings pages, and they're good.

**JSONC caveat to decide here:** `System.Text.Json` round-trips destroy comments and formatting. For
the **user** scope at minimum, do surgical text edits (write only the changed key, preserve the
rest) — that's the file people hand-tune. Deeper scopes can tolerate reformatting.

**Done when:** every registered setting is reachable, searchable, resettable, and shows where its
value came from.

---

## Phase 5 — The design token layer

**Work**

1. Define **8–12 tokens only**, exposed as application resources so XAML binds them with
   `DynamicResource` and code reads them like theme brushes:
   `font.ui.family`, `font.ui.size`, `font.code.family`, `font.code.size`,
   `font.chat.markdown.family`, `font.chat.markdown.size`, `ui.density`, `ui.cornerRadius`,
   `tabs.height`.
2. Migrate **opportunistically only**. There are 228 hardcoded `FontSize="n"` in XAML and 43 in
   C#; a big-bang conversion is a large visual-regression surface nobody can review. Convert a
   region when it's being touched for other reasons. Most literals will and should stay literals.
3. First real conversions, because they're the asks: `MarkdownHelper.cs:76`
   (`"Cascadia Code, Consolas, Courier New"`) → `font.code.family`; the chat markdown font; the
   existing project-scoped `DescriptionFontSize` re-expressed as
   `font.projectDescription.size`.
4. `ui.density` should drive tab height and paddings together rather than each being set
   individually — one knob, coherent result.

**Then, and only then**, themes may *suggest* typography (resolution: defaults → theme suggestion →
settings cascade, so an explicit user setting always wins). That's a small addition to the themes
plan's `ThemeLoader.Build`.

---

## Phase 6 — First slice of configurable surfaces

Each is small; the point is that the machinery makes them uniform. Ordered by request priority.

1. **Title bar**: `titleBar.showTotalCost`, `showTokenCounts`, `showUsageIndicator`,
   `showWorkspaceName`. Note the title bar is a fixed `Grid` with hardcoded column indices
   (`MainWindow.xaml:205-285`, usage at `Grid.Column="5"`, cost at `"6"`) — making elements
   optional means generating that row from a list, not toggling `Visibility` on fixed columns.
2. **Usage popup**: `usage.popup.orientation` (vertical/horizontal) and `usage.popup.items` as a
   map of `{visible, order}` keyed by `fiveHour` / `sevenDay` / `sevenDayOpus` / `sevenDaySonnet` /
   `extraUsage`. Sparse orders (10, 20, 30) so a scope can insert without restating. Unknown ids
   ignored, not fatal. `UsageDetailPanel` is populated in code, so it builds from the ordered list.
3. **Add-button placement**: `groupBar.addButtonPosition` / `projectBar.addButtonPosition`
   (left / afterTabs / farRight). Touches `RefreshMetaTabBar` (currently always appends) and
   `CreateToolbarAddButton`'s insertion point.
4. **Panel defaults**: `panels.defaultVisible` map plus `panels.<id>.defaultWidth`, keyed by the
   existing `AiChatId` / `FilePreviewId` / `FileExplorerId` / `GitStatusId` /
   `ProjectDescriptionId` / `TodoListId`. **Defaults for a fresh open only** — the live layout stays
   state (Phase 3). A repo may say "this project doesn't use the Todo panel"; it may not overwrite
   the layout you arranged five minutes ago.
5. `groupBar.activeProjectsLimit` (currently the const `ActiveProjectsLimit = 5`),
   `groupBar.showActiveProjectsGroup` promoted from workspace-only to a properly scoped setting.
6. Explorer / git / preview toggles from the catalogue §4.6 — the cheapest wins, all
   `project`-scoped.

---

## Suggested order and stopping points

| | Phase | Value delivered |
|---|---|---|
| 1 | Registry + store + bug fixes | invisible, but the two write-back bugs die |
| 2 | Cascade + scope enforcement | **shippable: real cascade, security boundary in place** |
| 3 | State/data split | settings files stop churning; safe to commit |
| 4 | Generated UI + provenance | **shippable: settings become discoverable** |
| 5 | Token layer | fonts configurable at all |
| 6 | Surface slice | the specific asks land |

Phases 1–2 together are the smallest coherent release. 1–4 is the one worth shipping. 5–6 are
open-ended by nature and should be paced.

## Risks

- **Scope creep in Phase 6.** It has no natural end. Timebox it and measure success as *"could I
  make the next surface configurable in an hour?"*, not *"is everything configurable?"*.
- **Perf.** Settings resolution will end up in hot paths. Pre-flattened dictionary lookups only.
- **Comment preservation** (Phase 4) is genuinely more work than it looks.
- **Every setting is a permanent compatibility commitment** and a widening of the state space you
  support. Making settings cheap to add makes it *more* important to be choosy, not less.
- **Don't build a settings language** — no expressions, interpolation or computed values. Every
  config system is pushed toward these and every one that yielded regrets it.
