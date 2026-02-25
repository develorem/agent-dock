using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentDock.Models;
using AgentDock.Services;

namespace AgentDock.Controls;

public partial class TodoListControl : UserControl
{
    private string _projectPath = "";
    private readonly ObservableCollection<TodoItem> _items = [];

    public TodoListControl()
    {
        InitializeComponent();
        TodoItemsList.ItemsSource = _items;
    }

    public void LoadProject(string projectPath)
    {
        _projectPath = projectPath;
        _items.Clear();

        var settings = ProjectSettingsManager.Load(projectPath);
        if (settings.TodoItems != null)
        {
            foreach (var item in settings.TodoItems)
                _items.Add(item);
        }

        UpdatePlaceholder();
    }

    private void Save()
    {
        var items = _items.Count > 0 ? _items.ToList() : null;
        ProjectSettingsManager.Update(_projectPath, s => s.TodoItems = items);
    }

    private void UpdatePlaceholder()
    {
        PlaceholderText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddItemPanel.Visibility = Visibility.Visible;
        NewItemTextBox.Text = "";
        NewItemTextBox.Focus();
    }

    private void NewItemTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNewItem();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddItemPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void NewItemTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitNewItem();
    }

    private void CommitNewItem()
    {
        var text = NewItemTextBox.Text.Trim();
        NewItemTextBox.Text = "";
        AddItemPanel.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(text))
            return;

        _items.Add(new TodoItem { Text = text });
        Save();
        UpdatePlaceholder();
    }

    private void TodoCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        Save();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is TodoItem item)
        {
            _items.Remove(item);
            Save();
            UpdatePlaceholder();
        }
    }
}
