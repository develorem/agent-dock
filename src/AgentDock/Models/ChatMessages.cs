using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Documents;
using System.Windows.Threading;

namespace AgentDock.Models;

// Chat message view-models for the virtualizing ItemsControl.
//
// Design: past messages are facts — once finalized, they never change. They're
// modelled as immutable records (no INPC, no virtual property dispatch, no
// allocation on access). Only the *currently being built* messages mutate, and
// only those types implement INotifyPropertyChanged. On finalize, a streaming
// VM is replaced in the collection by its immutable record counterpart, after
// which it has no event subscribers and no listeners — nothing to leak.

public abstract class ChatMessageVm(Guid id)
{
    public Guid Id { get; } = id;
}

// --- Immutable finalized messages ---

public sealed class UserMessage(Guid id, string text) : ChatMessageVm(id)
{
    public string Text { get; } = text;
}

/// <summary>
/// A finalized assistant message. Past messages don't mutate — IsMarkdownView
/// is part of the immutable identity; toggling the view replaces this VM in
/// the collection with a new instance carrying the flipped flag. No INPC.
///
/// The rendered FlowDocument is built <i>lazily</i> via <see cref="MarkdownBuilder"/>
/// — a memoizing closure created at finalize time. It is invoked (and the result
/// cached) only when the bubble is actually realized in a visible tab, so
/// background tabs and off-screen messages never pay the MdXaml + AvalonEdit build
/// cost. The same closure is passed to the toggled instance so flipping the view
/// doesn't re-parse. Null for single-line messages (rendered as plain text).
/// </summary>
public sealed class AssistantMessage(
    Guid id,
    string text,
    bool isMarkdownView,
    bool hasMarkdownToggle,
    Func<FlowDocument>? markdownBuilder) : ChatMessageVm(id)
{
    public string Text { get; } = text;
    public bool IsMarkdownView { get; } = isMarkdownView;
    /// <summary>True for multi-line assistant text — the only case where the
    /// source/rendered toggle button is meaningful (single-line messages have
    /// nothing for the renderer to add).</summary>
    public bool HasMarkdownToggle { get; } = hasMarkdownToggle;

    /// <summary>Memoizing factory for the rendered document; null if this message
    /// has no markdown. Shared across toggled instances so it builds at most once.</summary>
    public Func<FlowDocument>? MarkdownBuilder { get; } = markdownBuilder;

    /// <summary>Builds (first call) or returns the cached rendered FlowDocument,
    /// or null if this message renders as plain text.</summary>
    public FlowDocument? GetMarkdown() => MarkdownBuilder?.Invoke();
}

public sealed class ThinkingMessage(Guid id, string text, bool isExpanded) : ChatMessageVm(id)
{
    public string Text { get; } = text;
    public bool IsExpanded { get; } = isExpanded;
}

public sealed record ToolEntry(string Name, string FormattedInput);

public sealed class ExecutionMessage(Guid id, IReadOnlyList<ToolEntry> tools, bool isExpanded) : ChatMessageVm(id)
{
    public IReadOnlyList<ToolEntry> Tools { get; } = tools;
    public bool IsExpanded { get; } = isExpanded;
}

public sealed class SystemMessage(Guid id, string text, bool isWarning) : ChatMessageVm(id)
{
    public string Text { get; } = text;
    public bool IsWarning { get; } = isWarning;
}

/// <summary>
/// Transient placeholder shown after the user sends a message, until the first
/// delta or content_block_start arrives. Removed from the collection once
/// real content begins streaming.
/// </summary>
public sealed class WaitingMessage(Guid id) : ChatMessageVm(id);

// --- Live messages (only these need INPC) ---

