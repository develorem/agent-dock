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

/// <summary>One green tool-execution line inside an <see cref="ActivityMessage"/>.</summary>
public sealed record ToolEntry(string Name, string FormattedInput);

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
/// A streaming text buffer with coalesced UI updates. Deltas are accumulated in a
/// <see cref="StringBuilder"/> and flushed to <see cref="Text"/> at ~10 fps via a
/// shared <see cref="DispatcherTimer"/>, so a fast stream produces one binding-update
/// per frame instead of one per delta. Used by <see cref="ActivityThinkingEntry"/>.
/// </summary>
public abstract class StreamingText : INotifyPropertyChanged
{
    // ONE shared flush timer drives every streaming buffer across all sessions/tabs,
    // instead of one DispatcherTimer per buffer. Deltas accumulate in each buffer;
    // the timer flushes all dirty buffers. All access is on the UI thread (deltas
    // are posted there), so the static set needs no locking.
    private static readonly HashSet<StreamingText> s_dirty = [];
    private static DispatcherTimer? s_timer;
    private const int FlushIntervalMs = 100;

    private readonly StringBuilder _buffer = new();
    private string _text = "";

    public string Text
    {
        get => _text;
        set
        {
            // Authoritative reset (supersedes anything we'd accumulated).
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
    /// <see cref="PropertyChanged"/>. Called by the shared timer and also on
    /// finalize so the trailing delta isn't dropped between timer ticks.
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
/// A gray thinking/commentary block inside an <see cref="ActivityMessage"/>. Thinking
/// streams live (deltas appended via <see cref="StreamingText.AppendText"/>); commentary
/// is appended whole. Frozen via <see cref="StreamingText.Flush"/> at turn end.
/// </summary>
public sealed class ActivityThinkingEntry : StreamingText;

/// <summary>
/// The single unified "activity" bubble for one turn: an ordered, interleaved list of
/// gray <see cref="ActivityThinkingEntry"/> (thinking + commentary) and green
/// <see cref="ToolEntry"/> (tool executions). It replaces the old separate thinking and
/// execution bubbles — one bubble per turn instead of many.
///
/// Unlike the immutable finalized messages, this stays a single mutable INPC VM for its
/// whole life: it grows during the turn, then is <see cref="Freeze"/>d (collapsed, header
/// switched to a duration summary) at turn end. There is at most one of these per turn,
/// so the INPC overhead the perf overhaul avoided on long history doesn't apply here.
///
/// While building, <see cref="Header"/> is animated with a whimsical verb by the control;
/// on freeze it becomes a "Worked for Ns" summary. <see cref="IsExpanded"/> starts true so
/// the user watches progress, and is set false on freeze for the clean collapsed end state.
/// </summary>
public sealed class ActivityMessage(Guid id) : ChatMessageVm(id), INotifyPropertyChanged
{
    public ObservableCollection<object> Entries { get; } = [];

    // The currently-open thinking block, if any. Consecutive thinking/commentary
    // coalesces into it; a tool execution closes it so the next thinking starts fresh.
    private ActivityThinkingEntry? _openThinking;

    private string _header = "";
    public string Header
    {
        get => _header;
        set { if (_header != value) { _header = value; OnPropertyChanged(); } }
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool HasEntries => Entries.Count > 0;

    /// <summary>Append live thinking delta, opening a thinking block if needed.</summary>
    public void AppendThinking(string text)
    {
        EnsureOpenThinking();
        _openThinking!.AppendText(text);
    }

    /// <summary>Append a whole commentary block, coalesced into the open thinking
    /// block (separated) so consecutive gray content reads as one region.</summary>
    public void AddCommentary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_openThinking == null)
            EnsureOpenThinking();
        else
            _openThinking.AppendText("\n\n");
        _openThinking!.AppendText(text);
    }

    /// <summary>Close the open thinking block so subsequent thinking starts a new one.</summary>
    public void CloseThinking() => _openThinking = null;

    /// <summary>Add a green tool-execution line; closes any open thinking block first.</summary>
    public void AddTool(string name, string formattedInput)
    {
        CloseThinking();
        Entries.Add(new ToolEntry(name, formattedInput));
    }

    private void EnsureOpenThinking()
    {
        if (_openThinking != null) return;
        _openThinking = new ActivityThinkingEntry();
        Entries.Add(_openThinking);
    }

    /// <summary>Flush all streaming thinking blocks so their trailing deltas land,
    /// then close the open block. Called when the turn finalizes.</summary>
    public void Freeze()
    {
        foreach (var entry in Entries)
            (entry as ActivityThinkingEntry)?.Flush();
        _openThinking = null;
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
