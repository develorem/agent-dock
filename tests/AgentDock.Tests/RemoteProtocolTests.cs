using System.Text.Json;
using AgentDock.Models;
using AgentDock.Services;
using AgentDock.Services.Remote;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for the remote-session wire format.
///
/// WHY THESE MATTER MORE THAN THEY LOOK: every frame and every chat op is serialized through
/// System.Text.Json polymorphism, which is driven by an attribute list. Forgetting one
/// <c>[JsonDerivedType]</c> compiles perfectly and then throws at runtime the first time that
/// op crosses the wire — meaning a remote session that works fine until Claude happens to spawn
/// a subagent. The round-trip tests below exist so that failure is caught here instead.
///
/// HOW TO ADD A CASE: when you add a frame type or a ChatOp, add it to the round-trip theory
/// data. If the discriminator is missing, the test fails immediately.
/// </summary>
public class RemoteProtocolTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        var back = JsonSerializer.Deserialize<T>(json);
        Assert.NotNull(back);
        return back!;
    }

    // ---------------------------------------------------------------- chat ops

    public static TheoryData<ChatOp> AllChatOps() =>
    [
        new RemoveInactivityOp(),
        new AppendThinkingOp("thinking text"),
        new CommentaryOp("commentary"),
        new FinalizeThinkingOp(),
        new EnsureExecutionOp(),
        new AddToolOp("Bash", "ls -la"),
        new AddSubagentOp("Explore", "find the thing"),
        new AddSubagentReportOp("Explore", "claude-sonnet-5", "report body"),
        new ActivityCountsOp(2, 1, 0),
        new FinalizeExecutionOp(),
        new PostAnswerOp("the answer"),
        new TurnCompleteOp(new ClaudeResultMessage
        {
            Subtype = "success",
            IsError = false,
            TotalCostUsd = 0.42,
            NumTurns = 3,
            DurationMs = 1234,
            InputTokens = 10,
            OutputTokens = 20,
            CacheReadInputTokens = 30,
            CacheCreationInputTokens = 40,
        }),
    ];

    [Theory]
    [MemberData(nameof(AllChatOps))]
    public void EveryChatOpRoundTrips(ChatOp op)
    {
        var back = RoundTrip(op);
        Assert.Equal(op.GetType(), back.GetType());

        // TurnCompleteOp carries ClaudeResultMessage, which is a class — the record's
        // synthesized equality compares it by reference, so value equality is asserted
        // on its payload in TurnCompletePreservesTheResultPayload instead.
        if (op is not TurnCompleteOp)
            Assert.Equal(op, back);
    }

    [Fact]
    public void TurnCompletePreservesTheResultPayload()
    {
        var op = (TurnCompleteOp)RoundTrip<ChatOp>(new TurnCompleteOp(new ClaudeResultMessage
        {
            Subtype = "success",
            TotalCostUsd = 1.5,
            OutputTokens = 99,
            Errors = ["boom"],
        }));

        Assert.Equal("success", op.Result.Subtype);
        Assert.Equal(1.5, op.Result.TotalCostUsd);
        Assert.Equal(99, op.Result.OutputTokens);
        Assert.Equal(["boom"], op.Result.Errors);
    }

    // ---------------------------------------------------------------- transcript

    [Fact]
    public void TranscriptMessagesRoundTripThroughTheirBaseType()
    {
        SerializedChatMessage[] messages =
        [
            new SerializedUserMessage("id-1", "hello", [[1, 2, 3]]),
            new SerializedAssistantMessage("id-2", "**bold**", IsMarkdownView: true, HasMarkdownToggle: true),
            new SerializedSystemMessage("id-3", "warning", IsWarning: true),
            new SerializedActivityMessage("id-4", "Worked for 3s", IsExpanded: false,
            [
                new SerializedThinkingEntry("pondering"),
                new SerializedToolEntry("Read", "file.cs"),
                new SerializedSubagentEntry("Explore", "look around"),
                new SerializedSubagentReportEntry("Explore", "claude-sonnet-5", "found it"),
            ]),
        ];

        foreach (var message in messages)
        {
            var back = RoundTrip(message);
            Assert.Equal(message.GetType(), back.GetType());
        }
    }

    [Fact]
    public void ActivityEntryOrderIsPreserved()
    {
        var original = new SerializedActivityMessage("a", "hdr", true,
        [
            new SerializedThinkingEntry("one"),
            new SerializedToolEntry("Bash", "echo"),
            new SerializedThinkingEntry("two"),
        ]);

        var back = (SerializedActivityMessage)RoundTrip<SerializedChatMessage>(original);

        Assert.Collection(back.Entries,
            e => Assert.Equal("one", Assert.IsType<SerializedThinkingEntry>(e).Text),
            e => Assert.Equal("Bash", Assert.IsType<SerializedToolEntry>(e).Name),
            e => Assert.Equal("two", Assert.IsType<SerializedThinkingEntry>(e).Text));
    }

    // ---------------------------------------------------------------- frames

    public static TheoryData<RemoteFrame> AllFrames() =>
    [
        new ServerHelloMsg(RemoteProtocol.Version, "SERVER", "0.11.0", "AB CD"),
        new ClientHelloCmd(RemoteProtocol.Version, "CLIENT", "0.11.0"),
        new AuthChallengeMsg("nonce"),
        new AuthProofCmd("proof", null),
        new AuthResultMsg(true, "token", DisconnectReason.Unknown, null, null),
        new PingFrame(123),
        new PongFrame(123),
        new DisconnectMsg(DisconnectReason.ServerStopped, "bye"),
        new HostSnapshotMsg("SERVER", [new RemoteProjectDescriptor("id", "name", "C:\\p", "folder", "#fff", [7], true)]),
        new ProjectAddedMsg(new RemoteProjectDescriptor("id", "name", "C:\\p", null, null, null, false)),
        new ProjectRemovedMsg("id", "closed"),
        new ProjectDeltaMsg("id", 4, [new AddToolOp("Bash", "ls")]),
        new SessionStateMsg("id", new SessionStateSnapshot(
            ClaudeSessionState.Working, "opus", "Work", false, true, "Working...", 1.0, 1, 2, 3, 4)),
        new PermissionRequestMsg("id", "req", "Bash", "{}"),
        new PermissionResolvedMsg("id", "req"),
        new GitStatusMsg("id", new GitStatusSnapshot(true, "main", null,
            [new GitFileEntrySnapshot("a.cs", GitFileStatus.Modified, false)])),
        new ProjectSettingsMsg("id", new ProjectSettingsSnapshot("n", "d", 13, [new RemoteTodoItem("t", true)])),
        new DirectoryListingMsg("id", "req", "", [new RemoteFileEntry("src", "src", true)], null),
        new FileContentMsg("id", "req", "a.cs", "v1", "text", null, false, false, null),
        new FileInvalidatedMsg("id", ["a.cs"]),
        new TranscriptAppendMsg("id", 5, new SerializedSystemMessage("m", "hi", false)),
        new SendMessageCmd("msg-1", "id", "hello", null),
        new AllowPermissionCmd("id", "req"),
        new DenyPermissionCmd("id", "req", "no"),
        new AnswerQuestionCmd("id", "req", "q", "a"),
        new ListDirectoryCmd("id", "req", "src"),
        new ReadFileCmd("id", "req", "a.cs", "v1"),
        new ReadDiffCmd("id", "req", "a.cs", true),
        new WatchDirectoryCmd("id", "src", true),
        new UpdateTodoItemsCmd("id", [new RemoteTodoItem("t", false)]),
        new LocalCommandCmd("id", "/clear"),
        new AckCmd("id", 9),
        new ResyncCmd("id"),
    ];

    [Theory]
    [MemberData(nameof(AllFrames))]
    public void EveryFrameRoundTrips(RemoteFrame frame)
    {
        var back = RoundTrip(frame);
        Assert.Equal(frame.GetType(), back.GetType());
    }

    [Fact]
    public void BinaryPayloadsSurviveTheRoundTrip()
    {
        var bytes = new byte[4096];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);

        var back = (FileContentMsg)RoundTrip<RemoteFrame>(
            new FileContentMsg("id", "req", "logo.png", "v", null, bytes, false, false, null));

        Assert.Equal(bytes, back.Bytes);
    }

    [Fact]
    public void DeltaSequenceNumberSurvives()
    {
        // The sequence number is what makes reconnects detectable rather than silently
        // duplicating transcript content, so it is worth asserting explicitly.
        var back = (ProjectDeltaMsg)RoundTrip<RemoteFrame>(
            new ProjectDeltaMsg("id", 987654321, [new PostAnswerOp("x")]));

        Assert.Equal(987654321, back.Seq);
    }
}
