using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private (string ScreenId, Vector Offset, bool Duty)? _returnPosition;
    private (string School, string? Term)? _filterScope;

    public event Action? RefreshRequested;
    public event Action<SchoolOverview, OverviewScreenItem>? ScreenRequested;
    public event Action<string?, string?, string?, Control?>? DutyRequested;
    public event Action<string, Control>? DiagnosticsRequested;
    public event Action<Control>? ConnectionsRequested;
    public event Action<Control>? ActionPolicyRequested;

    private void OpenActionPolicyClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Control anchor) ActionPolicyRequested?.Invoke(anchor);
    }

    private void OpenConnectionsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Control anchor) ConnectionsRequested?.Invoke(anchor);
    }

    public SchoolDeviceDirectoryView()
    {
        InitializeComponent();
        SetChoices(NativeFilter, "AWAITING_PAIRING", "AWAITING_REPORT", "REPAIR_REQUIRED", "REPORTED", "UNKNOWN", "NOT_APPLICABLE");
        SetChoices(BrowserFilter, "AWAITING_LOGIN", "AWAITING_HEARTBEAT", "ONLINE", "DEGRADED", "OFFLINE", "UNKNOWN", "NOT_APPLICABLE");
        DeviceSearch.TextChanged += (_, _) => Render();
        NativeFilter.SelectionChanged += (_, _) => Render();
        BrowserFilter.SelectionChanged += (_, _) => Render();
        AddHandler(KeyDownEvent, DirectoryKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 890;
            Grid.SetRow(DeviceDirectoryActions, compact ? 1 : 0);
            Grid.SetColumn(DeviceDirectoryActions, compact ? 0 : 1);
            Grid.SetColumnSpan(DeviceDirectoryActions, compact ? 2 : 1);
        };
    }

    private static void SetChoices(ComboBox filter, params string[] states)
    {
        filter.Items.Add(new ComboBoxItem { Content = "全部状态" });
        foreach (var state in states)
            filter.Items.Add(new ComboBoxItem { Content = DeviceEvidence.LabelFor(state), Tag = state });
        filter.SelectedIndex = 0;
    }

    public void ResetSearch()
    {
        _returnPosition = null;
        _filterScope = null;
        ClearSelection();
    }

    private void ClearSelection()
    {
        DeviceSearch.Clear();
        NativeFilter.SelectedIndex = BrowserFilter.SelectedIndex = 0;
    }

    public void SetSnapshot(SchoolOverview? snapshot, string status)
    {
        _snapshot = snapshot;
        _status = status;
        _rows = [];
        if (snapshot is not null)
        {
            var scope = (snapshot.School.Id, snapshot.Term?.Id);
            if (_filterScope is { } previous && previous != scope) ResetSearch();
            _filterScope = scope;
            static string Title(SchoolOverviewScreen s) => $"{s.ClassName} · {s.ScreenName}";
            var ambiguous = snapshot.Screens.GroupBy(Title, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _rows = snapshot.Screens.Select(s => new OverviewScreenItem(s, ambiguous.Contains(Title(s)))).ToArray();
        }
        Render();
    }

    public void ShowNavigationMessage(string message) => DeviceDirectoryFeedback.Text = message;

    public OverviewScreenItem? FindEvidence(string? schoolId, string? termId, string classId, string screenId) =>
        _snapshot is { Term: { } term } && _snapshot.School.Id == schoolId && term.Id == termId
            ? _rows.FirstOrDefault(row => row.ClassId == classId && row.ScreenId == screenId) : null;

    public string EvidenceStatus(bool found) => found || _snapshot is null
        ? _status : "本次核对未找到这台大屏的接入记录。";

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
                bool duty = previous?.Duty == true;
                var action = DeviceDirectoryRows.ContainerFromItem(row)?.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.Classes.Contains("screen-duty-entry") == duty);
                if (action is not null) action.Focus();
                else DeviceSearch.Focus();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void ClearClicked(object? sender, RoutedEventArgs args)
    {
        ClearSelection();
        DeviceSearch.Focus();
    }

    private void DirectoryKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.F || args.KeyModifiers != KeyModifiers.Control) return;
        DeviceSearch.Focus();
        DeviceSearch.SelectAll();
        args.Handled = true;
    }

    private void Render()
    {
        var words = (DeviceSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? native = (NativeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        string? browser = (BrowserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        bool filtered = words.Length != 0 || native is not null || browser is not null;
        var rows = !filtered ? _rows : _rows.Where(row =>
            (native is null || row.Native.State == native) && (browser is null || row.Browser.State == browser) &&
            words.All(word => row.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                row.ScreenId.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        DeviceDirectoryRows.ItemsSource = rows;
        DeviceTable.IsVisible = rows.Count > 0;
        ClearFilters.IsEnabled = filtered;
        DeviceDirectoryFeedback.Text = _snapshot is null ? _status
            : _snapshot.Term is null ? "暂无启用学期。"
            : _rows.Count == 0 ? "当前学期暂无已登记大屏。"
            : rows.Count == 0 ? $"共 {_rows.Count} 台大屏，当前筛选无结果。"
            : filtered ? $"找到 {rows.Count} 台，共 {_rows.Count} 台 · {_status}" : $"共 {rows.Count} 台大屏 · {_status}";
    }

    private void RefreshClicked(object? sender, RoutedEventArgs args) => RefreshRequested?.Invoke();

    private void OpenDiagnosticsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Control anchor) DiagnosticsRequested?.Invoke(DeviceSearch.Text ?? "", anchor);
    }

    private void OpenDutyDirectoryClicked(object? sender, RoutedEventArgs args) => DutyRequested?.Invoke(null, null, null, sender as Control);

    private void OpenDutyClicked(object? sender, RoutedEventArgs args)
    {
        if (_snapshot?.Term is { } term && sender is Button { DataContext: OverviewScreenItem row } button)
        {
            var scroll = DeviceDirectoryRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _returnPosition = (row.ScreenId, scroll?.Offset ?? default, true);
            DutyRequested?.Invoke(row.ScreenId, row.ClassId, term.Id, button);
        }
    }

    private void OpenClicked(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is { } snapshot && sender is Button { Tag: OverviewScreenItem row })
        {
            var scroll = DeviceDirectoryRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            _returnPosition = (row.ScreenId, scroll?.Offset ?? default, false);
            ScreenRequested?.Invoke(snapshot, row);
        }
    }
}
