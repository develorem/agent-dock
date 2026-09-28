# Autopilot

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · [Remote Sessions](remote-sessions.md) · **Autopilot** · [Settings](settings.md)

---

A long task finishes a phase, the agent says it is ready for the next one, and then it stops. Sometimes it stops because a subagent came back and the main turn never noticed. Either way it needs nothing from you except someone to say "go on then" — and overnight there is nobody to say it.

Autopilot says it for you, between times you choose, for sessions you choose.

**Contents**

- [Modes](#modes)
- [Setting it up](#setting-it-up)
- [When a session gets nudged](#when-a-session-gets-nudged)
- [What the nudge says](#what-the-nudge-says)
- [Quota](#quota)
- [When a session parks itself](#when-a-session-parks-itself)
- [Reading it back in the morning](#reading-it-back-in-the-morning)
- [Limits](#limits)

---

## Modes

Agent Dock is always in exactly one of four modes, shown in the title bar and set from **Settings → Set Mode**.

| Mode | What it means |
|------|---------------|
| **Standard** | Ordinary local use. Nothing locked, nothing scheduled. |
| **Server Mode** | [Hosting this machine's sessions](remote-sessions.md) for another Agent Dock. Agent input is locked here. |
| **Autopilot** | Standard, plus a schedule. |
| **Remote** | [Driving another machine's sessions](remote-sessions.md). |

They are mutually exclusive, and choosing one leaves the current one for you — you don't have to stop hosting before you can arm autopilot.

Autopilot and Standard differ only in whether a schedule is armed. **Outside the window, an Autopilot machine behaves exactly like a Standard one**: nothing is disabled, nothing is greyed out, you type as normal. That is why the badge distinguishes the two states:

```
AUTOPILOT · 11:00pm–4:00am     armed, waiting for the window to open
AUTOPILOT · ACTIVE             inside the window, managing sessions
```

Hover the badge for a per-session breakdown. Click it to return to Standard.

**Remote** is the odd one out: it is the only mode that can end without you choosing to leave it, because the link can drop or the host can take back control. Agent Dock re-derives the mode continuously rather than remembering what you last picked, so the badge is never stale.

---

## Setting it up

**Settings → Set Mode → Autopilot…**

| Field | Notes |
|-------|-------|
| **Sessions to manage** | Every open project. Pick the ones with a real backlog — see [When a session parks itself](#when-a-session-parks-itself). A project whose session hasn't started yet can still be selected; it may well be running by the time the window opens |
| **Start** / **Finish** | 24-hour times. A window that runs past midnight — 23:00 to 04:00 — is the ordinary case, and the dialog tells you how long the window you typed actually is |
| **Repeat every day** | On, the window reopens nightly and survives restarting Agent Dock. Off, autopilot returns the machine to Standard once the window closes |
| **Customise the prompt** | Optional. Replaces the message body; the quota line is still added automatically |

Selecting Autopilot again while it's already armed reopens the dialog with your current settings, so adjusting tonight's finish time doesn't mean setting the whole thing up again.

A **repeating** schedule is restored when Agent Dock restarts. A **one-shot** schedule is not — that was a decision about one particular night, and reinstating it on some later launch would arm a mode that writes into live sessions on the strength of a choice you may not remember making.

---

## When a session gets nudged

"Waiting for user input" is not one state, and the difference matters. A session is nudged only when **all** of these hold:

- The turn has finished (`Idle`)
- **No background work is running.** A session reports idle while its subagents are still draining — this is the exact case autopilot exists for, and the exact case where nudging would talk over a turn that hasn't really ended
- **No permission prompt is open.** Autopilot never grants a permission
- **No question is waiting.** Autopilot never answers a design question on your behalf
- **The send queue is empty.** If you've already lined something up, that goes first
- It has been at rest for **60 seconds**. A turn that just finished is the moment someone starts reading it
- It has been at least **2 minutes** since the last nudge to that session

Typing anything into the composer cancels a pending nudge and restarts the 60-second wait. If you wander back to the machine at 1am, you get to finish your sentence.

Nudges go through the same [send queue](ai-chat.md) as everything else, so if you send something at the same instant, yours wins.

---

## What the nudge says

The built-in prompt is deliberately unspecific about *what* to do — it has to work across every session it manages — but very specific about what not to do:

- **Resume before invent.** It's asked to prefer work it already started or explicitly said it would do next, then work implied by what's there, and only then something justified by the repo itself
- **No decisions on your behalf.** No design choices, no product calls, no naming or scope preferences, nothing where it would normally stop and ask
- **Stay in the repository.** No pushing, no pull requests, no deploying, no messaging anyone
- **Leave it walkable.** Work committed, tests passing, a short note of what it did and what it left
- **An escape hatch.** *"If there is genuinely nothing that meets this bar, say so in one line and stop."* Without this, an agent asked to find work will find work — and inventing busywork unattended is worse than an idle session

You can replace the whole body from the dialog. The quota line is always appended, because it comes from live usage rather than from you.

---

## Quota

Agent Dock already knows how much of your plan is left. Autopilot scales what it asks for to match:

| Remaining | What it asks for |
|-----------|------------------|
| Over 50% | A substantial piece of work |
| 20–50% | Something finishable in one sitting; no large refactors |
| 5–20% | Small and self-contained, or stop instead |
| Under 5% | **Nothing is sent at all** |

The floor exists because a turn that runs out of quota mid-edit leaves the tree in a state nobody asked for and nobody is awake to fix.

Two details worth knowing:

**It gates on the worst of the 5-hour and 7-day windows.** The 5-hour figure is the one in the title bar, but it isn't reliably the binding one — 80% of your session left against 2% of your weekly allowance is a real combination, and only reading the first is how autopilot would start a large task it has no room to finish.

**No usage reading means no nudge.** The usage endpoint is rate-limited and can fail — a lapsed token, an HTTP 429, no network. Every one of those produces *no number*, and a missing number must never read as "plenty left". If there's no reading fresher than 10 minutes, autopilot waits.

Quota is per-login, so a project [assigned to an account](accounts.md) is gated on that account's figures.

---

## When a session parks itself

The failure worth guarding against isn't cost — on a fixed plan there's no runaway bill. It's the futile loop: nudge, *"I have nothing left to do"*, nudge, *"I have nothing left to do"*, all night, burning quota your other sessions could have spent.

So autopilot counts **consecutive turns that used no tools at all**. That's what a session saying "nothing to do" looks like from outside. After three in a row, that session parks itself for the rest of the window and the badge tooltip says so.

The count is on *unproductive* nudges, not all of them. **Any turn that does real work resets it to zero**, so a session with a genuine backlog runs all night. A flat ceiling would have punished exactly the session this feature exists for.

Two things un-park a session:

- **You send it anything.** Whatever autopilot concluded stops being true the moment someone gives it new instructions
- **The window reopens tomorrow.** A park is a verdict about a moment, not about a session — overnight the repository gains commits and new failures, so last night's "nothing left to do" says nothing about tonight

There's still some onus on you to pick sessions that have work. Three cheap turns is a small price, but it's not free.

---

## Reading it back in the morning

Every nudge is preceded by a system line in the transcript:

```
⟳ Autopilot — continuing unattended (62% quota left, large task)
```

This is the difference between a transcript you can audit and one you can't. Without it, a nudge is indistinguishable from something you typed before going to bed.

Nudges are also logged, with the project and the budget tier.

---

## Limits

- **Local only.** Autopilot is a Standard-mode feature. It can't be armed while hosting or while connected to a server, and it never writes into a remote session
- **It continues work, it doesn't start it.** A selected project whose session has exited is skipped, not restarted
- **It can't answer anything.** A session parked on a permission prompt or a question stays parked until you answer it — by design
- **One prompt for all managed sessions.** Per-project prompts aren't supported yet
