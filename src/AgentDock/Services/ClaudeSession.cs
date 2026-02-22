using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentDock.Models;

namespace AgentDock.Services;

/// <summary>
/// Manages a single Claude Code CLI subprocess for one project.
/// Communicates via the JSON-lines protocol over stdin/stdout.
/// </summary>
public class ClaudeSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _workingDirectory;
    private Process? _process;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private bool _disposed;

    public ClaudeSessionState State { get; private set; } = ClaudeSessionState.NotStarted;
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public bool IsDangerousMode { get; private set; }
    public ClaudePermissionRequest? PendingPermission { get; private set; }

    // --- Events ---

    /// <summary>Raised when session state changes.</summary>
    public event Action<ClaudeSessionState>? StateChanged;

    /// <summary>Raised when the system init message is received.</summary>
    public event Action<ClaudeSystemInit>? Initialized;

    /// <summary>Raised when a complete assistant message is received.</summary>
    public event Action<ClaudeAssistantMessage>? AssistantMessageReceived;

    /// <summary>Raised when a streaming text delta arrives.</summary>
    public event Action<ClaudeStreamDelta>? StreamDelta;

    /// <summary>Raised when Claude requests permission to use a tool.</summary>
    public event Action<ClaudePermissionRequest>? PermissionRequested;

    /// <summary>Raised when a result message is received (session turn complete).</summary>
    public event Action<ClaudeResultMessage>? ResultReceived;

    /// <summary>Raised when raw text arrives on stderr (for diagnostics).</summary>
    public event Action<string>? ErrorOutput;

    /// <summary>Raised when the process exits unexpectedly or normally.</summary>
    public event Action<int>? ProcessExited;

    public ClaudeSession(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Checks if the `claude` CLI is available in PATH.
    /// </summary>
    public static bool IsClaudeAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
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
    /// Starts a Claude Code session.
    /// </summary>
    /// <param name="dangerousMode">If true, skips all permission prompts.</param>
    public void Start(bool dangerousMode = false)
    {
        if (State != ClaudeSessionState.NotStarted && State != ClaudeSessionState.Exited && State != ClaudeSessionState.Error)
            throw new InvalidOperationException($"Cannot start session in state {State}");

        IsDangerousMode = dangerousMode;

        var args = "-p --output-format stream-json --input-format stream-json --verbose --include-partial-messages";

        if (dangerousMode)
            args += " --dangerously-skip-permissions";

        var psi = new ProcessStartInfo
        {
            FileName = "claude",
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

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke($"Failed to start Claude: {ex.Message}");
            return;
        }

        if (_process == null)
        {
            SetState(ClaudeSessionState.Error);
            ErrorOutput?.Invoke("Failed to start Claude process");
            return;
        }

        _stdin = _process.StandardInput;
        _stdin.AutoFlush = true;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        // Start reading stderr in background
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                ErrorOutput?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();

        // Start reading stdout NDJSON
        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadOutputLoop(_process.StandardOutput, _readCts.Token));

        SetState(ClaudeSessionState.Initializing);
    }

    /// <summary>
    /// Sends a user message to the Claude session.
    /// </summary>
    public void SendMessage(string text)
    {
        if (State != ClaudeSessionState.Idle)
            throw new InvalidOperationException($"Cannot send message in state {State}");

        var message = new ClaudeUserMessage
        {
            Message = new ClaudeMessagePayload
            {
                Role = "user",
                Content = text
            }
        };

        WriteJson(message);
        SetState(ClaudeSessionState.Working);
    }

    /// <summary>
    /// Responds to a pending permission request by allowing the tool use.
    /// </summary>
    public void AllowPermission()
    {
        if (PendingPermission == null)
            return;

        var response = new ClaudeControlResponse
        {
            RequestId = PendingPermission.RequestId,
            Response = new ClaudeControlResponseBody
            {
                ResponseData = new ClaudePermissionAllow()
            }
        };

        WriteJson(response);
        PendingPermission = null;
        SetState(ClaudeSessionState.Working);
    }

    /// <summary>
    /// Responds to a pending permission request by denying the tool use.
    /// </summary>
    public void DenyPermission(string? reason = null)
    {
        if (PendingPermission == null)
            return;

        var response = new ClaudeControlResponse
        {
            RequestId = PendingPermission.RequestId,
            Response = new ClaudeControlResponseBody
            {
                ResponseData = new ClaudePermissionDeny
                {
                    Message = reason ?? "User denied this action"
                }
            }
        };

        WriteJson(response);
        PendingPermission = null;
        SetState(ClaudeSessionState.Working);
    }

    /// <summary>
    /// Gracefully stops the Claude session.
    /// </summary>
    public void Stop()
    {
        if (_process == null || _process.HasExited)
            return;

        try
        {
            // Close stdin to signal EOF — Claude CLI should exit gracefully
            _stdin?.Close();
            _stdin = null;

            // Give it a moment to exit
            if (!_process.WaitForExit(3000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _readCts?.Cancel();
        Stop();
        _process?.Dispose();
        _readCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Internal ---

    private void WriteJson<T>(T message)
    {
        if (_stdin == null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            _stdin.WriteLine(json);
        }
        catch (Exception ex)
        {
            ErrorOutput?.Invoke($"Write error: {ex.Message}");
        }
    }

    private async Task ReadOutputLoop(StreamReader stdout, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(ct);
                if (line == null)
                    break; // EOF

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    ProcessMessage(line);
                }
                catch (Exception ex)
                {
                    ErrorOutput?.Invoke($"Parse error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            ErrorOutput?.Invoke($"Read error: {ex.Message}");
        }
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

        var init = new ClaudeSystemInit
        {
            SessionId = GetString(root, "session_id") ?? "",
            Model = GetString(root, "model") ?? "",
            Cwd = GetString(root, "cwd") ?? "",
            PermissionMode = GetString(root, "permissionMode") ?? "",
            Tools = root.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(t => t.GetString() ?? "").ToArray()
                : []
        };

        SessionId = init.SessionId;
        Model = init.Model;

        SetState(ClaudeSessionState.Idle);
        Initialized?.Invoke(init);
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

        if (!evt.TryGetProperty("delta", out var delta))
            return;

        var deltaType = GetString(delta, "type");
        if (deltaType != "text_delta")
            return;

        var text = GetString(delta, "text") ?? "";
        var index = evt.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

        StreamDelta?.Invoke(new ClaudeStreamDelta
        {
            Text = text,
            ContentBlockIndex = index
        });
    }

    private void HandleResultMessage(JsonElement root)
    {
        List<string>? errorList = null;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            errorList = errors.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        var result = new ClaudeResultMessage
        {
            Subtype = GetString(root, "subtype") ?? "",
            IsError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean(),
            Result = GetString(root, "result"),
            TotalCostUsd = root.TryGetProperty("total_cost_usd", out var cost) ? cost.GetDouble() : null,
            NumTurns = root.TryGetProperty("num_turns", out var turns) ? turns.GetInt32() : null,
            DurationMs = root.TryGetProperty("duration_ms", out var dur) ? dur.GetInt64() : null,
            Errors = errorList
        };

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

            var permReq = new ClaudePermissionRequest
            {
                RequestId = requestId,
                ToolName = toolName,
                Input = input
            };

            PendingPermission = permReq;
            SetState(ClaudeSessionState.WaitingForPermission);
            PermissionRequested?.Invoke(permReq);
        }
    }

    private void SetState(ClaudeSessionState newState)
    {
        if (State == newState)
            return;

        State = newState;
        StateChanged?.Invoke(newState);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;

        if (State != ClaudeSessionState.Exited && State != ClaudeSessionState.Error)
        {
            SetState(exitCode == 0 ? ClaudeSessionState.Exited : ClaudeSessionState.Error);
        }

        ProcessExited?.Invoke(exitCode);
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
