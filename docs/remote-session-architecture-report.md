# Remote Session Architecture — Report

**Status:** design plus decisions (§11) and implementation status (§13). The feature is built and verified; see §13 for gaps.
**Date:** 2026-08-28
**Feature:** run Agent Dock on machine B as a thin client onto the live sessions of Agent Dock on
machine A. Machine A ("server") keeps doing all the work — it owns the Claude subprocesses, the
files, the git repos. Machine B ("client") is a remote head.

---

## 1. What the feature actually is

Worth naming precisely, because it changes what's hard:

- It is **not** screen sharing. There is no pixel path. The client renders its own WPF UI.
- It is **not** a distributed agent. Only one machine ever runs `claude`.
- It **is** a *replicated view of session state* plus a *remote input channel*.

That framing gives the two hard problems: (a) getting session state onto a second machine and
keeping it in sync, including for a client that joins mid-turn or reconnects after a blip, and
(b) making the input channel authoritative on exactly one side at a time.

A pleasant consequence of the design: **the client needs no Claude Code install and no Anthropic
login.** All credentials, all accounts, all usage stay on the server. That is a genuine selling
point and it should be stated in the UI, because it is also what makes the security posture
serious (see §8).

---

## 2. Where to cut the architecture

Today the pipeline in one project tab is:

```
claude subprocess ──stdout JSON lines──▶ ClaudeSession ──events──▶ ChatTurnProcessor
                                                                        │
                                                          IReadOnlyList<ChatOp>
                                                                        │
                                                          AiChatControl.ApplyOps (UI thread)
```

Three candidate seams:

| Seam | What crosses the wire | Verdict |
|---|---|---|
| **A. Raw stdout** | Claude's own JSON lines, forwarded verbatim; client runs its own `ChatTurnProcessor` | Tempting (least new code) but the two ends must agree on parsing forever, and control messages (permission grant/deny, question answers) travel *in* that stream, so the client would also need to write back into the server's stdin. Version skew becomes a correctness bug, not a UX bug. |
| **B. ChatOp stream** | The already-classified `ChatOp` records, plus session state / stats / status | **Recommended.** `ChatOps.cs` already documents these as "plain data only — never WPF objects — so they are safe to construct off the UI thread". A design constraint that already exists for thread-crossing is exactly the one needed for wire-crossing. |
| **C. Rendered content** | Finished HTML/FlowDocument per message | Loses interactivity (markdown toggle, expand/collapse, file links) and is bulkier. No. |

So: **cut at the ChatOp boundary.** The server runs `ClaudeSession` + `ChatTurnProcessor` as it does
today and additionally fans the op stream out to the connected client. The client's chat panel
applies ops it receives instead of ops it generated.

### 2.1 Do the right abstractions exist? Partly — and the missing one is not the agent

The instinct is: if there is a service that interacts with the agent and a service that renders
output, remoting is one substitution — the interaction service talks to an RPC endpoint instead of a
local process, and nothing else changes. That is the right target. Measuring the current code
against it gives a more interesting answer than "yes" or "no".

**What is already right.**

1. **The render/interpret split exists and is good.** `ChatTurnProcessor` consumes `ClaudeSession`
   events and emits `ChatOp` records — "plain data only — never WPF objects". Interpretation of the
   agent's output is already separated from rendering it, and the boundary is already
   serialization-shaped. This is why §2 cuts there: the codebase drew that line first.
2. **`ClaudeSession` has no UI dependency.** It is events plus methods over a subprocess. In
   principle it is substitutable.
3. **The wiring topology is already right.** `MainWindow.CreateProjectDockingLayout` is a *single
   composition root* for a project tab. Every panel is constructed there and the panels are
   connected to each other only by events (`FileSelected → ShowFile`, `DiffRequested → ShowDiff`,
   `SessionStateChanged → RefreshStatus`). No panel knows about another panel. That is exactly the
   shape needed for a one-place substitution.

**What is wrong.** Not a missing service — a wrongly-typed dependency. Every panel's initializer
takes the same thing:

```csharp
fileExplorerControl.LoadDirectory(project.FolderPath);   // string
gitStatusControl.LoadRepository(project.FolderPath);     // string
descriptionControl.LoadProject(project.FolderPath);      // string
todoListControl.LoadProject(project.FolderPath);         // string
aiChatControl.Initialize(project.FolderPath);            // string
```

`string projectPath` **is** the leaky abstraction. It is an unqualified local Windows path, and it
silently means "on this machine". Given only a path, each panel has no choice but to become its own
composition root and reach for the local machine directly:

| Panel | Reaches for |
|---|---|
| `AiChatControl` | `new ClaudeSession(_projectPath, …)` (line 389) |
| `GitStatusControl` | `new GitService(projectPath)`, `new FileSystemWatcher(projectPath)` |
| `FilePreviewControl` | `File.Exists`, `File.ReadAllText`, `File.OpenRead` |
| `FileExplorerControl` | `Directory.Exists`, `Process.Start` |
| `TodoListControl`, `ProjectDescriptionControl` | `ProjectSettingsManager.Update(_projectPath, …)` |

