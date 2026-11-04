# Decisions

A running, append-only log of decisions made in conversation that are worth remembering later. Not auto-loaded into every session — read on demand (e.g. by the `status-check` subagent) so it doesn't cost tokens on every turn.

---

## 2026-11-04 — Model routing for the Pro account

- Default model for this repo set to Sonnet (`.claude/settings.json`, `"model": "sonnet"`) — the main driver for coding and git work.
- Added a `status-check` subagent (`.claude/agents/status-check.md`) pinned to Haiku, read-only tools only, for "is X done" / "what does docs/Y say" / "what did commit Z do" type questions. It reads the repo fresh — it has no access to prior chat history, only to files, docs, and git.
- Opus stays manual (`/model opus`), used deliberately for hard problems rather than as a default.
- Rationale: the account is a personal Claude Pro plan (aerobicsoup@gmail.com), which has the smallest 5-hour/weekly token-metered quota of the plans in use, shared across all models. Since Opus costs several times more per turn than Sonnet, and Sonnet more than Haiku, routing routine/status work to cheaper models stretches that shared budget further.

---

## 2026-11-05 — Needs manual verification (not yet tested by a human)

Everything below was implemented and build-verified (`dotnet build`, zero errors) but **not run** — this sandboxed shell can't execute `dotnet test`/`vstest` (they hang indefinitely, with or without the app open in Visual Studio) and `AgentDock.exe` itself was tied up in a VS debug session throughout, so nothing below has been seen working with real eyes yet.

### Browser remote (Server Mode web listener) — commits `15a5184`..`65e355e`

- Start Server Mode and confirm the status strip/tooltip show **both** the native address and a `browser:` URL.
- Open that URL locally (loopback) first: pairing-code login, then confirm the tab list, transcript, and composer populate.
- Open it from a second device on the LAN with "all interfaces" scope. If it fails to bind, confirm the error actually surfaces the `netsh http add urlacl ...` command rather than failing silently.
- Send a message from the browser and confirm it lands in the native window's transcript, and vice versa.
- Trigger an `AskUserQuestion` prompt — confirm the option buttons and the free-text field both work and unblock the agent. This exercises `PostAnswerOp` parsing, which was a corrected assumption while building, not an originally-verified path.
- Trigger a plain tool-permission prompt (e.g. a Bash command) — confirm Allow/Deny both work.
- Watch the tab status dot through a full cycle: flashing blue (working) → flashing green (done, tab not focused) → solid green (after clicking the tab).
- Confirm the changed-files list under the transcript matches git status.
- Confirm "boot on new connection" both directions: a native client connects while a browser is attached (browser should be dropped), and the reverse.
- Confirm the ~35s heartbeat timeout actually frees the slot when a browser just goes dark (lock the phone / kill its Wi-Fi) without closing the tab — and that "Take Back Control" on the host works against a web client the same as a native one.
- Eyeball the dark/light CSS and the theme-toggle button — never visually checked.
- Still genuinely missing, not just unexecuted: `RemoteWebAuthTests.cs`, the boot-on-success integration test, and the combined-listener-lifecycle test called for in the original plan were never written. Worth adding before trusting this long-term, not just smoke-testing it once.

### Account-colored chat panel headers

- Confirm overriding the AvalonDock resource key(s) only recolors the chat panel's header/active-tab bar — not scrollbars, toolbar highlights, or anything else that might share the same key. This was flagged explicitly as unconfirmed when proposing the approach (found via grepping the compiled theme package, not by reading the real template XAML).
- Confirm the color used per project actually matches that account's bracket color on the usage bar.
- Toggle the new setting off and confirm every panel reverts to the plain theme color.
- Switch light/dark theme while the setting is on — confirm the per-account override survives `OnThemeChanged` reassigning `DockingManager.Theme` rather than getting clobbered back to plain blue.
- Two projects on the same account should match; projects on different accounts should differ, in the same order as the usage bar.
- A project whose saved account id no longer resolves (account removed) should fall back gracefully, not throw or show a broken color.
