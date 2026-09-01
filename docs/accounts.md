# Accounts, Usage & Cost

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · **Accounts** · [Remote Sessions](remote-sessions.md) · [Settings](settings.md)

---

Agent Dock can hold **more than one Claude login** and pick which one a session signs in as. That lets you run more than one Claude plan at once across projects, and see each plan's remaining quota separately.

**Contents**

- [Claude Accounts](#claude-accounts)
- [Choosing an account for a session](#choosing-an-account-for-a-session)
- [The usage indicator](#the-usage-indicator)
- [Cost and tokens](#cost-and-tokens)
- [Troubleshooting](#troubleshooting)

---

## Claude Accounts

**Settings → Claude Accounts…**

Each account is a separate Claude login with its own private config directory. Agent Dock points the `claude` subprocess at it, so the accounts never share credentials.

| Button | What it does |
|--------|--------------|
| **Add Account** | Name the account (e.g. *Personal*, *Work*), then complete the sign-in in the terminal that opens |
| **Log In / Re-authenticate** | Signs the selected account in again. Runs the CLI's dedicated login command and goes straight to the browser sign-in |
| **Refresh** | Re-reads login state from disk |
| **Remove** | Forgets the account and its config directory |

The list **refreshes itself when a sign-in finishes** — no need to click Refresh to find out whether it worked. Progress and the result are reported inline in the dialog, and the action buttons disable while a sign-in is in flight.

The login page is **pre-filled with the address that account was last signed in as**. With several accounts the login pages are otherwise indistinguishable, and authenticating the wrong Claude login into the wrong account is an easy mistake to make.

### Signed-out accounts look signed out

An account that has been signed out shows *"signed out (click Log In)"* alongside its email address, in both this dialog and the start-panel picker. The email address alone is not a health signal — it survives a logout.

### Agent Dock does not touch your tokens

Agent Dock is **strictly read-only** with respect to each account's credentials file. The `claude` CLI is the sole owner of the token lifecycle for a config directory. When a stored access token has lapsed, the usage figure just goes quiet until the CLI renews it on next use — a slightly stale usage number is worth far more than a broken login.

---

## Choosing an account for a session

If you have more than one account configured, the AI Chat **start panel** shows a **LOGIN** picker above the two start buttons.

- The account you pick is **remembered against that project**, so the picker reopens on the same login next time
- It is **fixed once the session starts**. To change it, end the session and start a new one

With a single account (or none), the picker is hidden and sessions use the default login.

---

## The usage indicator

The title bar shows your Claude Code plan usage. Click it for the full breakdown.

### The compact figure

| With | Shows |
|------|-------|
| One login | `5h: 24% · resets in 2h 14m` — the 5-hour window and its countdown |
| Several logins | One bracket per login, e.g. `[Default 57%] [Work 23%]`, each tinted with that account's accent colour so it lines up with its group box in the popup |

Other states it can show:

| Text | Meaning |
|------|---------|
| `Session: —` | Not fetched yet |
| `Session not started` | No usage recorded in the current window |
| `Session: sign in to Claude` | No credentials found |
| `Session: auth expired` | The stored access token has lapsed. Send a message and the CLI renews it |
| `Session: offline` | Network error |
| `Session: rate limited` | The server asked us to back off. Agent Dock waits as long as it asked |
| `Session: error` | Server error |

### The detail popup

Click the indicator. Each configured login gets its own group box, tinted with that account's accent colour, showing:

- **5-hour session** — the rolling window, with time remaining
- **7-day weekly**
- **7-day Opus** and **7-day Sonnet** — per-model, when the plan reports them
- Extra usage, if you have it enabled

**Refresh** re-fetches. Accounts are fetched one at a time, and an account that gets rate-limited backs off for as long as the server asks rather than being hammered on the next tick.

Agent Dock only hits the usage endpoint when a session has actually consumed usage since the last fetch; otherwise it just re-renders the countdown every 90 seconds. That's what keeps an idle Agent Dock from collecting HTTP 429s. The figures come from the same endpoint Claude Code's own `/status` uses, so reading them consumes no inference tokens.

---

## Cost and tokens

| Where | Shows |
|-------|-------|
| **Title bar** | Total cost across every open project, plus elapsed session time |
| **AI Chat panel title** | That session's cumulative cost, token counts, and the active model |

Token counts cover input, output, cache reads and cache creation.

Cost is what the agent reports for the turn. It is a running total for the session, not a billing statement.

---

## Troubleshooting

### An account got signed out mid-session

This was a bug in versions before v0.10.0, where Agent Dock's usage indicator performed its own token refresh and raced the CLI's, eventually getting the whole token family revoked. It no longer refreshes tokens at all. If you are on an older version, update.

To recover: **Settings → Claude Accounts… → Log In / Re-authenticate**.

### A turn finished instantly with no answer and no cost

An auth failure arrives from the CLI as a result carrying the reason in its text, with zero tokens and zero cost. Agent Dock now always surfaces that reason — and when the affected account is signed out, the chat says which account it was and points at **Claude Accounts → Log In**.

### The usage indicator is blank

If you have named accounts configured but none of them is signed in yet, the indicator has nothing to show. Sign one in.

### Log In opened a terminal and left it sitting there

The terminal closes itself when the sign-in completes. It stays open only if the login failed, so there is an error left to read.
