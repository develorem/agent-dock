using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services.Abstractions;

namespace AgentDock.Services;

/// <summary>
/// Manages Claude Code CLI interaction for one project.
/// Uses one-shot mode per turn with --resume for conversation continuity.
/// Each SendMessage spawns a new process; streaming output is read in real-time.
/// </summary>
public class ClaudeSession : IDisposable
{
    private readonly string _workingDirectory;
    private readonly ILogService _log;
    private readonly IClaudeEnvironment _environment;
    private readonly IPerfDiagnostics _perf;
    private readonly string? _accountConfigDir;
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private System.Timers.Timer? _inactivityTimer;
    private bool _disposed;

    /// <summary>
    /// How long (in seconds) with no stdout output before firing InactivityTimeout.
    /// Default 90 seconds. Set to 0 to disable.
    /// </summary>
    public int InactivityTimeoutSeconds { get; set; } = 90;


    public ClaudeSessionState State { get; private set; } = ClaudeSessionState.NotStarted;
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public bool IsDangerousMode { get; private set; }
    public ClaudePermissionRequest? PendingPermission { get; private set; }

    // Subagents / background shells that are still running, keyed by task_id. Kept
    // deliberately separate from the turn lifecycle: a turn can return to Idle while
    // these keep working ("I'll spin up a background worker…"). The tab indicator uses
    // HasBackgroundWork to show "idle but still busy" rather than a solid-green
    // "available" diamond (see MainWindow.RefreshBackgroundWorkVisual).
    private readonly HashSet<string> _activeBackgroundTasks = [];

    /// <summary>
    /// True while one or more subagents / background tasks are still running, even if
    /// the main conversation turn has already returned to <see cref="ClaudeSessionState.Idle"/>.
    /// </summary>
    public bool HasBackgroundWork
    {
        get { lock (_activeBackgroundTasks) { return _activeBackgroundTasks.Count > 0; } }
    }

    // --- Events ---

    public event Action<ClaudeSessionState>? StateChanged;
    public event Action<ClaudeSystemInit>? Initialized;
    public event Action<ClaudeAssistantMessage>? AssistantMessageReceived;
    public event Action<ClaudeStreamDelta>? StreamDelta;
    public event Action<ClaudeContentBlockEvent>? ContentBlockStarted;
    public event Action<ClaudeContentBlockEvent>? ContentBlockStopped;
    public event Action<ClaudePermissionRequest>? PermissionRequested;
    public event Action<ClaudeResultMessage>? ResultReceived;
    /// <summary>Raised on subagent / background-task lifecycle events (task_started,
    /// task_progress, task_updated, task_notification).</summary>
    public event Action<ClaudeTaskEvent>? TaskEvent;
    /// <summary>Raised, on empty↔non-empty transitions only, when <see cref="HasBackgroundWork"/> changes.</summary>
    public event Action? BackgroundWorkChanged;
    public event Action<string>? ErrorOutput;
    public event Action<int>? ProcessExited;
    public event Action? InactivityTimeout;

    /// <summary>
    /// Creates a session for a project. When <paramref name="accountConfigDir"/> is
    /// set, every spawned <c>claude</c> subprocess runs with <c>CLAUDE_CONFIG_DIR</c>
    /// pointed at it, so this session acts as that Claude account (credentials,
    /// history and session transcripts live there). Null / empty = the machine's
    /// default login (<c>~/.claude</c>), i.e. today's behaviour. Chosen on the Start
    /// panel and fixed for the life of the session — see AccountManager.
    /// </summary>
    public ClaudeSession(
        string workingDirectory,
        ILogService log,
        IClaudeEnvironment environment,
        IPerfDiagnostics perf,
        string? accountConfigDir = null)
    {
        _workingDirectory = workingDirectory;
        _accountConfigDir = string.IsNullOrWhiteSpace(accountConfigDir) ? null : accountConfigDir;
        _log = log;
        _environment = environment;
        _perf = perf;
        _perf.SessionCreated();
    }

