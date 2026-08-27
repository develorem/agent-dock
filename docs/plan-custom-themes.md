# Implementation Plan — Custom User Themes

**Status:** Plan, not executed.
**Background:** [`configuration-architecture-report.md`](configuration-architecture-report.md) §6,
[`configuration-scope-catalogue.md`](configuration-scope-catalogue.md) §6.
**Depends on:** nothing. Deliberately executable **before** the configuration cascade plan.

---

## Goal

Users can author, share, install and edit themes. A theme is a single JSON file dropped into a
themes folder, detected at startup, selectable in Settings, and editable in a built-in builder
with a colour picker and live preview.

## Non-goals for this plan

- Typography in themes (fonts/sizes). **Blocked** — there is no token layer to bind to yet; see
  the configuration plan, Phase 5. Themes stay colour-only until that exists.
- Changing where theme *selection* is stored, or the settings cascade. Selection keeps using
  `AppSettings` exactly as today.
- Theme marketplace, remote install, or auto-update of themes.
- Syntax-highlighting themes beyond the existing dark/light switch.

## Decisions already taken (do not re-litigate)

1. Themes are **documents**, not settings. They never cascade. Selection cascades; definitions
   don't.
2. **JSON, not XAML.** Loading untrusted XAML instantiates arbitrary CLR types; themes are exactly
   the file type people download from strangers. A validated colour map loses nothing.
3. `extends` inheritance from a built-in base, so partial themes are the normal case.
4. `baseVariant` (`dark`/`light`) is **required** in every theme file.
5. Theme selection is settable at user + workspace scope, **never** project scope.
6. Application is all-or-nothing: a bad theme reports the offending key and leaves the current
   theme in place.

## Current state (verified)

- 6 themes as compiled XAML `ResourceDictionary` files in `src/AgentDock/Themes/`.
- **Exactly 140 keys in each, key sets byte-identical across all six.** Every value is a
  `SolidColorBrush`.
- Consumption is uniform: 105 distinct `DynamicResource` keys in XAML, 39 distinct
  `ThemeManager.GetBrush(...)` keys in C#.
- `ThemeManager.ApplyTheme` already swaps the merged dictionary at runtime
  (`ThemeManager.cs:60-67`), so live repaint already works.
- `ThemeRegistry` is a hardcoded `List<ThemeDescriptor>`; `ThemeDescriptor` is
  `(Id, DisplayName, BaseVariant, ResourcePath)`.
- `SharedStyles.xaml` (13 keys) and `MarkdownStyles.xaml` (1 key) load once and reference theme
  keys via `DynamicResource` — unaffected by any of this.
- `BaseVariant` drives two non-colour things: the AvalonDock theme
  (`MainWindow.xaml.cs:2388-2390`) and the syntax highlighting set (`ThemeManager.cs:70-71`).
- `GetBrush` falls back to `Brushes.Magenta` for missing keys (`ThemeManager.cs:88-93`).
- Theme pickers exist in `AppSettingsDialog` and `WorkspaceSettingsDialog`, both bound to
  `ThemeRegistry.All` displaying `DisplayName`.

---

## Phase 1 — One loading path: built-in themes become JSON

**Why first.** `extends: "Obsidian"` must read Obsidian's 140 colours. If built-ins stay XAML and
user themes are JSON, you need two readers and a way to extract colours out of a compiled
`ResourceDictionary`. Converting built-ins to the same JSON format collapses this to one code path
before any new feature is added.

**Work**

1. Add `Models/ThemeDefinition.cs`:
   ```csharp
   public class ThemeDefinition {
       public string Name { get; set; } = "";
       public string? Author { get; set; }
       public string? Extends { get; set; }
       public string BaseVariant { get; set; } = "dark";   // dark | light
       public Dictionary<string, string> Colors { get; set; } = [];
   }
   ```
2. Write a **one-off generator** (a script or a throwaway test) that reads each
   `Themes/*Theme.xaml` and emits `Themes/<name>.theme.json` with all 140 keys. Do not hand-type
   840 colour values.
3. Add the 6 JSON files as **embedded resources** in the csproj. Keep the `.xaml` files in place
   until step 5 passes.
4. Add `Services/ThemeLoader.cs`:
   - `ThemeDefinition Parse(string json)` — `System.Text.Json`, case-insensitive.
   - `ResourceDictionary Build(ThemeDefinition def, ThemeDefinition? baseDef)` — resolve `extends`,
     overlay `Colors`, produce a `ResourceDictionary` of `SolidColorBrush` keyed by name.
   - Colour parsing: accept `#RRGGBB` and `#AARRGGBB` via `ColorConverter`; anything else is an
     error naming the key.
5. **Golden test** (`tests/`): for each of the 6 themes, load the old XAML dictionary and the new
   JSON-built dictionary and assert identical key sets **and** identical `Color` values per key.
   This is the safety net for the whole phase.
