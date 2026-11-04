"use strict";

// Agent Dock browser remote client. Deliberately plain - no framework, no build step, matching
// the "genuinely lightweight, bundled into a desktop app" constraint this was built under.
//
// A note on JSON casing: the wire protocol (RemoteFrame and friends, in
// src/AgentDock/Services/Remote/RemoteMessages.cs) has no naming policy configured, so every
// field name here must match the C# record property exactly - PascalCase, not camelCase. The two
// small REST auth endpoints below were written to match the same convention on purpose, so
// there is one casing rule to remember, not two.
//
// Also note: C# enums (ClaudeSessionState, GitFileStatus) serialize as their *numeric* ordinal,
// not as strings - there is no JsonStringEnumConverter configured. The numeric maps below mirror
// the declared order in the C# source exactly; if that order ever changes there, it must change
// here too.

const ClaudeSessionState = {
  NotStarted: 0, Initializing: 1, Idle: 2, Working: 3, WaitingForPermission: 4, Exited: 5, Error: 6,
};

const GitFileStatusLabel = ["M", "A", "D", "R", "?"]; // Modified, Added, Deleted, Renamed, Untracked

// ---------------------------------------------------------------- state

const state = {
  ws: null,
  hostName: "",
  projects: new Map(), // id -> { descriptor, bubbles: [{id, kind, text}], session, prevState, unread, git, pendingPermission }
  activeId: null,
};

// ---------------------------------------------------------------- pairing-code normalization
// Port of PairingCode.Normalize (Services/Remote/PairingCode.cs). Must stay byte-for-byte
// equivalent, or a web user's forgiven typo (O instead of 0, I/L instead of 1, U instead of V)
// would produce a proof the server never would.

function normalizeCode(typed) {
  if (!typed) return "";
  let out = "";
  for (const raw of typed.trim().toUpperCase()) {
    let c = raw;
    if (c === "O") c = "0";
    else if (c === "I" || c === "L") c = "1";
    else if (c === "U") c = "V";
    if (c === " " || c === "-" || c === "_") continue;
    out += c;
  }
  return out;
}

async function computeProof(code, nonce) {
  const keyBytes = new TextEncoder().encode(code.toUpperCase());
  const key = await crypto.subtle.importKey(
    "raw", keyBytes, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(nonce));
  return btoa(String.fromCharCode(...new Uint8Array(sig)));
}

// ---------------------------------------------------------------- auth + connect

const loginForm = document.getElementById("login-form");
const codeInput = document.getElementById("code-input");
const loginError = document.getElementById("login-error");
const connectButton = document.getElementById("connect-button");

loginForm.addEventListener("submit", async (e) => {
  e.preventDefault();
  loginError.textContent = "";
  connectButton.disabled = true;

  try {
    const code = normalizeCode(codeInput.value);
    if (!code) throw new Error("Enter the pairing code.");

    const challenge = await postJson("/api/auth/challenge", {});
    if (challenge.Error) throw new Error(challenge.Error);

    const proof = await computeProof(code, challenge.Nonce);
    const result = await postJson("/api/auth/proof", { AttemptId: challenge.AttemptId, Proof: proof });
    if (result.Error) throw new Error(result.Error);

    connect();
  } catch (err) {
    loginError.textContent = err.message || "Could not connect.";
    connectButton.disabled = false;
  }
});

async function postJson(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  return response.json();
}

function connect() {
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  const ws = new WebSocket(`${scheme}//${location.host}/ws`);
  state.ws = ws;

  ws.addEventListener("open", () => {
    document.getElementById("login").hidden = true;
    document.getElementById("app").hidden = false;
  });

  ws.addEventListener("message", (event) => {
    let frame;
    try {
      frame = JSON.parse(event.data);
    } catch {
      return;
    }
    handleFrame(frame);
  });

  ws.addEventListener("close", () => {
    loginError.textContent = "Disconnected. Reconnecting…";
    document.getElementById("app").hidden = true;
    document.getElementById("login").hidden = false;
    connectButton.disabled = false;
    // The session cookie (if still valid) lets this retry skip straight back in without
    // re-prompting for the code - same idea as the native client's session-token fast path.
    setTimeout(() => loginForm.requestSubmit ? tryReconnect() : null, 2000);
  });
}

async function tryReconnect() {
  // A fresh WebSocket attempt alone is enough: the browser resends the adsess cookie on the
  // upgrade request automatically, and the server's /ws handler accepts on that alone.
  connect();
}

function send(frame) {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) {
    state.ws.send(JSON.stringify(frame));
  }
}

// ---------------------------------------------------------------- frame handling