So the abstraction that is missing is **not "agent client"** — that one nearly exists. It is **"the
machine this project lives on"**. Remoting only the agent would produce a chat panel driving machine
A next to a file explorer showing machine B's own C: drive.

**The seam to introduce.** One dependency, handed to every panel in place of the path string:

```
IProjectHost                          // where this project physically lives
    string DisplayRoot                 // for titles only — never for I/O
    IAgentSession   Agent              // send, permissions, questions, state, op stream
    IProjectFiles   Files              // list / read / watch, .gitignore-filtered
    IProjectGit     Git                // status, branch, diff
    IProjectConfig  Settings           // the project's .agentdock/settings.json
```

`LocalProjectHost` wraps today's code verbatim. `RemoteProjectHost` issues RPC. `MainWindow` picks
one at the single composition site, and the substitution genuinely is the one you would expect.

This method-shaped sketch is **refined into a state-shaped one in §2.5** — panels should observe
replicas rather than call fetch methods. Read §2.5 as the current form of the interface.

This also surfaces two things that were invisible while the dependency was a string:

- **`ProjectSettingsManager` is project-scoped state, not app state.** Todo items and the description
  font size are written into the *project's* `.agentdock/settings.json`. Remotely they must round-trip
  to the server, or the client silently writes them nowhere. Easy to miss entirely.
- **Some panel actions are inherently local.** "Reveal in Explorer", "open in editor"
  (`Process.Start`), and `GitService.CheckoutBranch` cannot be honoured across the wire. They are not
  host methods; they are capabilities the host either has or does not, and the UI must hide them
  rather than fail on them. `IProjectHost` needs a capability flag, not a method that throws.

**The second structural blocker: statics.** Even with `IProjectHost` handed to every panel, an
implementation cannot be substituted where it is reached through a static. Five panels bypass any
possible seam today:

```
AiChatControl:214,354          ProjectSettingsManager.Load / .Update(_projectPath, …)
FileExplorerControl:408        ProjectSettingsManager.Update(_rootPath, …)
ProjectDescriptionControl:33,85  ProjectSettingsManager.Load / .Update
TodoListControl:26,39          ProjectSettingsManager.Load / .Update
FileExplorerControl:39         public static HashSet<string> AvailableTools
```

`ProjectSettingsManager` is a `public static class` reading and writing
`<project>/.agentdock/settings.json` on the local disk. No amount of constructor wiring changes what
`TodoListControl` does at line 39 — it must become `host.Settings.Update(...)` on an instance the
host owns. Same for `FileExplorerControl.AvailableTools`, which is a static set of *editors installed
on this machine*: remotely the answer belongs to the server (it decides whether "Open in VS Code" is
even meaningful), so it becomes a host capability rather than a global.

`ClaudeSession.ClaudeBinaryPath` is also static, which is correct for a single local machine and
wrong the moment two hosts exist in one process — worth moving behind the local host implementation
at the same time.

### 2.2 The one place "same abstraction, different transport" is a lie

Worth stating because it is the classic distributed-objects trap: designing the interface against the
local implementation and then discovering the remote one cannot honestly satisfy it.

Three properties hold locally and do not hold remotely:

1. **Synchronous state reads.** `ClaudeSession.State` is a property; reading it is free and always
   truthful. Remotely it is a *replica* that can be stale by a round trip. An interface exposing
   `State` as a plain property invites the panel to treat a cached value as authoritative.
2. **Subscribe-from-birth.** A local subscriber attaches at t=0 and sees every event. A remote
   subscriber attaches at t=2h and needs the preceding history. Snapshot-and-replay (§2.3) is
   therefore **not** an artefact of a weak abstraction — it is inherent to remoting a stateful
   stream, and the interface must express it (`Attach()` returns a snapshot plus a sequence position)
   or every consumer will reinvent it badly.
3. **No failure mode between call and callback.** In-process, `SendMessage` cannot fail to arrive.
   Remotely it can, and there is a whole state — *disconnected* — with no local counterpart. If it is
   not in the interface, the panels have nowhere to render it and the UI lies about being connected.

So: the RPC should be **invisible to the panels** but **visible in the shape of the interface** —
async commands, snapshot-capable attach, and an explicit connection state that the local
implementation simply reports as permanently connected. Designed that way, substitution is real.
Designed against the local case first, it is not, and the difference only shows up under a dropped
Wi-Fi connection months later.

### 2.3 Snapshot + delta, not delta alone

An op stream alone cannot serve a client that connects at 11am to a session that started at 9am —
his stated requirement that "all of the previous session information will get loaded into the
history of the panel". So the protocol needs two shapes:

- **`TabSnapshot`** — the complete current state of one project tab: the serialized transcript,
  session state, model, account label, cumulative stats, status label, pending permission request
  (if any), pending send queue and scheduled messages, git status, branch.
- **`TabDelta`** — a batch of ops / state changes, carrying a **monotonic sequence number**.

Client joins → snapshot at seq N → deltas from N+1. Client reconnects at seq M → server replays
M+1.. from a bounded ring buffer if it still has them, else sends a fresh snapshot. Without a
sequence number, reconnects silently duplicate or drop transcript content, and that will look like
a rendering bug months later.