6. Point `ThemeManager.ApplyTheme` at `ThemeLoader.Build(...)` instead of
   `new ResourceDictionary { Source = uri }`. `MergedDictionaries` add/remove logic is unchanged.
7. Once the golden test is green, delete the 6 `*Theme.xaml` files and drop `ResourcePath` from
   `ThemeDescriptor`.

**Risks**

- Any theme XAML entry that isn't a plain `SolidColorBrush` (a gradient, a `StaticResource`
  reference) won't survive the conversion. Verified as all-`SolidColorBrush` today, but the
  generator should **fail loudly** on anything else rather than skip it.
- `MarkdownStyles.xaml` / `SharedStyles.xaml` must keep loading exactly as now — they are not
  themes and must not be swept into the conversion.

**Done when:** all 6 built-in themes render identically to before, built from JSON, with the golden
test proving colour-for-colour equivalence. No user-visible change.

---

## Phase 2 — User theme discovery and loading

**Work**

1. Themes folder: `Path.Combine(AppSettings.SettingsDir, "themes")` →
   `%LOCALAPPDATA%/AgentDock/themes/`. Create on first run. Drop a `README.txt` and one
   `example.theme.json` in it — the cheapest possible authoring on-ramp.
2. `ThemeLoader.DiscoverUserThemes()` — scan `*.theme.json` (and plain `*.json`), parse, validate,
   return successes plus a list of failures with file + key + reason.
3. Validation rules:
   - `name` required and non-empty; collides with a built-in id → suffix or reject with a clear
     message (pick one and be consistent).
   - `baseVariant` required, must be `dark` or `light`.
   - `extends` optional; must resolve to a **built-in** theme. Chained user-theme inheritance is
     out of scope — it invites cycles for little benefit.
   - Unknown colour keys → **warn and ignore** (forward compatibility with future versions).
   - Malformed colour value → **error**, theme not offered.
4. Log all failures via `Log.Warn` with file and key, and surface them in the Settings theme picker
   (a "1 theme failed to load — details" affordance). Silent failure here is the single most
   likely source of "my theme doesn't work" reports.
5. `FileSystemWatcher` on the folder, debounced ~300ms: re-discover, and if the changed theme is
   the active one, re-apply. This is what makes hand-authoring and the Phase 5 builder pleasant.

**Risks**

- Watcher fires mid-write and reads a truncated file. Debounce plus retry-once; on failure keep the
  current theme and log.
- Watcher event storms from editors that write-then-rename. Debounce covers it.

**Done when:** a hand-written JSON file in the themes folder appears in the picker, applies
correctly, and re-applies on save without a restart.

---

## Phase 3 — Registry, selection and pickers

**Work**

1. `ThemeRegistry` becomes dynamic: built-ins (embedded) + discovered user themes.
   `All` returns both, tagged with an origin so the UI can label them.
2. Extend `ThemeDescriptor` with `Origin { BuiltIn, User }` and the parsed `ThemeDefinition` (or a
   lazy loader for it). Drop `ResourcePath` (Phase 1 step 7).
3. `Resolve(string?)` gains one case: a saved id naming a **user theme that no longer exists**
   (file deleted, machine changed) → fall back to `Default`, log a warning, and do **not**
   overwrite the stored preference. The user's intent should survive re-adding the file. Keep the
   existing `"Dark"`/`"Light"` back-compat cases.
4. **Isolate theme-selection persistence in one place.** `LoadThemePreference` /
   `SaveThemePreference` (`ThemeManager.cs:116-125`) already are that place — keep all reads and
   writes there so the configuration plan swaps one method body, not scattered call sites.
5. **Fix the write-back bug while here** (`configuration-architecture-report.md` §2.1):
   `ApplyTheme` unconditionally calls `SaveThemePreference`, so opening a workspace permanently
   overwrites the user's app-level theme. Add `persist: true` as a parameter; workspace-driven
   application passes `false`. Small change, removes a real bug, and it's the same edit the
   configuration plan would make later.
6. Update both theme pickers (`AppSettingsDialog`, `WorkspaceSettingsDialog`) to group Built-in vs
   Custom, and add a "Open Themes Folder" button beside them.

**Ship gate — this is the first genuinely shippable point.** Users can author, install, share and
select themes. Everything after this is additive.

---

## Phase 4 — Key metadata and the reference doc

**Why before the builder.** The builder needs to group 140 keys and describe each one. That
metadata is the actual content of the builder UI, and it's also the thing that makes hand-authoring
viable. It is the largest *writing* task in this plan and the smallest *coding* one.

**Work**

1. `Services/ThemeKeys.cs` — one entry per key: `Key`, `Group`, `Description`, and optionally
   `PaintsHint` (where in the UI to look).
2. **Harvest the groups from the existing XAML comment headers** — the theme files are already
   sectioned (`<!-- Toolbar -->`, `<!-- Tab buttons -->`, `<!-- Group (meta) tab bar -->`,
   `<!-- Add button -->`, `<!-- Tab icon -->`, `<!-- File preview -->`, `<!-- Git status -->`,
   `<!-- File explorer -->`, `<!-- Panel placeholder -->`, …). Free, accurate grouping; capture it
   from git history if the files are already deleted by then.