    /// <summary>
    /// Marks the session as ready. Does not spawn a process yet —
    /// the first process is spawned when SendMessage is called.
    /// </summary>
    public void Start(bool dangerousMode = false)
    {
        _log.Info($"ClaudeSession.Start: dangerous={dangerousMode}, cwd={_workingDirectory}");

        if (State != ClaudeSessionState.NotStarted && State != ClaudeSessionState.Exited && State != ClaudeSessionState.Error)
            throw new InvalidOperationException($"Cannot start session in state {State}");

        IsDangerousMode = dangerousMode;
        SetState(ClaudeSessionState.Initializing);

        // Fire a synthetic init so the UI knows we're ready
        Initialized?.Invoke(new ClaudeSystemInit
        {
            SessionId = "",
            Model = "(pending first message)",
            Cwd = _workingDirectory,
            PermissionMode = dangerousMode ? "bypassPermissions" : "default",
            Tools = []
        });

        SetState(ClaudeSessionState.Idle);
    }

    /// <summary>
    /// Sends a user message by spawning a one-shot claude process.
    /// Uses --resume to continue the conversation if a session ID exists.
    /// Pass <paramref name="images"/> to attach images as Anthropic <c>image</c>
    /// content blocks; when empty the message is sent as a plain string
    /// (unchanged from the text-only path).
    /// </summary>
    public void SendMessage(string text, IReadOnlyList<ImageAttachment>? images = null)
    {
        if (State != ClaudeSessionState.Idle)
            throw new InvalidOperationException($"Cannot send message in state {State}");

        // Fresh turn — discard any background work tracked from a prior (now-exited)
        // process so a stale entry can't wedge the indicator into a permanent "busy".
        ClearBackgroundWork();

        SetState(ClaudeSessionState.Working);

        // Build arguments — matches the Claude Agent SDK's spawn args:
        // stream-json I/O for the full JSON protocol, --permission-prompt-tool stdio
        // routes permission prompts through stdin/stdout as control_request/control_response.
        // Note: -p is NOT used (the SDK doesn't use it; stream-json implies non-interactive).
        var args = "--output-format stream-json --input-format stream-json --permission-prompt-tool stdio --verbose --include-partial-messages";

        if (SessionId != null)
            args += $" --resume \"{SessionId}\"";

        if (IsDangerousMode)
            args += " --dangerously-skip-permissions";

        var resolvedPath = _environment.ResolveBinaryPath();
        _log.Info($"ClaudeSession.SendMessage: launching '{resolvedPath}' with args: {args}");

        var psi = new ProcessStartInfo
        {
            FileName = resolvedPath,
            Arguments = args,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        // Prevent nested-session detection if launched from within Claude Code
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        // Run this turn as the account chosen on the Start panel by relocating
        // Claude's entire config dir. Unset = the machine's default ~/.claude login.
        if (_accountConfigDir != null)
        {
            psi.Environment["CLAUDE_CONFIG_DIR"] = _accountConfigDir;
            _log.Info($"ClaudeSession.SendMessage: CLAUDE_CONFIG_DIR={_accountConfigDir}");
        }

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.Error("ClaudeSession.SendMessage: failed to start process", ex);
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke($"Failed to start Claude: {ex.Message}");
            return;
        }

        if (_process == null)
        {
            _log.Error("ClaudeSession.SendMessage: Process.Start returned null");
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke("Failed to start Claude process");
            return;
        }

        _log.Info($"ClaudeSession.SendMessage: process started, PID={_process.Id}");

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        // Read stderr
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                _log.Info($"ClaudeSession STDERR: {e.Data}");
                ErrorOutput?.Invoke(e.Data);
            }
        };
        _process.BeginErrorReadLine();

        // Read stdout NDJSON
        _readCts = new CancellationTokenSource();
        var stdout = _process.StandardOutput;
        _readTask = Task.Run(() => ReadOutputLoop(stdout, _readCts.Token));

        // Observe faults so they don't go unnoticed
        _readTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _log.Error("ClaudeSession: ReadOutputLoop task faulted", t.Exception);
                ErrorOutput?.Invoke($"Read loop crashed: {t.Exception?.InnerException?.Message}");
                if (State == ClaudeSessionState.Working || State == ClaudeSessionState.WaitingForPermission)
                    SetState(ClaudeSessionState.Error);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        // Start inactivity watchdog
        StartInactivityTimer();

        // Send the user message as JSON on stdin — this is what actually
        // triggers Claude to start processing (with stream-json input,
        // the CLI reads from stdin, not from -p).
        WriteStdin(SerializeUserMessage(text, images));
    }

    /// <summary>
    /// Responds to a pending permission request by allowing the tool use.
    /// </summary>
    public void AllowPermission()
    {
        if (PendingPermission == null || _process == null)
            return;

        _log.Info($"ClaudeSession.AllowPermission: {PendingPermission.ToolName}");
        WriteStdin(JsonSerializer.Serialize(new ClaudeControlResponse
        {
            Response = new ClaudeControlResponseBody
            {
                RequestId = PendingPermission.RequestId,
                ResponseData = new ClaudePermissionAllow
                {
                    UpdatedInput = PendingPermission.Input.ValueKind != JsonValueKind.Undefined
                        ? PendingPermission.Input : null,
                    ToolUseId = PendingPermission.ToolUseId
                }
            }
        }, JsonOptions));

        PendingPermission = null;
        StartInactivityTimer(); // Restart watchdog after permission granted
        SetState(ClaudeSessionState.Working);
    }

    /// <summary>
    /// Responds to a pending permission request by denying the tool use.
    /// </summary>
    public void DenyPermission(string? reason = null)
    {
        if (PendingPermission == null || _process == null)
            return;

        _log.Info($"ClaudeSession.DenyPermission: {PendingPermission.ToolName}");
        WriteStdin(JsonSerializer.Serialize(new ClaudeControlResponse
        {
            Response = new ClaudeControlResponseBody
            {
                RequestId = PendingPermission.RequestId,
                ResponseData = new ClaudePermissionDeny
                {
                    Message = reason ?? "User denied this action",
                    ToolUseId = PendingPermission.ToolUseId
                }
            }
        }, JsonOptions));

        PendingPermission = null;
        StartInactivityTimer(); // Restart watchdog after permission denied
        SetState(ClaudeSessionState.Working);
    }

    /// <summary>
    /// Responds to a pending AskUserQuestion permission request with the user's selected answer.
    /// Sends allow with updatedInput containing the answers dictionary.
    /// </summary>
    public void AnswerQuestion(string questionText, string answer)
    {
        if (PendingPermission == null || _process == null)
            return;

        _log.Info($"ClaudeSession.AnswerQuestion: '{answer}' for '{questionText}'");

        // Build updatedInput with the user's answer merged into the original input
        var answersDict = new Dictionary<string, string> { { questionText, answer } };
        var updatedInput = new Dictionary<string, object>();

        // Copy original input properties
        if (PendingPermission.Input.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in PendingPermission.Input.EnumerateObject())
                updatedInput[prop.Name] = prop.Value;
        }
        updatedInput["answers"] = answersDict;

        var updatedJson = JsonSerializer.SerializeToElement(updatedInput, JsonOptions);

        WriteStdin(JsonSerializer.Serialize(new ClaudeControlResponse
        {
            Response = new ClaudeControlResponseBody
            {
                RequestId = PendingPermission.RequestId,
                ResponseData = new ClaudePermissionAllow
                {
                    UpdatedInput = updatedJson,
                    ToolUseId = PendingPermission.ToolUseId
                }
            }
        }, JsonOptions));

        PendingPermission = null;
        StartInactivityTimer(); // Restart watchdog after question answered
        SetState(ClaudeSessionState.Working);
    }

    public async Task StopAsync()
    {
        _log.Info("ClaudeSession.Stop called");

        StopInactivityTimer();

        // Cancel the read loop first so it stops posting Dispatcher calls
        _readCts?.Cancel();

        // Set state immediately (UI stays responsive)
        SetState(ClaudeSessionState.Exited);

        // Kill the process tree off the UI thread, but await completion
        var proc = _process;
        _process = null;
        if (proc != null && !proc.HasExited)
        {
            await Task.Run(() =>
            {
                try
                {
                    proc.StandardInput.Close();
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch { }
                finally
                {
                    _log.Info($"ClaudeSession: process killed (exited={proc.HasExited})");
                    try { proc.Dispose(); } catch { }
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _perf.SessionDisposed();
        StopInactivityTimer();
        _readCts?.Cancel();
        KillCurrentProcess();
        _readCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Internal ---

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes a user message for stdin. Text-only messages serialize with a
    /// plain string <c>content</c> (unchanged from before image support); messages
    /// with images serialize <c>content</c> as an Anthropic content-block array.
    /// Public so the protocol shape can be locked by tests.
    /// </summary>
    public static string SerializeUserMessage(string text, IReadOnlyList<ImageAttachment>? images)
        => JsonSerializer.Serialize(
            new ClaudeUserMessage { Message = new ClaudeMessagePayload { Content = BuildUserContent(text, images) } },
            JsonOptions);

    /// <summary>
    /// Builds the <c>content</c> value: a plain string when there are no images,
    /// otherwise a list of content blocks (images first, then the text block when
    /// non-empty) matching the Anthropic Messages API shape.
    /// </summary>
    private static object BuildUserContent(string text, IReadOnlyList<ImageAttachment>? images)
    {
        if (images is not { Count: > 0 })
            return text;

        var blocks = new List<object>(images.Count + 1);
        foreach (var img in images)
            blocks.Add(new ClaudeImageBlock
            {
                Source = new ClaudeImageSource { MediaType = img.MediaType, Data = img.Base64Data }
            });
        if (!string.IsNullOrEmpty(text))
            blocks.Add(new ClaudeTextBlock { Text = text });
        return blocks;
    }

    private void WriteStdin(string json)
    {
        if (_process == null || _process.HasExited)
            return;

        try
        {
            _log.Info($"ClaudeSession STDIN: {(json.Length > 500 ? json[..500] + "..." : json)}");
            _process.StandardInput.WriteLine(json);
            _process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            _log.Error("ClaudeSession: WriteStdin error", ex);
        }
    }

    private void KillCurrentProcess()
    {
        if (_process == null || _process.HasExited)
            return;

        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(3000))
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process.Dispose();
        _process = null;
    }

    private async Task ReadOutputLoop(StreamReader stdout, CancellationToken ct)
    {
        _log.Info("ClaudeSession: ReadOutputLoop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(ct);
                if (line == null)
                {
                    _log.Info("ClaudeSession: ReadOutputLoop got EOF");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Reset inactivity watchdog on each line of output
                ResetInactivityTimer();

                // Skip logging streaming-token deltas — they fire dozens of times per second
                // during a long response and were producing 100+ MB log files. The events are
                // still parsed and dispatched to the UI; we just don't write them to disk.
                if (!line.StartsWith("{\"type\":\"stream_event\"", StringComparison.Ordinal))
                    _log.Info($"ClaudeSession STDOUT: {(line.Length > 500 ? line[..500] + "..." : line)}");

                try
                {
                    ProcessMessage(line);
                }
                catch (Exception ex)
                {
                    _log.Error("ClaudeSession: ProcessMessage error", ex);
                    ErrorOutput?.Invoke($"Parse error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info("ClaudeSession: ReadOutputLoop cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("ClaudeSession: ReadOutputLoop error", ex);
            ErrorOutput?.Invoke($"Read error: {ex.Message}");
        }

        StopInactivityTimer();

        // If the loop ended while we were still Working (EOF without a result message,
        // or an exception killed the loop), recover by transitioning to Error.
        if (State == ClaudeSessionState.Working || State == ClaudeSessionState.WaitingForPermission)
        {
            _log.Warn($"ClaudeSession: ReadOutputLoop ended while in {State} — setting Error");
            ErrorOutput?.Invoke("Claude stopped responding (output stream ended unexpectedly)");
            SetState(ClaudeSessionState.Error);
        }

        _log.Info("ClaudeSession: ReadOutputLoop ended");
    }

    private void ProcessMessage(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            return;

        var type = typeProp.GetString();

        switch (type)
        {
            case "system":
                HandleSystemMessage(root);
                break;
            case "assistant":
                HandleAssistantMessage(root);
                break;
            case "stream_event":
                HandleStreamEvent(root);
                break;
            case "result":
                HandleResultMessage(root);
                break;
            case "control_request":
                HandleControlRequest(root);
                break;
        }
    }

    private void HandleSystemMessage(JsonElement root)
    {
        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;

        if (subtype is "task_started" or "task_progress" or "task_updated" or "task_notification")
        {
            HandleTaskEvent(subtype, root);
            return;
        }

        if (subtype != "init")
            return;

        var newSessionId = GetString(root, "session_id");
        var newModel = GetString(root, "model");

        _log.Info($"ClaudeSession: system init — session={newSessionId}, model={newModel}");

        // Capture session ID for --resume on subsequent turns
        if (newSessionId != null)
            SessionId = newSessionId;
        if (newModel != null)
            Model = newModel;
    }

    /// <summary>
    /// Parses a subagent / background-task lifecycle system message and raises
    /// <see cref="TaskEvent"/>. The status for <c>task_updated</c> lives in a nested
    /// <c>patch.status</c>; the others carry it (when present) at the top level.
    /// </summary>
    private void HandleTaskEvent(string subtype, JsonElement root)
    {
        var taskId = GetString(root, "task_id");
        if (taskId == null)
            return;

        var status = GetString(root, "status");
        if (status == null && root.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.Object)
            status = GetString(patch, "status");

        var evt = new ClaudeTaskEvent
        {
            Subtype = subtype,
            TaskId = taskId,
            ToolUseId = GetString(root, "tool_use_id"),
            Description = GetString(root, "description"),
            SubagentType = GetString(root, "subagent_type"),
            TaskType = GetString(root, "task_type"),
            Status = status
        };

        _log.Info($"ClaudeSession: task {subtype} id={taskId} status={status ?? "-"} type={evt.TaskType ?? "-"} agent={evt.SubagentType ?? "-"}");
        UpdateBackgroundWork(evt);
        TaskEvent?.Invoke(evt);
    }

    /// <summary>
    /// Folds a task lifecycle event into <see cref="_activeBackgroundTasks"/> and raises
    /// <see cref="BackgroundWorkChanged"/> only when the set crosses empty↔non-empty
    /// (the tab indicator only cares whether *any* background work is in flight).
    /// </summary>
    private void UpdateBackgroundWork(ClaudeTaskEvent evt)
    {
        bool changed;
        lock (_activeBackgroundTasks)
        {
            var wasActive = _activeBackgroundTasks.Count > 0;
            if (evt.IsTerminal)
                _activeBackgroundTasks.Remove(evt.TaskId);
            else
                _activeBackgroundTasks.Add(evt.TaskId);
            changed = (_activeBackgroundTasks.Count > 0) != wasActive;
        }

        if (changed)
            BackgroundWorkChanged?.Invoke();
    }

    /// <summary>
    /// Drops all tracked background work. Called when a new turn spawns and when the
    /// process exits: once the process is gone no further terminal task events can
    /// arrive, so this is the safety net that prevents a permanently "busy" indicator.
    /// </summary>
    private void ClearBackgroundWork()
    {
        bool changed;
        lock (_activeBackgroundTasks)
        {
            changed = _activeBackgroundTasks.Count > 0;
            _activeBackgroundTasks.Clear();
        }

        if (changed)
            BackgroundWorkChanged?.Invoke();
    }

    private void HandleAssistantMessage(JsonElement root)
    {
        var msg = new ClaudeAssistantMessage
        {
            Uuid = GetString(root, "uuid") ?? "",
            ParentToolUseId = GetString(root, "parent_tool_use_id"),
            Model = root.TryGetProperty("message", out var msgForModel)
                ? GetString(msgForModel, "model")
                : null
        };

        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var blockType = GetString(block, "type") ?? "";
                var cb = new ClaudeContentBlock
                {
                    Type = blockType,
                    Text = GetString(block, "text"),
                    Id = GetString(block, "id"),
                    Name = GetString(block, "name"),
                    Input = block.TryGetProperty("input", out var input) ? input.Clone() : null
                };
                msg.Content.Add(cb);
            }
        }

        AssistantMessageReceived?.Invoke(msg);
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt))
            return;

        var eventType = GetString(evt, "type");

        // content_block_start / content_block_stop
        if (eventType == "content_block_start")
        {
            var index = evt.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
            var blockType = "";
            if (evt.TryGetProperty("content_block", out var cb))
                blockType = GetString(cb, "type") ?? "";
            ContentBlockStarted?.Invoke(new ClaudeContentBlockEvent { Index = index, BlockType = blockType });
            return;
        }

        if (eventType == "content_block_stop")
        {
            var index = evt.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
            ContentBlockStopped?.Invoke(new ClaudeContentBlockEvent { Index = index, BlockType = "" });
            return;
        }

        // content_block_delta — text_delta or thinking_delta
        if (!evt.TryGetProperty("delta", out var delta))
            return;

        var deltaType = GetString(delta, "type");
        if (deltaType != "text_delta" && deltaType != "thinking_delta")
            return;

        var text = deltaType == "thinking_delta"
            ? GetString(delta, "thinking") ?? ""
            : GetString(delta, "text") ?? "";
        var index2 = evt.TryGetProperty("index", out var idx2) ? idx2.GetInt32() : 0;

        StreamDelta?.Invoke(new ClaudeStreamDelta
        {
            Text = text,
            ContentBlockIndex = index2,
            DeltaType = deltaType
        });
    }

    private void HandleResultMessage(JsonElement root)
    {
        List<string>? errorList = null;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            errorList = errors.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        // Parse token usage from "usage" object
        long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheCreation = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt64();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt64();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreation = cc.GetInt64();
        }

        var result = new ClaudeResultMessage
        {
            Subtype = GetString(root, "subtype") ?? "",
            IsError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean(),
            Result = GetString(root, "result"),
            TotalCostUsd = root.TryGetProperty("total_cost_usd", out var cost) ? cost.GetDouble() : null,
            NumTurns = root.TryGetProperty("num_turns", out var turns) ? turns.GetInt32() : null,
            DurationMs = root.TryGetProperty("duration_ms", out var dur) ? dur.GetInt64() : null,
            Errors = errorList,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreation
        };

        // A result with num_turns == 0 is NOT this turn completing. When a session is
        // resumed (--resume) with an orphaned background shell left over from a previous
        // turn, the CLI emits an empty, zero-cost "flush" result (num_turns:0, result:"")
        // BEFORE it processes the user's message — the real init + turn follow it on the
        // same process. Treating that as completion drops the session to Idle the instant
        // the user sends, firing the completion chime and a $0 cost line prematurely.
        // Ignore it and stay Working; the real result (num_turns >= 1) that follows
        // completes the turn. A genuine reply is always >= 1 turn; error results are let
        // through so real failures still surface.
        if (result.NumTurns == 0 && !result.IsError)
        {
            _log.Info("ClaudeSession: ignoring resume-flush result (num_turns=0) — real turn follows");
            return;
        }

        // Process finished this turn — stop the watchdog, close stdin so the process
        // exits cleanly, then transition back to idle. Next SendMessage will spawn a
        // fresh process with --resume.
        StopInactivityTimer();
        try { _process?.StandardInput.Close(); } catch { }
        SetState(ClaudeSessionState.Idle);
        ResultReceived?.Invoke(result);
    }

    private void HandleControlRequest(JsonElement root)
    {
        var requestId = GetString(root, "request_id") ?? "";

        if (!root.TryGetProperty("request", out var request))
            return;

        var subtype = GetString(request, "subtype");

        if (subtype == "can_use_tool")
        {
            var toolName = GetString(request, "tool_name") ?? "";
            var input = request.TryGetProperty("input", out var inp) ? inp.Clone() : default;
            var toolUseId = GetString(request, "tool_use_id") ?? "";

            _log.Info($"ClaudeSession: permission request — tool={toolName}, requestId={requestId}, toolUseId={toolUseId}");

            var permReq = new ClaudePermissionRequest
            {
                RequestId = requestId,
                ToolName = toolName,
                Input = input,
                ToolUseId = toolUseId
            };

            PendingPermission = permReq;
            StopInactivityTimer(); // Don't timeout while waiting for user
            SetState(ClaudeSessionState.WaitingForPermission);
            PermissionRequested?.Invoke(permReq);
        }
        else
        {
            _log.Warn($"ClaudeSession: unhandled control_request subtype '{subtype}', requestId={requestId}");
        }
    }

    private void SetState(ClaudeSessionState newState)
    {
        if (State == newState)
            return;

        _log.Info($"ClaudeSession: state {State} -> {newState}");
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode;
        try
        {
            exitCode = _process?.ExitCode ?? -1;
        }
        catch (InvalidOperationException)
        {
            // Race condition: the Exited event fires before the OS handle is fully ready.
            exitCode = -1;
        }

        _log.Info($"ClaudeSession: process exited with code {exitCode}");

        // The process is gone — no more task events can arrive, so any still-"running"
        // background tasks are dead. Clear them so the indicator settles.
        ClearBackgroundWork();

        // Only transition to error if we weren't expecting the exit
        if (State == ClaudeSessionState.Working)
        {
            if (exitCode != 0)
            {
                SetState(ClaudeSessionState.Error);
            }
            // If exit code 0 while working, the result handler should have set Idle already.
            // If it didn't (edge case), set idle now.
            else if (State == ClaudeSessionState.Working)
            {
                SetState(ClaudeSessionState.Idle);
            }
        }

        ProcessExited?.Invoke(exitCode);
    }

    // --- Inactivity Watchdog ---

    private void StartInactivityTimer()
    {
        StopInactivityTimer();
        if (InactivityTimeoutSeconds <= 0) return;

        _inactivityTimer = new System.Timers.Timer(InactivityTimeoutSeconds * 1000);
        _inactivityTimer.AutoReset = false;
        _inactivityTimer.Elapsed += (_, _) =>
        {
            if (State == ClaudeSessionState.Working)
            {
                _log.Warn($"ClaudeSession: no output for {InactivityTimeoutSeconds}s — firing InactivityTimeout");
                InactivityTimeout?.Invoke();
            }
        };
        _inactivityTimer.Start();
    }

    private void ResetInactivityTimer()
    {
        if (_inactivityTimer == null) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    /// <summary>
    /// Restarts the inactivity timer from scratch (called when user clicks "Wait longer").
    /// </summary>
    public void ExtendInactivityTimer()
    {
        StartInactivityTimer();
    }

    private void StopInactivityTimer()
    {
        if (_inactivityTimer == null) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Dispose();
        _inactivityTimer = null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