### 2.4 Transcript serialization is not free

The in-memory transcript is *not* currently serializable, and this is the one place the existing
design actively resists remoting:

- `UserMessage.Images` holds `IReadOnlyList<ImageSource>` — a WPF type.
- `AssistantMessage` holds `Func<FlowDocument>? MarkdownBuilder` — a memoizing closure.
- `ActivityMessage.Entries` is a heterogeneous `ObservableCollection<object>` of thinking buffers,
  tool entries, subagent entries and subagent reports.

None of that is a blocker, but it means a parallel DTO layer: a `SerializedChatMessage` union that
each VM can be projected to, and rehydrated from on the client (rebuilding the lazy markdown
closure locally, which is the right place for it anyway — rendering cost belongs to whoever
renders). Images become byte payloads with a content type.

Also worth noting: **there is no transcript persistence today.** History lives only in the
`ObservableCollection<ChatMessageVm>` of a live `AiChatControl`. That is fine for this feature (the
server is running, so the transcript exists) but it means a client that reconnects after a *server
restart* gets whatever `--resume` gives Claude, and an empty Agent Dock transcript. Expected, but
should be a visible message rather than a mysteriously blank panel.

---

### 2.5 Every panel is a replica, not a request — and this fixes local flicker too

The natural instinct for the non-chat panels is request/response RPC: the file explorer asks for a
directory listing, the preview asks for a file body, the git panel asks for status. That is the wrong
mechanism, and the reason is visible in the current code without any network involved.

**The discipline already exists — in exactly one panel.** `GitStatusControl` is a model citizen:

- git work happens off the UI thread (`RefreshCoreAsync`)
- overlapping refreshes coalesce via `_refreshing` / `_refreshQueued` rather than queueing up
- background (inactive) tabs do no work at all
- `BranchName.Text` is assigned only when it actually changed
- the bound collection is **diff-and-patched** by `SyncFileList(target, targetSet)`, never rebuilt

**And `FileExplorerControl` is the counter-example.** `Refresh()` is:

```csharp
var expandedPaths = CollectExpandedPaths();
LoadDirectory(_rootPath);          // FileTree.Items.Clear() + full rebuild
RestoreExpandedState(expandedPaths);
```

Collect-rebuild-restore is the tell: the wholesale rebuild destroys UI state, so the state has to be
reconstructed afterwards. It is wired to `gitStatusControl.FileSystemChanged`, so **the agent editing
files flickers the tree today, locally.** The remote case does not introduce this problem; it makes an
existing one unmissable.

So the abstraction's real job is to make the `GitStatusControl` discipline the *default* rather than
something each panel reinvents or forgets. That means a uniform mechanism:

> **Panels bind to a replica. The server pushes deltas into it. Panels never perform I/O and never
> ask "fetch this now".**

The same shape works locally — the "push" already exists there in the `FileSystemWatcher`. Which is
the real prize: the local implementation exercises the same code path as the remote one, instead of
being a separate, easier case that hides bugs.

That reshapes `IProjectHost` from method-shaped to state-shaped. Panels observe; they do not call:

```
IProjectHost
    IObservable/INPC replicas:  Git status + branch, Todo items, Description,
                               Icon, Project settings, Agent state + transcript
    Lazy pulls (results enter the replica):  directory children, file body, diff
    HostCapabilities:          CanRevealInExplorer, CanOpenInEditor, CanCheckoutBranch
    ConnectionState:           Connected | Stale | Disconnected   (Local: always Connected)
```

#### What the caching layer actually has to do

"Cache it so it doesn't re-query" is right, but four properties do the work:

1. **Lazy and pinned for the tree.** Never replicate a 100k-file repository. Fetch a directory's
   children when it is expanded, then *pin* it; the server watches only materialized directories and
   pushes invalidations for those alone. Unpin on collapse. This keeps the replica proportional to
   what the user is actually looking at.
2. **Version tokens on content.** File bodies and diffs cache under `(path, mtime+size)` or a content
   hash, in an LRU bounded by *bytes*, not entries. Invalidations carry the new version so the client
   can tell whether its copy is still good — that is the mechanism that stops re-querying, rather
   than a timer.
3. **Debounce on the server, before the wire.** A build or a `git checkout` touches thousands of
   files. The watcher storm must be coalesced server-side; pushing it and coalescing on the client
   wastes the link and arrives late anyway.
4. **Stale-but-shown beats empty-then-repopulated.** On reconnect, do **not** clear the replica and
   rebuild. Mark it stale (a subtle visual state at most) and patch it when the snapshot lands. This
   single rule is the difference between a reconnect that is invisible and one that flashes the whole
   window.

#### The hazard that cuts the other way

Caching has a correctness edge here that it would not have in most apps: **the agent is continuously
rewriting the repository.** File bodies and diffs go stale as a matter of routine, not as an edge
case. In a code tool, showing stale content is worse than showing it slowly — a remote user reading a
file Claude rewrote thirty seconds ago, with nothing indicating it, is a genuine way to make a wrong
decision.

Consequences: invalidation must ride the same watcher that drives git status (it already exists and
already fires `FileSystemChanged`); the version token is part of the cache key, not an optimisation;
and a preview showing content known to be superseded should say so rather than wait to be re-fetched.