3. Write one plain-English description per key. ~140 short sentences. No way around it, and it's
   the difference between a themable app and a nominally-themable one.
4. **Audit the key set for gaps.** The recent tab work needed a brand-new
   `ProjectTabActiveBorderBrush`, which strongly suggests organic holes. Find them **now** — after
   third parties author against the contract, this becomes a compatibility surface. Look
   specifically for hardcoded `Brushes.*` and inline `#RRGGBB` literals in C# and XAML that should
   be keys.
5. Generate `docs/theme-reference.md` from `ThemeKeys` + the 6 built-in definitions: key,
   description, and the value in each built-in theme. Generated so it cannot drift.
6. Add a `theme-1.json` JSON Schema (enumerating valid colour keys) and reference it via `$schema`
   in the example theme, so hand-authoring in VS Code gets completion and validation.

**Done when:** every one of the 140 keys has a group and a description; the reference doc
generates; the schema gives completion in VS Code.

---

## Phase 5 — Theme builder UI

**Work**

1. New `Windows/ThemeBuilderWindow.xaml`. Layout: left = key groups from `ThemeKeys`; right =
   scrollable list of keys in the selected group, each with swatch + hex field + picker button.
2. Start-from flow: **Duplicate an existing theme** (the way essentially every theme actually gets
   made) or start from `extends: <built-in>` with zero overrides.
3. **Inherited vs overridden** must be visually obvious per key, with a per-key "revert to
   inherited". This is the difference between a usable builder and a confusing one — without it,
   users can't tell what their theme actually contains.
4. **Live preview** — apply on change, debounced. Nearly free: everything resolves through
   `DynamicResource`/`GetBrush` and `ApplyTheme` already hot-swaps. Restore the previously active
   theme on cancel.
5. Colour picker: WPF has no built-in one. Either write a small HSV picker or take a
   permissively-licensed control. Needs hex entry, alpha (some keys use `#AARRGGBB`), and an
   eyedropper only if cheap.
6. Save writes **only keys that differ from `extends`**, so files stay small and readable and
   inherit future improvements to the base.
7. Export = save-as into any folder. Import = copy a file into the themes folder, with validation
   errors shown against the offending key.
8. Entry points: Settings → Appearance → "Edit/New Theme", and an "Edit a copy" affordance when a
   built-in is selected (built-ins are never edited in place).

**Risks**

- Scope creep. Contrast checking, palette generation, and preview-of-arbitrary-screens are all
  tempting and all optional. Ship the picker + live preview first.
- Live preview on every keystroke thrashing `ApplyTheme` across 140 keys — debounce, and prefer
  mutating the single brush in the active dictionary over rebuilding it.

**Done when:** a user can duplicate Obsidian, change colours with a picker, watch the app update
live, save, and hand the resulting file to someone else.

---

## Phase 6 — Fallback and polish

1. **Magenta fallback:** keep `Brushes.Magenta` in debug builds (it catches developer mistakes),
   but in release resolve a missing key from the `extends` base, then from `ThemeRegistry.Default`.
   A user's partial theme must never render magenta.
2. Theme-load failures visible in Settings, not just the log.
3. Confirm `OnThemeChanged` (`MainWindow.xaml.cs:4107+`) covers every code-side brush application
   for a *user* theme switch — it already rebuilds tabs and the meta bar; sweep for anything that
   caches a brush at construction and never refreshes.
4. Document the format in `docs/` (authoring guide: file shape, `extends`, `baseVariant`, where to
   put it, how to share it).

---

## Suggested order and stopping points

| | Phase | Value delivered |
|---|---|---|
| 1 | Built-ins → JSON | none (refactor, golden-test protected) |
| 2 | Discovery + loading | hand-authored themes work |
| 3 | Registry + pickers + write-back fix | **shippable: author, install, share, select** |
| 4 | Key metadata + reference doc | authoring becomes practical for others |
| 5 | Builder UI | **shippable: themes without touching JSON** |
| 6 | Fallback + polish | robustness |

Stopping after 3 is a coherent release. Stopping after 4 is a better one. 5 is the feature people
will actually talk about.

## Forward compatibility with the configuration plan

- Theme selection stays in `AppSettings` via `LoadThemePreference`/`SaveThemePreference` only. The
  configuration plan replaces those two method bodies and nothing else.
- No theme work should touch `WorkspaceFile`, `ProjectSettings`, or `AppSettings`' API shape.
- `theme.colorOverrides` (per-key nudges in the settings cascade) is **not** in this plan. It
  belongs to the configuration plan, and Phase 1's `ThemeLoader.Build` is the natural place to
  overlay it later — one extra overlay step, by design.
- Typography in themes stays out until the token layer exists.
