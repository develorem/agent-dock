using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Manages Claude Code CLI interaction for one project.
/// Uses one-shot mode per turn with --resume for conversation continuity.
/// Each SendMessage spawns a new process; streaming output is read in real-time.
/// </summary>
public class ClaudeSession : IDisposable
{
    private readonly string _workingDirectory;
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

    /// <summary>
    /// Path to the claude binary. Defaults to "claude" (found via PATH).
    /// Set via Settings > Claude Path Override and persisted in AppSettings.
    /// </summary>
    public static string ClaudeBinaryPath { get; set; } = "claude";

    public ClaudeSessionState State { get; private set; } = ClaudeSessionState.NotStarted;
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public bool IsDangerousMode { get; private set; }
    public ClaudePermissionRequest? PendingPermission { get; private set; }

    // --- Events ---

    public event Action<ClaudeSessionState>? StateChanged;
    public event Action<ClaudeSystemInit>? Initialized;
    public event Action<ClaudeAssistantMessage>? AssistantMessageReceived;
    public event Action<ClaudeStreamDelta>? StreamDelta;
    public event Action<ClaudeContentBlockEvent>? ContentBlockStarted;
    public event Action<ClaudeContentBlockEvent>? ContentBlockStopped;
    public event Action<ClaudePermissionRequest>? PermissionRequested;
    public event Action<ClaudeResultMessage>? ResultReceived;
    public event Action<string>? ErrorOutput;
    public event Action<int>? ProcessExited;
    public event Action? InactivityTimeout;

