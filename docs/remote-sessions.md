# Remote Sessions

[Features](features.md) · [Workspace](workspace.md) · [Groups](groups.md) · [Projects](projects.md) · [Panels](project-features.md) · [AI Chat](ai-chat.md) · [Accounts](accounts.md) · **Remote Sessions** · [Settings](settings.md)

---

Agent Dock can host its live sessions for a second copy of Agent Dock running on another machine. The machine that hosts keeps doing all the work — it owns the agent processes, the files and the git repositories. The second machine is a remote screen and keyboard for it.

Typical use: your desktop is mid-task on four projects, and you want to keep driving it from a laptop in another room without leaving anything running twice.

**Contents**

- [The two roles](#the-two-roles)
- [Hosting a machine (server mode)](#hosting-a-machine-server-mode)
- [Connecting from the other machine](#connecting-from-the-other-machine)
- [What the client can see and do](#what-the-client-can-see-and-do)
- [Only one driver at a time](#only-one-driver-at-a-time)
- [Notification sounds](#notification-sounds)
- [Leaving remote mode](#leaving-remote-mode)
- [Security](#security)
- [Troubleshooting](#troubleshooting)

---

## The two roles

| Role | What it means | Where you start it |
|------|---------------|--------------------|
| **Server** (host) | This machine runs the agents and publishes them | **File → Remote → Enter Server Mode…** |
| **Client** (remote) | This machine drives the other one's sessions | **File → Remote → Connect to Server…** |

The two are mutually exclusive. A machine that is hosting cannot connect out, and a machine that is connected cannot host — the menu items disable each other, and trying anyway explains why.

Only the **hosting** machine needs an agent installed and a Claude login. The client needs neither.

---

## Hosting a machine (server mode)

### 1. Start hosting

**File → Remote → Enter Server Mode…**

The dialog asks for three things:

| Field | Notes |
|-------|-------|
| **Port** | Defaults to **7420**. The port is bind-tested before the server starts, not guessed at. If it is taken, the dialog fills in the next free port — press **Start Server** again to use it |
| **This machine's address** | Read-only. The `address:port` to type on the other machine (see [Why an IP address](#why-an-ip-address-and-not-the-machine-name)) |
| **Allow connections from other machines** | On by default. Unchecked, the server only accepts connections from this machine — useful for testing two copies of Agent Dock on one box |

The first time you host you also have to acknowledge what hosting allows. See [Security](#security).

Windows Firewall may prompt the first time you host. Allow it on your private network.

### 2. Read the address and the pairing code

Once hosting, a **status strip** appears along the bottom of the window:

```
Server mode · 192.168.1.42:7420 (all interfaces) · waiting for a client · code   K7M2-QWXP
                                                       [Copy address] [Copy code] [Stop server]
```

- **Copy address** copies `192.168.1.42:7420` — the *Where is the server?* field on the client
- **Copy code** copies the eight-character pairing code — the *Pairing code* field on the client
- Hover the strip to see **every** address this machine can be reached on. A developer machine usually has several (Wi-Fi, Ethernet, Hyper-V, WSL, Docker, VPN); the one shown inline is the best guess, and the rest are one hover away

The pairing code uses an alphabet with no `0`/`O`, `1`/`I`/`L` or `U` — the glyphs people misread copying a code from one screen to another. It is accepted however you type it: with or without the dash, in any case.

### Why an IP address and not the machine name

Agent Dock shows an IPv4 literal rather than the Windows machine name. A machine name only resolves if the client's network is publishing matching NetBIOS or mDNS names, which plenty of home routers, guest networks and VPN links do not. An IP address always resolves.

The client will still accept a machine name, a hostname or a domain if you prefer one and you know it resolves.

### 3. What changes on the hosting machine

While hosting, this machine is a headless worker, and its UI says so:

- **A `SERVER MODE` badge appears in the title bar.** Click it to stop hosting
- **A `Server mode` pill appears next to the Active tab** on the group bar, with a **Stop** button. Server mode also switches the window to the [Active Projects](groups.md#active-projects) view, because that is exactly the set of projects being published — so what you see locally matches what the client sees. (The group bar only exists once a project is open; the title-bar badge is always there)
- **Agent input is disabled** in every chat panel. The composer greys out and a notice explains why. This starts the moment you press Start Server, not when a client connects: the point of server mode is that the keyboard is somewhere else
- **Notification sounds stop.** They play on the client instead — see [Notification sounds](#notification-sounds)
- **The machine is kept awake** while hosting. The display is still free to sleep; only system sleep is held off, because a host laptop suspending mid-session is the most likely way this feature appears to "randomly stop working"

Everything else on the host stays usable: you can browse files, read transcripts, look at git status, change themes, add project folders and start new sessions.

**Permission and question prompts** are a deliberate exception. While no client is connected, they stay answerable on the host — otherwise a blocked turn would be stuck with nobody able to answer it anywhere. Once a client attaches, they lock here and are answered there.

### Which projects get published

Only projects with a **live agent session** are published — the same definition of "active" the [Active Projects](groups.md#active-projects) group uses. A project folder that is open but has no session running is not shipped anywhere.

Starting or ending a session while hosting updates the client's tab set automatically.

---

## Connecting from the other machine

**File → Remote → Connect to Server…**

### 1. Where is the server?

Enter the address and port from the host's status strip.

Connecting happens **before** you are asked for anything secret, so the dialog can first tell you *which machine answered* and show its identity fingerprint. If the current workspace has projects open, you are warned first — connecting closes them.

### 2. Enter the pairing code

Type the eight-character code from the host's status strip and connect.

If the server's certificate fingerprint has changed since you last paired with this machine, the dialog says so prominently. That happens after a reinstall — but it is also what a machine impersonating the server would look like. Only continue if you know why it changed.

### 3. What you get

The client builds a **single group named after the host machine**, holding one tab per live session there. Each chat panel loads that session's existing conversation history.

The layout is yours. It is your window, not a mirror of the host's — rearrange, float and tab the panels however you like.

The window title and the status strip both name the machine you are driving. A remote window that looked identical to a local one would be a way to run the wrong command on the wrong machine.

---

## What the client can see and do

| Panel | Where the data comes from |
|-------|---------------------------|
| **AI Chat** | The host. Your message is sent there, run there, and the response streams back — thinking, tool calls, the answer, cost and token counts |
| **File Explorer** | The host. Children are fetched when you expand a folder, so a large repository is never shipped wholesale |
| **File Preview** | The host. File content is fetched over the wire and syntax-highlighted locally |
| **Git Status** | The host. The client never runs `git` against a path that isn't on its disk |
| **Todo List** | The host |
| **Project Description** | The host |

Slash commands work as normal, with one exception: **`/logs` is not available remotely**, because it opens a folder and the folder is on the other machine. **`/clear`** clears both sides, since the transcript is shared state.

### File transfer

File contents are cached on the client under a version token, so viewing the same file twice does not re-transfer it — the host answers "unchanged" in one small frame. Because the agent is continuously rewriting the repository, invalidations ride the same file watcher that drives git status: files the agent touches are marked stale and re-fetched next time you look at one.

---

## Only one driver at a time

- **One client at a time.** A second connection is refused and told who currently holds it
- **Take back control** on the host's status strip disconnects the client but keeps listening
- **Regenerate Pairing Code** invalidates the session token, which drops a connected client. You are asked to confirm if someone is attached
- **Stop Server is never disabled** — that is how someone who walks back to the host reclaims it

### Disconnections explain themselves

"The server stopped hosting", "The server took back control", "The pairing code was regenerated", a version mismatch and a dropped link are five different messages, not one generic failure.

A dropped connection **reconnects itself** using a token issued at pairing, so a Wi-Fi blip does not ask you to type the code again. The transcript stays on screen and is marked stale rather than cleared — then patched when the link returns.

If the link is gone for good, the remote tabs stay on screen, read-only, with the reason shown. **Disconnect** tears them down.

---

## Notification sounds

The whole point of server mode is that nobody is in the room with the host. So:

- **The hosting machine goes silent** while it is hosting. A chime there reaches nobody who can act on it — and if you happen to be within earshot of both machines, you hear every notification twice
- **The client plays the sounds instead**, because that is where you are
- The client uses **the host's per-project sound settings**, which travel with the project. Turning "Agent waiting for input" off for a project on the host keeps it off when you drive that project remotely

See [Sound notifications](settings.md#sound-notifications) for the individual toggles.

---

## Leaving remote mode

Any of these ends the current role:

| Where | Hosting | Connected |
|-------|---------|-----------|
| Title-bar badge | Stops the server | Disconnects |
| Pill beside the Active tab | Stops the server | Disconnects |
| Status strip button | **Stop server** | **Disconnect** |
| Menu | **File → Remote → Stop Server** | **File → Remote → Disconnect** |

Stopping the server **re-enables input on the host immediately**. It does not stop the agent sessions — stopping hosting is not stopping the work.

---

## Security

Hosting is exactly as powerful as it sounds, which is why the first time you enable it you have to acknowledge it:

> **A paired client can instruct an agent that reads, writes and runs commands on the hosting machine.** If a session is running in dangerous mode, the client can do anything your account can do.

What protects it:

- **TLS with a self-signed certificate**, generated once and kept. The client **pins its fingerprint on first pairing**; a changed fingerprint on a later connect is a prominent warning, not a silent accept
- **The pairing code proves authorization; the pinned certificate proves identity.** The code itself never crosses the wire — the client answers a challenge with a keyed proof
- **Pairing attempts are rate-limited** and lock out after repeated failures, which is what makes an eight-character code sufficient
- **Every file request from a client is containment-checked** against the project root, and size-capped
- **Credentials never leave the host.** The client needs no agent install and no Anthropic login; all accounts and usage stay on the hosting machine

There is deliberately **no "LAN only" bind option**. Reaching the server from another machine requires binding all interfaces; binding one adapter address instead breaks the moment a VPN attaches or DHCP moves the address, and would not restrict anything a firewall rule does not restrict better.

**Prefer a VPN over forwarding the port on your router.** This port is not something to expose to the internet.

---

## Troubleshooting

### The client cannot reach the server

1. Check you used an **address from the host's strip**, not the machine name. Hover the strip for the full list — a machine with Hyper-V, WSL or a VPN has several addresses and only some of them are on your network
2. Check **Allow connections from other machines** was ticked on the host. Unchecked, only that machine can connect
3. Check Windows Firewall on the host allows Agent Dock on your private network
4. Confirm both machines are on the same network — a guest Wi-Fi network usually cannot reach the main one

### "Connecting to nothing" / it times out

Nothing is listening on that address and port. Confirm the host still shows its status strip, and that the port in the strip matches the port you typed.

### The pairing code is rejected

- The code is regenerated every time you start server mode. Re-read it from the host's strip
- After repeated failures the host locks out pairing attempts for a couple of minutes. Wait, then try again
- Case and the dash do not matter. `k7m2qwxp`, `K7M2-QWXP` and `k7m2-QWXP` are all the same code

### The client shows no tabs

Only projects with a **live agent session** are published. Start a session on the host and the client's tab set follows automatically.

### The host's input is still disabled after stopping

Stopping the server re-enables it. If the window still looks locked, check the title bar: a `REMOTE` badge means this machine is a *client* of another one, which is a different mode — disconnect instead.

### The fingerprint warning appeared

The host reinstalled, or its certificate was recreated. If you can explain the change, continue. If you cannot, stop and find out why before entering a code.
