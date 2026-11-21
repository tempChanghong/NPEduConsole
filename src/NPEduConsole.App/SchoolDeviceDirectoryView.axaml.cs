using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolDeviceDirectoryView : UserControl
{
    private SchoolOverview? _snapshot;
    private IReadOnlyList<OverviewScreenItem> _rows = [];
    private string _status = "";
    private (string ScreenId, Vector Offset)? _returnPosition;

    public event Action? RefreshRequested;
    public event Action<SchoolOverview, OverviewScreenItem>? ScreenRequested;

    public SchoolDeviceDirectoryView() => InitializeComponent();

    public void ResetSearch()
    {
        _returnPosition = null;
        DeviceSearch.Clear();
    }

    public void SetSnapshot(SchoolOverview? snapshot, string status)
    {
        _snapshot = snapshot;
        _status = status;
        _rows = snapshot?.Screens.Select(SchoolOverviewView.ScreenRow).ToArray() ?? [];
        var ambiguous = _rows.GroupBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ambiguous.Count > 0)
            _rows = _rows.Select(row => ambiguous.Contains(row.Title)
                ? row with { Title = $"{row.Title}（绑定 {row.ScreenId}）" } : row).ToArray();
        Render();
    }

    public void ShowNavigationMessage(string message) => DeviceDirectoryFeedback.Text = message;

    public void RestorePosition(string screenId)
    {
        var snapshot = _snapshot;
        var previous = _returnPosition;
        _returnPosition = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible || !ReferenceEquals(_snapshot, snapshot)) return;
            var scroll = DeviceDirectoryRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null && previous?.ScreenId == screenId) scroll.Offset = previous.Value.Offset;
            var row = DeviceDirectoryRows.Items.OfType<OverviewScreenItem>().FirstOrDefault(item => item.ScreenId == screenId);
            if (row is null) { DeviceSearch.Focus(); return; }
            DeviceDirectoryRows.SelectedItem = row;
            if (DeviceDirectoryRows.ContainerFromItem(row) is null) DeviceDirectoryRows.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsEffectivelyVisible || !ReferenceEquals(_snapshot, snapshot)) return;
                var action = DeviceDirectoryRows.ContainerFromItem(row)?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                if (action is not null) action.Focus();
                else DeviceSearch.Focus();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void SearchChanged(object? sender, TextChangedEventArgs args) => Render();

    private void Render()
    {
        string query = DeviceSearch.Text?.Trim() ?? "";
        var rows = query.Length == 0 ? _rows : _rows.Where(row =>
            row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        DeviceDirectoryRows.ItemsSource = rows;
        DeviceTable.IsVisible = rows.Count > 0;
        DeviceDirectoryFeedback.Text = _snapshot is null ? _status
            : _snapshot.Term is null ? "暂无启用学期。"
            : _rows.Count == 0 ? "当前学期暂无已登记大屏。"
            : rows.Count == 0 ? "没有符合查询条件的大屏。"
            : $"{rows.Count} 台大屏 · {_status}";
    }

    private void RefreshClicked(object? sender, RoutedEventArgs args) => RefreshRequested?.Invoke();

    private void OpenClicked(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is { } snapshot && sender is Button { Tag: OverviewScreenItem row })
        {
            var scroll = DeviceDirectoryRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _returnPosition = (row.ScreenId, scroll?.Offset ?? default);
            ScreenRequested?.Invoke(snapshot, row);
        }
    }
}