function handleFrame(frame) {
  switch (frame["$k"]) {
    case "ping":
      send({ "$k": "pong", TicksUtc: frame.TicksUtc });
      break;
    case "host.snapshot":
      onHostSnapshot(frame);
      break;
    case "project.added":
      ensureProject(frame.Project);
      renderTabs();
      break;
    case "project.removed":
      // Left in the map (marked gone) rather than deleted, so a currently-viewed tab doesn't
      // vanish out from under the user - mirrors the native client's "stale, not cleared".
      renderTabs();
      break;
    case "project.snapshot":
      onProjectSnapshot(frame);
      break;
    case "project.delta":
      onProjectDelta(frame);
      break;
    case "project.append":
      onProjectAppend(frame);
      break;
    case "session.state":
      onSessionState(frame);
      break;
    case "session.permission":
      onPermission(frame);
      break;
    case "session.permissionResolved":
      onPermissionResolved(frame);
      break;
    case "git.status":
      onGitStatus(frame);
      break;
    case "bye":
      loginError.textContent = frame.Message || "The server ended the session.";
      break;
    default:
      // Everything else (files.*, project.settings, ...) is out of scope for this client.
      break;
  }
}

function ensureProject(descriptor) {
  let project = state.projects.get(descriptor.Id);
  if (!project) {
    project = { descriptor, bubbles: [], session: null, prevState: null, unread: false, git: null, pendingPermission: null };
    state.projects.set(descriptor.Id, project);
  } else {
    project.descriptor = descriptor;
  }
  return project;
}

function onHostSnapshot(frame) {
  state.hostName = frame.HostName;
  document.getElementById("host-name").textContent = frame.HostName;

  for (const descriptor of frame.Projects) ensureProject(descriptor);

  renderTabs();
  if (state.activeId == null && frame.Projects.length > 0) selectProject(frame.Projects[0].Id);
}

function onProjectSnapshot(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;

  project.bubbles = frame.Transcript
    .map(messageToBubble)
    .filter((b) => b != null);
  project.session = frame.Session;
  project.prevState = frame.Session.State;
  project.git = frame.Git;
  project.pendingPermission = frame.PendingPermission;

  renderTabs();
  if (project === activeProject()) renderMain();
}

function messageToBubble(message) {
  switch (message["$m"]) {
    case "user": return { id: message.Id, kind: "user", text: message.Text };
    case "assistant": return { id: message.Id, kind: "assistant", text: message.Text };
    case "system": return { id: message.Id, kind: "system", text: message.Text };
    default: return null; // "activity" - the live tool-call/thinking detail, out of scope here
  }
}

function onProjectDelta(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;

  let changed = false;
  frame.Ops.forEach((op, i) => {
    // The only op this client renders: the assistant's finished answer for the turn in
    // progress. Thinking/tool/subagent/count ops are exactly the "working" detail the user
    // said this client doesn't need to show - the status dot already covers "it's working".
    if (op["$o"] === "answer") {
      project.bubbles.push({ id: `delta-${frame.Seq}-${i}`, kind: "assistant", text: op.Text });
      changed = true;
    }
  });

  if (changed && project === activeProject()) renderTranscript(project);
}

function onProjectAppend(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;

  const bubble = messageToBubble(frame.Message);
  if (!bubble) return;

  // De-dupe the echo of a message this client itself just sent optimistically.
  if (project.bubbles.some((b) => b.id === bubble.id)) return;

  project.bubbles.push(bubble);
  if (project === activeProject()) renderTranscript(project);
}

function onSessionState(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;

  const prev = project.session ? project.session.State : null;
  project.session = frame.State;

  // Mirrors the native tab-icon rule: a turn that finished while this tab wasn't the one you
  // were looking at leaves the unread (flashing-green) mark until you click into it.
  const justFinished = (prev === ClaudeSessionState.Working || prev === ClaudeSessionState.WaitingForPermission)
    && frame.State.State === ClaudeSessionState.Idle;
  if (project !== activeProject() && (justFinished || frame.State.HasBackgroundWork)) {
    project.unread = true;
  }
  if (project === activeProject()) project.unread = false;

  renderTabs();
  if (project === activeProject()) renderStatus(project);
}

function onPermission(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;
  project.pendingPermission = frame;
  if (project === activeProject()) renderQa(project);
}

function onPermissionResolved(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project || !project.pendingPermission) return;
  if (project.pendingPermission.RequestId !== frame.RequestId) return;
  project.pendingPermission = null;
  if (project === activeProject()) renderQa(project);
}

function onGitStatus(frame) {
  const project = state.projects.get(frame.ProjectId);
  if (!project) return;
  project.git = frame.Status;
  if (project === activeProject()) renderChangedFiles(project);
}

// ---------------------------------------------------------------- rendering

function activeProject() {
  return state.activeId == null ? null : state.projects.get(state.activeId);
}

function selectProject(id) {
  state.activeId = id;
  const project = state.projects.get(id);
  if (project) project.unread = false;
  renderTabs();
  renderMain();
}

function dotClass(project) {
  if (!project.session) return "";
  switch (project.session.State) {
    case ClaudeSessionState.Working: return "working";
    default: return project.unread ? "idle-unread" : "idle-read";
  }
}

function renderTabs() {
  const list = document.getElementById("tab-list");
  list.innerHTML = "";

  for (const project of state.projects.values()) {
    const row = document.createElement("div");
    row.className = "tab" + (project.descriptor.Id === state.activeId ? " active" : "");

    const dot = document.createElement("span");
    dot.className = "dot " + dotClass(project);

    const name = document.createElement("span");
    name.className = "name";
    name.textContent = project.descriptor.DisplayName;

    row.append(dot, name);
    row.addEventListener("click", () => selectProject(project.descriptor.Id));
    list.appendChild(row);
  }
}

