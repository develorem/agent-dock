using AgentDock.Models;
using AgentDock.Services.Remote;

namespace AgentDock.Controls;

/// <summary>
/// Remote surface for the todo list.
///
/// Worth stating because it is easy to get wrong: todo items are <i>project</i> state, not app
/// state — they live in the project's own <c>.agentdock/settings.json</c>, on the server's disk.
/// A remote edit therefore has to round-trip to the server, or the client would appear to save
/// and actually write nothing.
/// </summary>
public partial class TodoListControl
{
    private IRemoteProjectSettingsChannel? _remoteChannel;
    private string? _remoteProjectId;

    public bool IsRemote => _remoteChannel != null;

    public void EnterRemoteMode(IRemoteProjectSettingsChannel channel, string projectId)
    {
        _remoteChannel = channel;
        _remoteProjectId = projectId;
    }

    /// <summary>Applies the item list pushed by the server, patching rather than rebuilding.</summary>
    public void ApplyRemoteItems(List<RemoteTodoItem> items)
    {
        // Reconcile in place so an in-progress edit or the scroll position is not thrown away by
        // a push that arrived while the user was reading.
        while (_items.Count > items.Count)
            _items.RemoveAt(_items.Count - 1);

        for (var i = 0; i < items.Count; i++)
        {
            if (i < _items.Count)
            {
                if (_items[i].Text != items[i].Text) _items[i].Text = items[i].Text;
                if (_items[i].IsCompleted != items[i].IsCompleted) _items[i].IsCompleted = items[i].IsCompleted;
            }
            else
            {
                _items.Add(new TodoItem { Text = items[i].Text, IsCompleted = items[i].IsCompleted });
            }
        }

        UpdatePlaceholder();
    }

    /// <summary>Sends the current list to the server instead of writing it locally.</summary>
    private bool TrySaveRemote()
    {
        if (_remoteChannel == null || _remoteProjectId == null) return false;

        _remoteChannel.UpdateTodoItems(
            _remoteProjectId,
            _items.Select(i => new RemoteTodoItem(i.Text, i.IsCompleted)).ToList());

        return true;
    }
}

/// <summary>Remote surface for the project description panel — read-only over the wire.</summary>
public partial class ProjectDescriptionControl
{
    private bool _isRemote;

    public bool IsRemote => _isRemote;

    /// <summary>
    /// Switches to remote mode. Editing the description opens the project settings dialog, which
    /// writes to the project folder — so the affordance is withdrawn rather than left to fail
    /// silently against a path that does not exist on this machine.
    /// </summary>
    public void EnterRemoteMode()
    {
        _isRemote = true;
        SettingsButton.Visibility = System.Windows.Visibility.Collapsed;
    }

    /// <summary>Applies description and font size pushed by the server.</summary>
    public void ApplyRemoteSettings(string? description, double? fontSize)
    {
        SetDescription(description);
        if (fontSize.HasValue) ApplyFontSize(fontSize.Value);
    }
}

/// <summary>
/// What the project-scoped panels need from their connection: a way to write project settings back
/// to the machine that owns them.
/// </summary>
public interface IRemoteProjectSettingsChannel
{
    void UpdateTodoItems(string projectId, List<RemoteTodoItem> items);
}