#### Coverage — it really is every panel

| Panel | What the replica holds | Notes |
|---|---|---|
| AI chat | transcript, session state, stats, queue, pending permission | §2.3 snapshot + ops |
| Git status | `GitFileEntry` list, branch | server pushes on its existing watcher cadence |
| File explorer | children of *materialized* directories only | `.gitignore` filtering stays server-side |
| File preview | LRU content cache keyed by path + version | text, bytes for images, diffs |
| Todo list | todo items | persisted in the **project's** `.agentdock/settings.json` |
| Project description | description text + font size | same file |
| Project icon | resolved icon + image bytes if file-based | see below |

`ResolveProjectIcon` (`MainWindow.xaml.cs:959`) needs care: it loads project settings,
auto-discovers a logo *file inside the project folder*, **persists the discovered choice back**, and
then builds a `UIElement` from either a built-in glyph name or that file path. Remotely the discovery
and the write-back must happen on the server, and the client needs the image bytes — an icon path is
meaningless on a machine that cannot see the folder.

---

## 3. Transport

Requirements: two Agent Dock instances, no browser, LAN or VPN or port-forward, must carry both
small control frames and occasional large payloads (file contents, image attachments), must detect
half-open TCP (Wi-Fi and VPN produce these constantly).

Options considered:

| Option | Pros | Cons |
|---|---|---|
| ASP.NET Core / Kestrel + WebSocket | TLS, auth middleware, routing for free | Requires the **ASP.NET Core shared framework** on the server machine. Today Agent Dock needs only the .NET Desktop Runtime. Adding a second runtime prerequisite to the installer for one feature is a poor trade. |
| `HttpListener` + `AcceptWebSocketAsync` | in-box, no new package | http.sys demands a **URL ACL reservation** (`netsh http add urlacl`) for any non-localhost prefix unless running elevated. A silent "access denied" on first server start for every user is a support nightmare. |
| **`TcpListener` + `SslStream` + length-prefixed frames** | in-box, no ACL, no extra runtime, full control of framing, trivially supports binary payloads | Hand-rolled framing (~small and well-understood); no browser client ever |

**Recommendation: `TcpListener` + `SslStream`, length-prefixed JSON frames** (binary frames for
image/file payloads). Both ends are Agent Dock, so WebSocket's browser-compat framing buys nothing.
If a browser client is ever wanted, `WebSocketProtocol.CreateFromStream` can wrap the same
`SslStream` later without changing the message layer.

Frame layer needs, explicitly:

- **Heartbeat both directions** (e.g. 10s ping, 30s timeout). Without it, a sleeping laptop or a
  dropped VPN leaves both sides believing they are connected — the single most likely field failure.
- **Bounded outbound queue with coalescing.** A heavy turn emits hundreds of text deltas per second.
  The UI already solves this locally by flushing `StreamingText` at ~10fps; the wire should do the
  same: coalesce text-append ops into ~100ms windows, and *never* let a slow client block the
  server's read loop. Structural ops (tool calls, turn complete, state changes, permission requests)
  must never be dropped or reordered; only text appends may be merged.
- **UTC everywhere** on the wire; render in client-local time. Scheduled messages make this real.

---

## 4. Pairing, identity and reconnect

The 8-character code is the right idea but it should be understood as an **authorization** secret,
not an identity mechanism. Both are needed.

Proposed handshake:

1. Client connects; TLS with a **self-signed certificate** the server generates on first server-mode
   start and persists in `%LOCALAPPDATA%/AgentDock`.
2. Server sends `ServerHello`: machine name, app version, **protocol version**, cert fingerprint.
3. Client checks protocol version (refuse loudly on mismatch — do not attempt compatibility), shows
   the machine name and fingerprint, and pins the fingerprint on first pairing (trust-on-first-use,
   as SSH does). A changed fingerprint on a later connect is a warning, not a silent accept.
4. Client prompts for the code, sends a proof of it (HMAC over a server nonce — the code itself need
   not go over the wire even inside TLS).
5. On success the server issues a longer-lived **session token**. Reconnects use the token, so a
   Wi-Fi blip does not re-prompt for the code. Optionally remembered so the next launch can offer
   "reconnect to DESKTOP-ABC".

Code hygiene:
- Alphabet without ambiguous glyphs (no `0/O`, `1/I/l`) — the user will read this off one screen and
  type it on another. Crockford base32 gives 32⁸ ≈ 10¹² combinations.
- Regenerated each time server mode starts, plus a manual "regenerate" button (which invalidates
  existing tokens).
- **Rate limit and lock out** failed attempts (e.g. 5 failures → cooldown), and surface the failures
  on the server UI. Brute force is only theoretical *if* attempts are throttled.

---

## 5. Server mode UX

Menus — remote is a peer of "open workspace", per his framing:

```
File
├── Remote →
│   ├── Start Server…          (dialog: port, bind address)
│   ├── Stop Server
│   └── Connect to Server…     (dialog: host/URL → connect → code prompt)
```