function renderMain() {
  const project = activeProject();
  document.getElementById("project-name").textContent = project ? project.descriptor.DisplayName : "—";
  if (!project) return;

  renderStatus(project);
  renderTranscript(project);
  renderChangedFiles(project);
  renderQa(project);
}

function renderStatus(project) {
  document.getElementById("project-status").textContent = project.session ? project.session.StatusLabel : "";
}

function renderTranscript(project) {
  const el = document.getElementById("transcript");
  el.innerHTML = "";
  for (const bubble of project.bubbles) {
    const div = document.createElement("div");
    div.className = `bubble ${bubble.kind}`;
    div.textContent = bubble.text;
    el.appendChild(div);
  }
  el.scrollTop = el.scrollHeight;
}

function renderChangedFiles(project) {
  const el = document.getElementById("changed-files");
  const entries = project.git && project.git.Entries ? project.git.Entries : [];
  if (entries.length === 0) {
    el.hidden = true;
    return;
  }

  el.hidden = false;
  el.innerHTML = "";
  for (const entry of entries) {
    const row = document.createElement("div");
    row.className = "file";
    const status = document.createElement("span");
    status.className = "status";
    status.textContent = GitFileStatusLabel[entry.Status] || "?";
    const path = document.createElement("span");
    path.textContent = entry.FilePath;
    row.append(status, path);
    el.appendChild(row);
  }
}

function renderQa(project) {
  const panel = document.getElementById("qa");
  const permission = project.pendingPermission;
  if (!permission) {
    panel.hidden = true;
    return;
  }
  panel.hidden = false;

  const questionEl = document.getElementById("qa-question");
  const optionsEl = document.getElementById("qa-options");
  const customInput = document.getElementById("qa-custom-input");
  optionsEl.innerHTML = "";

  if (permission.ToolName === "AskUserQuestion") {
    let parsed;
    try { parsed = JSON.parse(permission.InputJson); } catch { parsed = null; }
    const question = parsed && parsed.questions && parsed.questions[0];

    questionEl.textContent = question ? question.question : "The agent has a question.";

    for (const option of (question && question.options) || []) {
      const btn = document.createElement("button");
      btn.textContent = option.label;
      btn.title = option.description || "";
      btn.addEventListener("click", () => {
        send({ "$k": "cmd.answer", ProjectId: project.descriptor.Id, RequestId: permission.RequestId,
               QuestionText: questionEl.textContent, Answer: option.label });
      });
      optionsEl.appendChild(btn);
    }

    customInput.placeholder = "Or type a reply…";
    customInput.hidden = false;
    customInput.dataset.mode = "answer";
  } else {
    questionEl.textContent = `${project.descriptor.DisplayName} wants to run: ${permission.ToolName}`;

    const allow = document.createElement("button");
    allow.className = "primary";
    allow.textContent = "Allow";
    allow.addEventListener("click", () => {
      send({ "$k": "cmd.allow", ProjectId: project.descriptor.Id, RequestId: permission.RequestId });
    });

    const deny = document.createElement("button");
    deny.textContent = "Deny";
    deny.addEventListener("click", () => {
      send({ "$k": "cmd.deny", ProjectId: project.descriptor.Id, RequestId: permission.RequestId, Reason: null });
    });

    optionsEl.append(allow, deny);
    customInput.hidden = true;
    customInput.dataset.mode = "";
  }
}

document.getElementById("qa-custom-send").addEventListener("click", () => {
  const project = activeProject();
  const input = document.getElementById("qa-custom-input");
  if (!project || !project.pendingPermission || !input.value.trim()) return;

  send({
    "$k": "cmd.answer",
    ProjectId: project.descriptor.Id,
    RequestId: project.pendingPermission.RequestId,
    QuestionText: document.getElementById("qa-question").textContent,
    Answer: input.value.trim(),
  });
  input.value = "";
});

// ---------------------------------------------------------------- composer

document.getElementById("composer-send").addEventListener("click", sendComposerMessage);
document.getElementById("composer-input").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    sendComposerMessage();
  }
});

function sendComposerMessage() {
  const project = activeProject();
  const input = document.getElementById("composer-input");
  const text = input.value.trim();
  if (!project || !text) return;

  const id = crypto.randomUUID();
  project.bubbles.push({ id, kind: "user", text });
  renderTranscript(project);

  send({ "$k": "cmd.send", MessageId: id, ProjectId: project.descriptor.Id, Text: text, Images: null });
  input.value = "";
}

// ---------------------------------------------------------------- theme

const themeToggle = document.getElementById("theme-toggle");
const themeOrder = ["auto", "light", "dark"];

function applyTheme(theme) {
  if (theme === "auto") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
  localStorage.setItem("theme", theme);
}

themeToggle.addEventListener("click", () => {
  const current = localStorage.getItem("theme") || "auto";
  const next = themeOrder[(themeOrder.indexOf(current) + 1) % themeOrder.length];
  applyTheme(next);
});

applyTheme(localStorage.getItem("theme") || "auto");
