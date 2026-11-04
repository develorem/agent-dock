# Decisions

A running, append-only log of decisions made in conversation that are worth remembering later. Not auto-loaded into every session — read on demand (e.g. by the `status-check` subagent) so it doesn't cost tokens on every turn.

---

## 2026-11-04 — Model routing for the Pro account

- Default model for this repo set to Sonnet (`.claude/settings.json`, `"model": "sonnet"`) — the main driver for coding and git work.
- Added a `status-check` subagent (`.claude/agents/status-check.md`) pinned to Haiku, read-only tools only, for "is X done" / "what does docs/Y say" / "what did commit Z do" type questions. It reads the repo fresh — it has no access to prior chat history, only to files, docs, and git.
- Opus stays manual (`/model opus`), used deliberately for hard problems rather than as a default.
- Rationale: the account is a personal Claude Pro plan (aerobicsoup@gmail.com), which has the smallest 5-hour/weekly token-metered quota of the plans in use, shared across all models. Since Opus costs several times more per turn than Sonnet, and Sonnet more than Haiku, routing routine/status work to cheaper models stretches that shared budget further.