**Port check.** The honest check is to *attempt the bind* — querying
`IPGlobalProperties.GetActiveTcpListeners()` races with anything else starting up. Try to bind; on
`SocketException`/`AddressAlreadyInUse`, report it and offer "find next free port". Naming the
process holding the port is possible but needs shelling out to `netstat -ano`; probably not worth it
for v1.

**Bind address matters.** `127.0.0.1` (useless for this feature but ideal for testing),
LAN interface, or all interfaces. Defaulting to all interfaces is convenient and is also what makes
an accidental internet exposure possible. Suggest defaulting to the LAN and making "all interfaces"
a deliberate choice.

**Server status affordance.** A persistent strip while server mode is on:
`Server mode · DESKTOP-ABC:7420 · code XXXX-XXXX · 1 client connected (192.168.1.40)`
with copy, regenerate, kick-client, and stop. The server user must always be able to see that
someone is attached and sever it in one click.

**Firewall.** The first bind to a non-loopback address triggers the Windows Defender Firewall
prompt, which requires elevation to accept. Options: let it happen and document it, or have the
installer add an inbound rule for the chosen port. Either way it needs to be an explicit decision,
because a declined prompt produces a server that looks started and is unreachable.

**Sleep.** An idle server laptop suspending mid-session is the second most likely field failure.
While server mode is on, the server should hold a power request
(`SetThreadExecutionState`/`PowerCreateRequest`) to keep the machine awake — with the display free to
sleep. This is small and prevents a whole class of "it randomly disconnects" reports.

---

## 6. Server mode is non-interactive — decided

**Entering server mode makes the server's agent interaction fully non-interactive.** Not just the
text box: the input box, send, queue/schedule, the inline permission Allow/Deny panel, and
`AskUserQuestion` answers are all inert on the server. Everything is answered from the remote.

The reasoning is the physical situation: **the human is sitting at the remote machine, not at the
server.** Leaving Allow/Deny live on the server would leave a control nobody is present to click,
and would create two possible answerers for one question.

Two consequences worth stating so they are not mistaken for defects later.

**A session that hits a permission prompt while no client is attached simply waits.** It waits
quietly, because the inactivity watchdog is deliberately gated to `Working` only:

```csharp
if (State == ClaudeSessionState.Working)   // ClaudeSession.cs:942
    InactivityTimeout?.Invoke();
```

In `WaitingForPermission` the timer fires and does nothing. That is correct — a prompt waiting on a
human should not time out — and it means the turn sits until a client attaches and answers it. The
tab already surfaces this state correctly via `TabIndicator.Question`. **This is accepted behaviour,
not a bug to fix.** The escape hatch is that *Stop Server is not agent interaction* and therefore
stays live: a user who returns to the server machine stops server mode and has full control back.

**Permission remoting is therefore core protocol, not an extra.** Because no prompt can ever be
answered locally in server mode, the client must render both the permission panel and the
`AskUserQuestion` panel and be able to answer them. `PermissionRequested` push plus
`AllowPermission` / `DenyPermission` / `AnswerQuestion` commands are load-bearing frames — if they
are missing or broken, every session that needs approval deadlocks. They belong in the first working
version of the protocol, not a later pass.

What stays live on the server: window chrome, Stop Server, the server status strip, theme, and
non-agent navigation. There is no need to make the machine unusable, only to make it a
non-participant in agent conversations.

## 7. What the client sees

### 7.1 Tabs and the group

Per his spec: one group, named after the server machine, containing one tab per **active** session.
`MainWindow.GetActiveProjects()` already defines "active" (a live agent session) for the dynamic
Active Projects group — reuse that definition rather than inventing a second one.

Open behaviours that need deciding:

- **Session starts on the server after the client connected** → push a tab-added frame. Cheap, and
  without it the client goes stale in a confusing way.
- **Session ends on the server** → keep the tab, mark it exited, disable its input. Yanking a panel
  out from under someone who is reading it is worse than a dead tab. Clean it up on next connect.
- **Project closed on the server** → same treatment, with a clear "closed on server" state.
- **Server opens a different workspace while server mode is on** → the whole active set changes.
  Either block workspace switching in server mode, or treat it as a full resync. Resync is more
  honest; blocking is simpler.
- **Can the client start a new session, or end one?** His description is read-and-drive-existing.
  Recommend v1: no. Starting a session needs the server's full project list (not just the active
  set) and raises "as which account?" — a whole extra surface.

### 7.2 Layout

The remote group is not a workspace, so its layout has nowhere to live. Suggest persisting remote
docking layouts per (server identity, project path) in `%LOCALAPPDATA%` app settings, so
reconnecting restores the arrangement. Small cost, and without it every reconnect resets the user's
panels — which will be noticed immediately.