    public ClaudeSession(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    public static bool IsClaudeAvailable()
    {
        try
        {
            // Use cmd.exe /c to resolve .cmd/.bat wrappers (npm-installed CLIs on Windows)
            // This matches the prerequisite check behaviour.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {ClaudeBinaryPath} --version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the Claude binary path to a full path that Process.Start can use directly.
    /// Searches PATH for .cmd/.bat/.exe variants if ClaudeBinaryPath is just a name like "claude".
    /// </summary>
    public static string ResolveClaudeBinaryPath()
    {
        var path = ClaudeBinaryPath;

        // If it's already a rooted path that exists, use it directly
        if (Path.IsPathRooted(path) && File.Exists(path))
            return path;

        // Search PATH for the command, matching Windows PATHEXT resolution order:
        // bare name and .exe first (preserves existing behaviour for working installs),
        // then .cmd/.bat (fixes npm-installed CLI wrappers).
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { "", ".exe", ".cmd", ".bat" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, path + ext);
                if (File.Exists(candidate))
                {
                    Log.Info($"ClaudeSession: resolved '{path}' to '{candidate}'");
                    return candidate;
                }
            }
        }

        // Fallback: return as-is and let Process.Start try
        return path;
    }

    /// <summary>
    /// Marks the session as ready. Does not spawn a process yet —
    /// the first process is spawned when SendMessage is called.
    /// </summary>
    public void Start(bool dangerousMode = false)
    {
        Log.Info($"ClaudeSession.Start: dangerous={dangerousMode}, cwd={_workingDirectory}");

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
    /// </summary>
    public void SendMessage(string text)
    {
        if (State != ClaudeSessionState.Idle)
            throw new InvalidOperationException($"Cannot send message in state {State}");

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

        Log.Info($"ClaudeSession.SendMessage: launching claude with args: {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveClaudeBinaryPath(),
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

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeSession.SendMessage: failed to start process", ex);
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke($"Failed to start Claude: {ex.Message}");
            return;
        }

        if (_process == null)
        {
            Log.Error("ClaudeSession.SendMessage: Process.Start returned null");
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke("Failed to start Claude process");
            return;
        }

        Log.Info($"ClaudeSession.SendMessage: process started, PID={_process.Id}");

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        // Read stderr
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                Log.Info($"ClaudeSession STDERR: {e.Data}");
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
                Log.Error("ClaudeSession: ReadOutputLoop task faulted", t.Exception);
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
        var userMessage = new ClaudeUserMessage
        {
            Message = new ClaudeMessagePayload { Content = text }
        };
        WriteStdin(JsonSerializer.Serialize(userMessage, JsonOptions));
    }

    /// <summary>
    /// Responds to a pending permission request by allowing the tool use.
    /// </summary>
    public void AllowPermission()
    {
        if (PendingPermission == null || _process == null)
            return;

        Log.Info($"ClaudeSession.AllowPermission: {PendingPermission.ToolName}");
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

        Log.Info($"ClaudeSession.DenyPermission: {PendingPermission.ToolName}");
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

        Log.Info($"ClaudeSession.AnswerQuestion: '{answer}' for '{questionText}'");

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
        Log.Info("ClaudeSession.Stop called");

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
                    Log.Info($"ClaudeSession: process killed (exited={proc.HasExited})");
                    try { proc.Dispose(); } catch { }
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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

    private void WriteStdin(string json)
    {
        if (_process == null || _process.HasExited)
            return;

        try
        {
            Log.Info($"ClaudeSession STDIN: {(json.Length > 500 ? json[..500] + "..." : json)}");
            _process.StandardInput.WriteLine(json);
            _process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeSession: WriteStdin error", ex);
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
        Log.Info("ClaudeSession: ReadOutputLoop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(ct);
                if (line == null)
                {
                    Log.Info("ClaudeSession: ReadOutputLoop got EOF");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Reset inactivity watchdog on each line of output
                ResetInactivityTimer();

                Log.Info($"ClaudeSession STDOUT: {(line.Length > 500 ? line[..500] + "..." : line)}");

                try
                {
                    ProcessMessage(line);
                }
                catch (Exception ex)
                {
                    Log.Error("ClaudeSession: ProcessMessage error", ex);
                    ErrorOutput?.Invoke($"Parse error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("ClaudeSession: ReadOutputLoop cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("ClaudeSession: ReadOutputLoop error", ex);
            ErrorOutput?.Invoke($"Read error: {ex.Message}");
        }

        StopInactivityTimer();

        // If the loop ended while we were still Working (EOF without a result message,
        // or an exception killed the loop), recover by transitioning to Error.
        if (State == ClaudeSessionState.Working || State == ClaudeSessionState.WaitingForPermission)
        {
            Log.Warn($"ClaudeSession: ReadOutputLoop ended while in {State} — setting Error");
            ErrorOutput?.Invoke("Claude stopped responding (output stream ended unexpectedly)");
            SetState(ClaudeSessionState.Error);
        }

        Log.Info("ClaudeSession: ReadOutputLoop ended");
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
        if (subtype != "init")
            return;

        var newSessionId = GetString(root, "session_id");
        var newModel = GetString(root, "model");

        Log.Info($"ClaudeSession: system init — session={newSessionId}, model={newModel}");

        // Capture session ID for --resume on subsequent turns
        if (newSessionId != null)
            SessionId = newSessionId;
        if (newModel != null)
            Model = newModel;
    }

    private void HandleAssistantMessage(JsonElement root)
    {
        var msg = new ClaudeAssistantMessage
        {
            Uuid = GetString(root, "uuid") ?? ""
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

            Log.Info($"ClaudeSession: permission request — tool={toolName}, requestId={requestId}, toolUseId={toolUseId}");

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
            Log.Warn($"ClaudeSession: unhandled control_request subtype '{subtype}', requestId={requestId}");
        }
    }

    private void SetState(ClaudeSessionState newState)
    {
        if (State == newState)
            return;

        Log.Info($"ClaudeSession: state {State} -> {newState}");
        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        Log.Info($"ClaudeSession: process exited with code {exitCode}");

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
                Log.Warn($"ClaudeSession: no output for {InactivityTimeoutSeconds}s — firing InactivityTimeout");
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
