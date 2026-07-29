using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PiCompanion.Application.Skills;
using PiCompanion.Desktop.Localization;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace PiCompanion.Desktop.Skills;

internal sealed class SkillCompletionController : IDisposable
{
    private const int MaximumSuggestions = 8;
    private readonly WpfTextBox _textBox;
    private readonly Popup _popup;
    private readonly WpfListBox _list;
    private readonly TextBlock _status;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SkillCompletionItem>>> _loadSkills;
    private CancellationTokenSource? _loadCancellation;
    private IReadOnlyList<SkillCompletionItem>? _skills;
    private string? _lastQuery;
    private bool _disposed;

    public SkillCompletionController(
        WpfTextBox textBox,
        Popup popup,
        WpfListBox list,
        TextBlock status,
        Func<CancellationToken, Task<IReadOnlyList<SkillCompletionItem>>> loadSkills)
    {
        _textBox = textBox;
        _popup = popup;
        _list = list;
        _status = status;
        _loadSkills = loadSkills;
        _textBox.TextChanged += OnTextChanged;
        _list.PreviewMouseLeftButtonUp += OnSuggestionMouseLeftButtonUp;
    }

    public bool HandlePreviewKeyDown(WpfKeyEventArgs e)
    {
        if (!_popup.IsOpen || !_textBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Tab:
            case Key.Enter:
                CommitSelection();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    public void Close()
    {
        _loadCancellation?.Cancel();
        _popup.IsOpen = false;
    }

    public bool CommitSelection()
    {
        if (!_popup.IsOpen || _list.SelectedItem is not SkillCompletionItem item)
        {
            return false;
        }

        ApplySuggestion(item);
        return true;
    }

    public void Invalidate()
    {
        _skills = null;
        _lastQuery = null;
        Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textBox.TextChanged -= OnTextChanged;
        _list.PreviewMouseLeftButtonUp -= OnSuggestionMouseLeftButtonUp;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
    }

    private async void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!SkillCompletionQuery.TryParse(_textBox.Text, out var query))
        {
            Close();
            _skills = null;
            _lastQuery = null;
            return;
        }

        if (string.Equals(query, _lastQuery, StringComparison.Ordinal))
        {
            return;
        }

        _lastQuery = query;
        if (_skills is null)
        {
            await LoadAndShowAsync(query);
            return;
        }

        ShowMatches(query);
    }

    private async Task LoadAndShowAsync(string query)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _list.ItemsSource = null;
        _status.Text = DesktopLocalizer.Text("正在读取可用技能…", "Reading available skills…");
        _status.Visibility = Visibility.Visible;
        _popup.IsOpen = true;
        try
        {
            _skills = await _loadSkills(cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !SkillCompletionQuery.TryParse(_textBox.Text, out var currentQuery) ||
                !string.Equals(query, currentQuery, StringComparison.Ordinal))
            {
                return;
            }

            ShowMatches(query);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                _list.ItemsSource = null;
                _status.Text = DesktopLocalizer.Text(
                    $"读取技能失败：{exception.Message}",
                    $"Unable to read skills: {exception.Message}");
                _status.Visibility = Visibility.Visible;
                _popup.IsOpen = true;
            }
        }
    }

    private void ShowMatches(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var matches = (_skills ?? [])
            .Where(skill =>
                normalized.Length == 0 ||
                skill.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                skill.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(MaximumSuggestions)
            .ToArray();
        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Length > 0 ? 0 : -1;
        _status.Text = matches.Length == 0
            ? DesktopLocalizer.Text("没有匹配的可用技能", "No matching available skills")
            : DesktopLocalizer.Text(
                "选择后将插入标准 /skill: 技能调用",
                "Selection inserts a standard /skill: invocation");
        _status.Visibility = Visibility.Visible;
        _popup.IsOpen = true;
    }

    private void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
        {
            return;
        }

        var index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex;
        _list.SelectedIndex = (index + delta + _list.Items.Count) % _list.Items.Count;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnSuggestionMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = source is null
            ? null
            : ItemsControl.ContainerFromElement(_list, source) as ListBoxItem;
        if (container?.DataContext is SkillCompletionItem item)
        {
            ApplySuggestion(item);
            e.Handled = true;
        }
    }

    private void ApplySuggestion(SkillCompletionItem item)
    {
        _textBox.Text = SkillCompletionQuery.CreateInvocation(item.Name);
        _textBox.CaretIndex = _textBox.Text.Length;
        Close();
        _textBox.Focus();
    }
}