Should remote connect be **exclusive** with a local workspace? His words ("instead of loading a
workspace") say yes, and exclusive is markedly simpler: no ambiguity about which machine a panel
refers to. The alternative — remote group alongside local groups — is more useful but needs every
panel to know its machine, and every path in the UI to be machine-qualified. Recommend exclusive
for v1, revisit later.

### 7.3 File explorer, git status, file preview

These are read-only panels today, which removes a whole category of risk — there is no write path to
secure.

The replication mechanism for these is settled in §2.5 (replica + pushed deltas, never
request/response polling). What remains panel-specific:

- **Explorer:** children fetched on expand and then pinned; `.gitignore` filtering stays server-side
  (reuse `GitIgnoreFilter`). `Refresh()`'s clear-and-rebuild must become an incremental tree patch —
  this is the largest single panel change, and it improves the local app as much as the remote one.
- **Git status:** the server pushes `GitFileEntry` lists and the branch name on its existing watcher
  cadence, debounced. `GitService.CheckoutBranch` / `CreateAndCheckoutBranch` are **mutations** on
  someone else's machine — a host capability the remote host does not have, so the UI hides them.
- **Preview:** content arrives as an LRU-cached, version-keyed replica entry. Two server-side guards
  are mandatory, because the client is a network peer and its requests are untrusted input:
  **canonicalize and confirm the path is inside the project root** (path traversal), and **cap the
  size**. Images travel as bytes.
- **Todo list and project description:** both persist into the **project's**
  `.agentdock/settings.json` via `ProjectSettingsManager`, so both are server-side state that
  round-trips. They are not derived from the session.
- **Project icon:** `ResolveProjectIcon` auto-discovers a logo file in the project folder and
  persists the choice — discovery and write-back happen on the server; the client receives resolved
  icon identity plus image bytes when it is file-based.

### 7.4 Chat panel specifics that need remote handling

- **Local slash commands.** `/clear`, `/compact`, `/stop`, `/logs` are handled locally in
  `AiChatControl.ExecuteLocalCommand`. Remotely: `/clear` should clear *both* transcripts (it is a
  display action on shared state), `/compact` goes to the server session as it does now, `/stop`
  ends a session on the server (allow? see §7.1), and `/logs` opens an Explorer window — meaningless
  on the client and must be filtered out of the remote command list. The command list itself is a
  static table in `ClaudeSlashCommands`, so it needs no fetching.
- **Image attachments** flow client → server as bytes with a size cap.
- **Send queue and scheduled messages** should be visible on the client, and the **server's clock
  owns scheduling** — schedule times cross the wire as UTC.
- **Stale permission answers.** A client answer keyed by a `RequestId` the server has already
  resolved (timeout, or the local user answered before the lock) must be discarded, not applied to
  whatever prompt is current. `ClaudePermissionRequest.RequestId` already exists; echo it.
- **File-reference clicks** in chat resolve to remote reads under the same path guard.
- **Sounds.** `SoundService` fires on the server today (session start, agent waiting, session end).
  When a client is driving, the notification wants to be where the human is — the client. Simplest
  answer: fire on both, and let each side's sound settings decide.
- **Usage and cost.** Work happens on the server, so the client's usage indicator and cost figures
  must be the server's, relayed. Account labels likewise.

---

## 8. Security posture

This deserves blunt language in the product, not just in a doc.

> Server mode lets a remote peer instruct an agent that can read, write and execute on the server
> machine. If a session is running in dangerous mode, the remote peer can do anything that account
> can do.

That is the whole feature working as intended, and it is also why the following are not optional:

- TLS with pinned self-signed cert (identity), plus the pairing code (authorization).
- Rate-limited code attempts, code rotation on every server start.
- Explicit acknowledgement the first time server mode is enabled.
- Path containment on every file request.
- A visible indicator whenever a client is attached, and one-click kick.
- Never bind to all interfaces by default.

The port must not be treated as internet-facing. If someone wants remote access across the internet,
the honest advice is a VPN or SSH tunnel, and the docs should say so rather than implying a
port-forward is fine.

---

## 9. Start / stop scenario matrix

### Server side

| # | Scenario | Expected behaviour |
|---|---|---|
| S1 | Start server, no client yet | Bind succeeds, code shown; local input normal (per §6 recommendation) |
| S2 | Start server, port in use | Clean failure, prior state untouched, offer next free port |
| S3 | Start server, firewall prompt declined | Detectable? At minimum, warn that no client has ever connected; document the fix |
| S4 | Start server with sessions already running | Those become the client's initial tab set |
| S5 | Stop server, client attached | Send `ServerStopping` with reason; graceful close; local input unlocks; **Claude sessions keep running** |
| S6 | Server app exits | Same frame as S5, plus existing behaviour (all Claude sessions killed). Client must distinguish "server stopped" from "connection lost" |
| S7 | Server crashes | Client sees dead socket/heartbeat timeout → reconnecting state, transcript stays visible but read-only |
| S8 | Server machine sleeps mid-turn | Prevented by the power request (§5). If it happens anyway: Claude subprocess survives, stream resumes, client resyncs by seq |
| S9 | Session ends server-side (`/stop`, Claude exit, inactivity kill) | Push state change; client tab shows exited, input disabled for that tab |
| S10 | New session started server-side while client attached | Push tab-added |
| S11 | Project tab closed server-side | Push tab-closed; client marks it, does not silently vanish |
| S12 | Server switches workspace | Blocked while server mode is on (§11) |
| S13 | Server regenerates the code while a client is attached | Existing token invalidated → client disconnected, must re-pair. Confirm before doing it |

### Client side

| # | Scenario | Expected behaviour |
|---|---|---|
| C1 | Wrong host / unreachable | Timeout with a clear message, not a hang |
| C2 | Something else on that port | Handshake fails → "not an Agent Dock server" |
| C3 | Protocol version mismatch | Refuse explicitly, name both versions, do not attempt compatibility |
| C4 | Wrong code | Error, retry allowed, rate-limited; failures visible on the server |
| C5 | Cert fingerprint changed since last pair | Warn prominently; require confirmation |
| C6 | Second client connects | Rejected with the incumbent's name; the server user can kick the incumbent (§11) |
| C7 | Clean disconnect | Server unlocks input, clears client indicator |
| C8 | Client crashes / process killed | Heartbeat timeout → server unlocks after grace period |
| C9 | Network blip | Auto-reconnect with backoff using the session token; resync by seq; no code re-prompt |
| C10 | Client sends a message then disconnects | Server still processes it — it is already in Claude. Response is in the transcript on reconnect. **No work is lost**, which is a nice property to advertise |
| C11 | Client sends while session is Working | Existing `SendQueue` behaviour; queue state is replicated so the client sees it |
| C12 | Client answers a stale permission prompt | Discarded by `RequestId` |
| C13 | Client closes a remote tab | Local view action only; does not close anything on the server |
| C14 | Client exits | Equivalent to C7 |

---

## 10. Things easy to miss

- **Sequence numbers and a bounded replay buffer.** The difference between a reconnect that works and
  a transcript with duplicated paragraphs.
- **Half-open connection detection.** Heartbeats are not optional on Wi-Fi/VPN.
- **Backpressure with selective coalescing.** Merge text deltas, never drop structural ops.
- **The server going to sleep.** Hold a power request while server mode is on.
- **Two instances on one machine for testing.** There is no single-instance mutex, so loopback
  testing works — but both instances share `%LOCALAPPDATA%/AgentDock/settings.json` and will fight
  over it (last writer wins). A dev-only settings-directory override would make testing sane.
- **Protocol version as a first-class field**, checked before anything else. This will ship in
  0.11 and be talked to by 0.12 clients.
- **Disconnect *reasons*.** "Server stopped", "kicked", "code regenerated", "version mismatch",
  "connection lost" are five different messages to a user and one enum on the wire. Logging them on
  both sides will pay for itself the first week.
- **Non-Windows clients later.** Keeping paths on the wire as opaque server-relative strings costs
  nothing now and keeps an Avalonia or mobile client possible.
- **LAN discovery.** Typing an IP is fine; a UDP broadcast/mDNS "servers near you" list is a clear
  future nicety, not a v1 need.
- **What the window title and taskbar say.** A client window that looks identical to a local one is
  a way to run the wrong command on the wrong machine. Remote tabs, the group, and the window title
  all want unmistakable remote branding.

---

## 11. Settled decisions

Do not re-litigate these.

- **Scope is the whole feature, not a staged subset.** Chat, git status, file explorer and file
  preview are all remoted. There is no "chat-only first version". The `IProjectHost` refactor (§2.1)
  is still a sensible *first commit* because it is a no-behaviour-change change, but it is a
  sequencing choice inside one feature, not a reduced release.
- **The client drives existing sessions only.** It discovers the server's active set, shows one tab
  per session, and types into them. It does not start new sessions (that would need the server's full
  project list plus an "as which account?" choice) and does not end them.
- **One client at a time.** A second connection is rejected with the incumbent's name; the server
  user can kick the incumbent. Input ownership is therefore never ambiguous.
- **Remote connect is exclusive with a local workspace** — it is the peer of "open workspace", per
  the original framing. No mixing of local and remote groups in one window.
- **Transport is `TcpListener` + `SslStream` + hand-rolled length-prefixed frames** (§3). The
  ASP.NET Core runtime prerequisite is rejected; the `HttpListener` URL-ACL requirement is rejected.
- **Every panel goes through the host abstraction, not just chat.** File explorer, git status, file
  preview, todo list, project description and the project icon are all served through `IProjectHost`
  and all replicate. There is no panel that keeps reaching for the local machine.
- **The mechanism is replica + pushed deltas, not request/response polling** (§2.5). Panels observe
  state and never perform I/O. The local implementation is push-based too, via the existing
  `FileSystemWatcher`, so both implementations run the same code shape.
- **Bound collections are diff-and-patched, never cleared and rebuilt.** `GitStatusControl.SyncFileList`
  is the reference implementation; `FileExplorerControl.Refresh`'s clear-and-rebuild is the bug to
  remove. This is a correctness rule for the abstraction, not a remote-only optimisation.
- **Server mode is non-interactive on the server.** Entering server mode makes all agent interaction
  inert on the server machine — input box, send, queue/schedule, permission Allow/Deny, and
  `AskUserQuestion` answers. Everything is answered from the remote, because that is where the human
  is. Stop Server stays live, and is the way a returning user reclaims control. See §6 for the two
  consequences (a prompt with no client attached waits quietly and that is correct; permission
  remoting is core protocol).
- **Workspace switching is blocked while server mode is on.** Server mode means nobody is driving the
  server's UI, so the ability to swap the active session set out from under a connected client buys
  nothing and costs a whole resync path. The server user stops the server first.

---

## 12. Open decisions

None outstanding. Everything above is decided; next deliverable is an implementation plan.

---

## 13. Implementation status

Built and verified on branch `refactor/di-and-mvvm`. This section records what exists, what was
deliberately left out, and the gaps a later session should know about.

### Verified end to end

Exercised by driving two real instances over loopback (UI automation, not unit tests):

| Behaviour | Result |
|---|---|
| Server mode starts, port bind-checked, pairing code shown on the strip | works |
| TLS handshake, server identified by machine name + certificate fingerprint before any secret | works |
| Pairing code accepted as typed (separator, lower case) | works |
| Client builds a group named after the server, with one tab per active session | works |
| File explorer tree served from the server, lazily per expanded node | works |
| Git status + branch replicated (`refactor/di-and-mvvm` shown on the client) | works |
| File clicked on the client, content fetched from the server and syntax-highlighted | works |
| Message typed on the client, run by the server, answer + cost + tokens streamed back | works |
| Server agent input inert while a client is attached (composer, send, permission panels) | works |
| Take back control: client told "The server took back control.", server unlocks and keeps listening | works |
| Stop server: client told "The server stopped hosting.", server unlocks, menu re-enabled | works |

Automated coverage (178 tests): every frame and every `ChatOp` round-trips through its JSON
discriminator; pairing proof/verify including replay and wrong-code; path containment against
traversal and prefix-match escapes; version-token caching; and a real loopback handshake suite
covering pairing, lockout, second-client refusal, stop, kick and code regeneration.

### Deviations from the design above

- **No "LAN only" bind option.** The design suggested defaulting to the LAN. There is no such bind
  address: reaching another machine requires `IPAddress.Any`, and binding a single adapter is
  fragile under VPN or DHCP change while restricting nothing a firewall rule does not restrict
  better. The choice offered is loopback-only (for testing two instances on one box) or all
  interfaces. See `RemoteBindScope`.
- **Resync instead of a server-side replay buffer.** `RemoteProtocol.ReplayBufferSize` exists but is
  unused; recovery is `ResyncCmd` plus incremental snapshot reconciliation on the client. Costs more
  bandwidth on the rare path, cannot be subtly wrong, and the client's reconcile means the user sees
  no flash.
- **Bubble identity is carried through the send queue.** Not in the original design, and needed: a
  remote message the server had to queue would otherwise be dispatched under a fresh id and appear
  twice on the client. `QueuedMessage.BubbleId` exists for this.

### Known gaps

- **`WatchDirectoryCmd` is accepted but ignored server-side.** Directory pinning is tracked on the
  client only; the server pushes invalidations for whatever the git watcher reports rather than
  filtering to pinned directories. Functionally correct, chattier than designed.
- **`AckCmd` is informational.** Nothing consumes it, because recovery is resync-based.
- **The client does not display the server's send queue.** `ProjectSnapshotMsg.Queue` is populated
  and sent, but nothing applies it: the client's queue chips are driven by its own `SendQueue`, and
  populating that would make the client's pump try to send messages the server already owns. A
  display-only collection is the right fix.
- **`ProjectRemovedMsg` is never sent.** Session start/stop triggers a full host-snapshot rebuild
  instead, which the client reconciles. The frame and its handler exist for when a cheaper path is
  wanted.
- **Image attachments from a remote client are wired but unverified.** `SendMessageCmd.Images`
  carries them and the server rebuilds `ImageAttachment`s, but no end-to-end test has sent one.
- **"Session ended" after every turn** appears on both machines. That is pre-existing local
  behaviour (`AiChatControl.OnProcessExited` — the one-shot process exits after each turn), not
  something remoting introduced; it is replicated faithfully.

### The abstraction shortfall — read this before extending

**`IProjectHost` (§2.1) and the state-shaped refinement (§2.5) were not built.** What exists instead
is narrower and less clean:

- Each panel keeps taking `string projectPath` and gained an `EnterRemoteMode(...)` plus an
  `IsRemote` branch inside its own methods. The panels therefore know about *both* modes.
- The channel interfaces are decomposed per concern (`IRemoteChatChannel`, `IRemoteFileChannel`,
  `IRemoteProjectSettingsChannel`) rather than aggregated behind one host object.
- `CreateProjectDockingLayout` does branch exactly once on `isRemote`, so there *is* a single
  composition site — but it selects a mode for panels that then re-check it, rather than selecting
  an implementation the panels cannot distinguish.

The consequence is the one the design predicted: every future panel has to remember to handle both
cases, and "does this panel work remotely?" is answered by reading the panel rather than by which
host it was handed. The replica discipline is real where it matters (git status and the explorer
patch rather than rebuild; the preview caches under a version token) but it is implemented panel by
panel rather than enforced by a shared seam.

This was a sequencing choice: the MVVM/abstraction restructure was explicitly deferred, and the
feature was built on the structure that existed. It is worth collapsing into `IProjectHost` when that
restructure happens — the per-panel remote code is then the input to it, not wasted work, because the
wire protocol, the replication rules and the panel-level patch logic all survive the move.
