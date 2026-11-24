using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermDiagnosticsView : UserControl
{
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly ManagedAcademicTerm _term = null!;
    private bool _closed, _navigating;
    internal Func<SchoolOverview, SchoolOverviewTask, Task<bool>>? OpenTask { get; set; }
    internal Action<ConsoleApiException>? ScopeUnavailable { get; init; }
    internal Action? CloseRequested { get; set; }
    internal Action? CloseLegacyDialog { get; set; }

    public TermDiagnosticsView() => InitializeComponent();

    internal TermDiagnosticsView(ConsoleClient client, School school, ManagedAcademicTerm term) : this()
    {
        _client = client;
        _school = school;
        _term = term;
        DiagnosticsHeading.Text = $"{school.Name} · {term.Name}（{term.StatusText}）";
        DiagnosticsActiveTerm.Text = "正在读取全校启用学期。";
        DiagnosticsOverview.SessionExpired += error => ScopeUnavailable?.Invoke(error);
        DiagnosticsOverview.AuthorizationLost += (error, _, _) => ScopeUnavailable?.Invoke(error);
        DiagnosticsOverview.SnapshotChanged += (snapshot, notice) => RenderScope(snapshot, school, term, notice);
        DiagnosticsOverview.TaskRequested += async (snapshot, task) =>
        {
            if (_closed || _navigating || !DiagnosticsOverview.Shows(snapshot) || OpenTask is not { } open) return;
            _navigating = true;
            try
            {
                if (await open(snapshot, task))
                {
                    if (!_closed) await DiagnosticsOverview.RefreshAsync(restoreTask: true);
                }
                else if (!_closed) DiagnosticsOverview.ShowNavigationMessage("当前版本没有能精确定位此事项的办理入口。");
            }
            catch (ConsoleApiException error)
            {
                if (!_closed) DiagnosticsOverview.ShowNavigationMessage(error.Message);
            }
            finally { _navigating = false; }
        };
    }

    internal void Start() => DiagnosticsOverview.SetScope(_client, _school, _term.Id);

    internal void End()
    {
        _closed = true;
        CloseLegacyDialog?.Invoke();
        DiagnosticsOverview.SetScope(null, null);
    }

    private void RenderScope(SchoolOverview? overview, School school, ManagedAcademicTerm term, string notice)
    {
        DiagnosticsScreens.Children.Clear();
        DiagnosticsObservations.IsVisible = overview is not null;
        if (overview?.Term is not { } selected)
        {
            DiagnosticsHeading.Text = $"{school.Name} · {term.Name}";
            DiagnosticsActiveTerm.Text = notice.StartsWith("正在", StringComparison.Ordinal) ?
                "正在核对学期状态及全校启用学期。" : "全校启用学期待核对。";
            return;
        }
        DiagnosticsHeading.Text = $"{school.Name} · {selected.Name}（{selected.StatusLabel}）";
        DiagnosticsActiveTerm.Text = $"全校启用学期：{overview.ActiveTerm?.Name ?? "暂无"}";
        DiagnosticsScreens.Children.Add(Row(selected.Status == "ACTIVE" ?
            "以下为服务器读取时实际绑定到本学期的屏，观测时间来自对应设备。" :
            "以下只列当前仍绑定到本学期班级的屏。网页时间是最近记录；无法据此还原历史值守或原生运行。"));
        foreach (var screen in overview.Screens)
        {
            var times = (screen.Browser.LastReportedAt, screen.Native.LastReportedAt);
            DiagnosticsScreens.Children.Add(Row($"{screen.ClassName} · {screen.ScreenName}（{screen.ScreenId}）" +
                $"　网页：{Time(times.Item1)}　原生：{Time(times.Item2)}"));
        }
        if (overview.Screens.Count == 0) DiagnosticsScreens.Children.Add(Row("本次读取未找到仍绑定到所选学期班级的启用大屏。"));
    }

    private static TextBlock Row(string text) => new() {Text = text, TextWrapping = TextWrapping.Wrap};
    private static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无可核对时间";
    private void ReturnClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();
}
