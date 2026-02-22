using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDock.Models;

// --- Enums ---

public enum ClaudeSessionState
{
    NotStarted,
    Initializing,
    Idle,
    Working,
    WaitingForPermission,
    Exited,
    Error
}

// --- Outgoing messages (written to stdin) ---

public class ClaudeUserMessage
{
    [JsonPropertyName("type")]
    public string Type => "user";

    [JsonPropertyName("message")]
    public ClaudeMessagePayload Message { get; init; } = null!;
}

public class ClaudeMessagePayload
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";
}

public class ClaudeControlResponse
{
    [JsonPropertyName("type")]
    public string Type => "control_response";

    [JsonPropertyName("request_id")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("response")]
    public ClaudeControlResponseBody Response { get; init; } = null!;
}

public class ClaudeControlResponseBody
{
    [JsonPropertyName("subtype")]
    public string Subtype => "success";

    [JsonPropertyName("response")]
    public object ResponseData { get; init; } = null!;
}

public class ClaudePermissionAllow
{
    [JsonPropertyName("behavior")]
    public string Behavior => "allow";

    [JsonPropertyName("updatedInput")]
    public JsonElement? UpdatedInput { get; init; }
}

public class ClaudePermissionDeny
{
    [JsonPropertyName("behavior")]
    public string Behavior => "deny";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "User denied this action";
}

// --- Incoming messages (read from stdout) ---

/// <summary>
/// Raw envelope for any NDJSON line from Claude CLI.
/// We deserialize to JsonElement first, then inspect "type" to decide the concrete shape.
/// </summary>
public class ClaudeRawMessage
{
    public string Type { get; init; } = "";
    public string? Subtype { get; init; }
    public string? SessionId { get; init; }
    public string? Uuid { get; init; }
    public JsonElement Raw { get; init; }
}

/// <summary>
/// Parsed system init message.
/// </summary>
public class ClaudeSystemInit
{
    public string SessionId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string[] Tools { get; init; } = [];
    public string PermissionMode { get; init; } = "";
}

/// <summary>
/// A content block in an assistant message.
/// </summary>
public class ClaudeContentBlock
{
    public string Type { get; init; } = "";
    public string? Text { get; init; }

    // For tool_use blocks
    public string? Id { get; init; }
    public string? Name { get; init; }
    public JsonElement? Input { get; init; }
}

/// <summary>
/// Parsed assistant message.
/// </summary>
public class ClaudeAssistantMessage
{
    public string Uuid { get; init; } = "";
    public List<ClaudeContentBlock> Content { get; init; } = [];
}

/// <summary>
/// A streaming text delta event.
/// </summary>
public class ClaudeStreamDelta
{
    public string Text { get; init; } = "";
    public int ContentBlockIndex { get; init; }
}

/// <summary>
/// A permission request from Claude (control_request with subtype can_use_tool).
/// </summary>
public class ClaudePermissionRequest
{
    public string RequestId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public JsonElement Input { get; init; }
}

/// <summary>
/// Result message emitted when Claude finishes.
/// </summary>
public class ClaudeResultMessage
{
    public string Subtype { get; init; } = "";
    public bool IsError { get; init; }
    public string? Result { get; init; }
    public double? TotalCostUsd { get; init; }
    public int? NumTurns { get; init; }
    public long? DurationMs { get; init; }
    public List<string>? Errors { get; init; }
}