/// <summary>
/// Base for the two streaming text VMs (assistant and thinking). Deltas are
/// accumulated in a <see cref="StringBuilder"/> and flushed to <see cref="Text"/>
/// at ~30 fps via a <see cref="DispatcherTimer"/>, so a fast stream produces
/// one binding-update per frame instead of one per delta.
/// </summary>
public abstract class StreamingTextMessage(Guid id) : ChatMessageVm(id), INotifyPropertyChanged
{
    // ONE shared flush timer drives every streaming VM across all sessions/tabs,
    // instead of one DispatcherTimer per VM. Deltas accumulate in each VM's
    // buffer; the timer flushes all dirty VMs at ~10 fps. All access is on the UI
    // thread (deltas are posted there), so the static set needs no locking.
    private static readonly HashSet<StreamingTextMessage> s_dirty = [];
    private static DispatcherTimer? s_timer;
    private const int FlushIntervalMs = 100;

    private readonly StringBuilder _buffer = new();
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            // Authoritative reset (e.g. when the final assistant message arrives
            // with the full text — supersedes anything we'd accumulated).
            _buffer.Clear();
            s_dirty.Remove(this);
            if (_text == value) return;
            _text = value;
            OnPropertyChanged(nameof(Text));
        }
    }

    /// <summary>Append a stream delta. Coalesced via the shared flush timer.</summary>
    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _buffer.Append(text);
        s_dirty.Add(this);
        if (s_timer != null) return;

        s_timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs)
        };
        s_timer.Tick += (_, _) => FlushAll();
        s_timer.Start();
    }

    private static void FlushAll()
    {
        // Snapshot: flushing fires PropertyChanged, which we don't want to race
        // against set mutation. Buffers are drained, so the set is then empty.
        foreach (var vm in s_dirty.ToList())
            vm.Flush();
        s_dirty.Clear();
        if (s_timer != null)
        {
            s_timer.Stop();
            s_timer = null;
        }
    }

    /// <summary>
    /// Drains the buffer into <see cref="Text"/> and fires a single
    /// <see cref="PropertyChanged"/>. Called by the shared timer and also from
    /// <c>FinalizeStreaming</c> so the trailing delta isn't dropped when the turn
    /// completes between timer ticks.
    /// </summary>
    public void Flush()
    {
        s_dirty.Remove(this);
        if (_buffer.Length == 0) return;
        _text += _buffer.ToString();
        _buffer.Clear();
        OnPropertyChanged(nameof(Text));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The currently-streaming assistant text. On turn completion it's replaced
/// in the messages collection by an immutable <see cref="AssistantMessage"/>.
/// </summary>
public sealed class StreamingAssistantMessage(Guid id) : StreamingTextMessage(id);

/// <summary>
/// The currently-streaming thinking text. Most models (e.g. claude-opus-4-7
/// in summary mode) don't send any thinking_delta events, in which case this
/// VM is never created. When a model does stream thinking, the VM is added
/// to the collection on the first delta and replaced by an immutable
/// <see cref="ThinkingMessage"/> when the content_block_stop arrives.
/// </summary>
public sealed class StreamingThinkingMessage(Guid id) : StreamingTextMessage(id);

/// <summary>
/// Execution bubble currently being filled with tool_use entries for the
/// active turn. Replaced by an immutable <see cref="ExecutionMessage"/>
/// when the turn finishes.
/// </summary>
public sealed class BuildingExecutionMessage(Guid id) : ChatMessageVm(id), INotifyPropertyChanged
{
    public ObservableCollection<ToolEntry> Tools { get; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Inactivity warning bubble. <see cref="ElapsedText"/> ticks once per second
/// while shown. <see cref="OnWait"/> and <see cref="OnKill"/> are invoked by
/// the bound buttons.
/// </summary>
public sealed class InactivityWarning(Guid id) : ChatMessageVm(id), INotifyPropertyChanged
{
    private string _elapsedText = "";
    public string ElapsedText
    {
        get => _elapsedText;
        set { if (_elapsedText != value) { _elapsedText = value; OnPropertyChanged(); } }
    }

    public Action? OnWait { get; init; }
    public Action? OnKill { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
