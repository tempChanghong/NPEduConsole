using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public partial class StudyEvidenceView : UserControl
{
    private sealed record DeviceOption(Device Device, string BindingId, string ScreenName, string Caption) { public override string ToString() => Caption; }
    private sealed record ReportOption(StudyReportGroup Report, string Caption) { public override string ToString() => Caption; }
    private sealed record ModeOption(StudyReportMode? Mode, string Caption) { public override string ToString() => Caption; }
    private static readonly ModeOption[] Modes =
    [
        new(null, "全部模式"),
        new(StudyReportMode.Daily, "日常模式 · ClassIsland 时间"),
        new(StudyReportMode.Exam, "考试模式 · Windows 时间"),
    ];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ConsoleClient? _client;
    private StudyEvidenceScope? _scope;
    private CancellationTokenSource? _deviceRead, _reportRead;
    private StudyEvidenceReceipt? _receipt;
    private StudyRecordsWindow? _records;
    private DeviceFieldActionWindow? _fieldAction;
    private StudyReportGroup? _loadedReport;
    private string? _schoolName, _className;
    private DeviceOption[] _devices = [];
    private string? _cursor;
    private DateOnly? _date;

    public StudyEvidenceView()
    {
        InitializeComponent();
        StudyReportModePicker.ItemsSource = Modes;
        StudyReportModePicker.SelectedIndex = 0;
        _clock.Tick += (_, _) => RenderReceipt();
        DetachedFromVisualTree += (_, _) =>
        {
            _clock.Stop();
            Cancel(ref _reportRead);
            ClearReports();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_receipt is not null) RenderReceipt();
            ResetReportsStatus();
        };
    }

    public void SetScope(ConsoleClient? client, string? schoolId, string? termId, Classroom? classroom, DateOnly? date = null, string? schoolName = null)
    {
        Cancel(ref _deviceRead);
        Cancel(ref _reportRead);
        _fieldAction?.Close();
        _clock.Stop();
        _receipt = null;
        _scope = null;
        StudyReportModePicker.SelectedIndex = 0;
        _client = client;
        _date = date;
        _schoolName = schoolName ?? schoolId;
        _className = classroom?.Name;
        DevicePicker.ItemsSource = null;
        ReceiptTime.IsVisible = false;
        ReceiptConfigVersion.IsVisible = false;
        ReadReceiptButton.IsEnabled = false;
        StudyEvidenceField.IsEnabled = false;
        ReadReportsButton.IsEnabled = false;
        ClearReports();
        _devices = [];
        IsVisible = client is not null && schoolId is not null && termId is not null && classroom is not null;
        if (client is null || schoolId is null || termId is null || classroom is null) return;
        _scope = new(schoolId, termId, classroom.Id);
        var devices = classroom.Screens.SelectMany(s => s.Devices.Select(d => new DeviceOption(d, s.Id, s.Name, $"{s.Name} · {d.Name}"))).ToArray();
        var duplicateNames = devices.GroupBy(item => item.Caption).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        _devices = devices.Select(item => duplicateNames.Contains(item.Caption)
            ? item with { Caption = $"{item.Caption}（{item.Device.Id}）" } : item).ToArray();
        DevicePicker.IsEnabled = _devices.Length > 0;
        DevicePicker.ItemsSource = _devices;
        ReceiptStatus.Text = _devices.Length == 0 ? "本班暂无已接入的大屏。" : "请选择设备查看回执。";
        if (_devices.Length > 0) DevicePicker.SelectedIndex = 0;
        ResetReportsStatus();
    }

    private void ResetReportsStatus()
    {
        if (_scope is null) return;
        ReportsStatus.Text = _date is { } selected ? $"查询 {selected:M 月 d 日} 开始且已上传的场次。" : "可查询最近 30 天已上传的自习报告。";
        ReadReportsButton.IsEnabled = true;
    }

    private static void Cancel(ref CancellationTokenSource? active)
    {
        var previous = active;
        active = null;
        previous?.Cancel();
    }

    private async void DeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        _fieldAction?.Close();
        StudyEvidenceField.IsEnabled = DevicePicker.SelectedItem is DeviceOption;
        await ReadReceipt();
    }

    private async void OpenFieldAction(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _scope is not { } scope || _fieldAction is not null ||
            DevicePicker.SelectedItem is not DeviceOption selected || TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = new DeviceFieldActionWindow(client, new(scope.SchoolId, scope.TermId, scope.ClassId, selected.BindingId, selected.Device.Id),
            _schoolName ?? scope.SchoolId, _className ?? scope.ClassId, selected.ScreenName, selected.Device.Name);
        _fieldAction = window;
        try { await window.ShowDialog(owner); }
        finally { if (_fieldAction == window) _fieldAction = null; }
        if (_scope != scope || _client != client || !ReferenceEquals(DevicePicker.SelectedItem, selected) || !IsEffectivelyVisible) return;
        if (!window.Revoked) { StudyEvidenceField.Focus(); return; }

        int position = DevicePicker.SelectedIndex;
        _devices = _devices.Where(option => option.Device.Id != selected.Device.Id || option.BindingId != selected.BindingId).ToArray();
        DevicePicker.ItemsSource = _devices;
        DevicePicker.IsEnabled = _devices.Length > 0;
        if (_devices.Length > 0)
        {
            DevicePicker.SelectedIndex = Math.Min(position, _devices.Length - 1);
            DevicePicker.Focus();
        }
        else
        {
            ReceiptStatus.Text = "原配对授权已撤销。本次读取的设备列表暂无其他已接入大屏。";
            ReadReportsButton.Focus();
        }
    }
    private async void RefreshReceipt(object? sender, RoutedEventArgs e) => await ReadReceipt();
    private async Task ReadReceipt()
    {
        Cancel(ref _deviceRead);
        _clock.Stop();
        _receipt = null;
        ReceiptTime.IsVisible = false;
        ReceiptConfigVersion.IsVisible = false;
        if (_scope is not { } scope || _client is not { } client || DevicePicker.SelectedItem is not DeviceOption target) return;
        using var read = _deviceRead = new();
        ReadReceiptButton.IsEnabled = false;
        ReceiptStatus.Text = "正在读取教室回执…";
        try
        {
            var snapshot = await client.ReadStudyReceiptAsync(scope, target.Device.Id, read.Token);
            if (_deviceRead != read) return;
            _receipt = snapshot;
            RenderReceipt();
            ReceiptConfigVersion.Text = $"学校当前有效配置 {snapshot.Current.Version[..12]}";
            ReceiptConfigVersion.IsVisible = true;
            if (snapshot.ReceivedAt is { } received)
            {
                ReceiptTime.Text = $"学校收到回执于 {SchoolTime(received)}";
                ReceiptTime.IsVisible = true;
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            if (_deviceRead == read && !SessionEnded(ex)) ReceiptStatus.Text = ErrorText(ex);
        }
        finally
        {
            if (_deviceRead == read) { _deviceRead = null; ReadReceiptButton.IsEnabled = true; }
        }
    }

    private void RenderReceipt()
    {
        if (_receipt is not { } receipt) return;
        ReceiptStatus.Text = receipt.Describe();
        if (receipt.ObservationFresh && receipt.Observation?.Version == receipt.Current.Version)
            _clock.Start();
        else _clock.Stop();
    }

    private void ClearReports()
    {
        ClearReportDetail();
        _cursor = null;
        ReportPicker.ItemsSource = null;
        ReportPicker.IsVisible = false;
        EarlierReports.IsVisible = false;
    }

    private void ClearReportDetail()
    {
        _records?.Close();
        _loadedReport = null;
        StudySessionRecords.IsVisible = false;
        FragmentList.Children.Clear();
    }

    private async void RefreshReports(object? sender, RoutedEventArgs e) => await ReadReports(null);
    private async void ReportModeChanged(object? sender, SelectionChangedEventArgs e) => await ReadReports(null);
    private async void ReadEarlier(object? sender, RoutedEventArgs e) { if (_cursor is { } cursor) await ReadReports(cursor); }

    private async Task ReadReports(string? cursor)
    {
        if (_scope is not { } scope || _client is not { } client || StudyReportModePicker.SelectedItem is not ModeOption option) return;
        Cancel(ref _reportRead);
        ClearReports();
        using var read = _reportRead = new();
        ReportsStatus.Text = "正在查询本班报告…";
        ReadReportsButton.IsEnabled = false;
        try
        {
            var page = await client.ReadStudyReportsAsync(scope, cursor, read.Token, _date, option.Mode);
            if (_reportRead != read) return;
            var choices = page.Items.Select(report =>
            {
                string deviceName = _devices.FirstOrDefault(x => x.Device.Id == report.DeviceId)?.Caption ??
                    $"历史设备 {report.DeviceId}";
                return new ReportOption(report, $"{report.Window.Label} · {(report.Mode == "Exam" ? "考试" : "日常")} · {deviceName}");
            }).ToArray();
            _cursor = page.Cursor;
            EarlierReports.IsVisible = _cursor is not null;
            ReportPicker.ItemsSource = choices;
            ReportPicker.IsVisible = choices.Length > 0;
            ReportsStatus.Text = choices.Length > 0 ? "选择场次查看各片段的实际采样结果。" : "当前没有符合条件的已上传报告。";
            if (choices.Length > 0) ReportPicker.SelectedIndex = 0;
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            if (_reportRead == read && !SessionEnded(ex)) ReportsStatus.Text = ErrorText(ex);
        }
        finally
        {
            if (_reportRead == read) { _reportRead = null; ReadReportsButton.IsEnabled = true; }
        }
    }

    private async void ReportChanged(object? sender, SelectionChangedEventArgs e)
    {
        ClearReportDetail();
        if (ReportPicker.SelectedItem is not ReportOption selected || _scope is not { } scope || _client is not { } client) return;
        Cancel(ref _reportRead);
        using var read = _reportRead = new();
        ReadReportsButton.IsEnabled = true;
        ReportsStatus.Text = "正在读取本场报告…";
        try
        {
            var detail = await client.ReadStudyReportAsync(scope, selected.Report, read.Token);
            if (_reportRead != read) return;
            var stored = detail.Photos?.Where(photo => photo.State == "Stored").ToArray();
            ReportsStatus.Text = $"学校已收到 {detail.FragmentCount} 个报告片段。" +
                (stored is null ? " 照片存储记录待核对。" : stored.Length == 0 ? " 本场暂无可查看的已存照片。"
                    : $" {detail.Truncated switch {true => "至少 ", false => ""}}{stored.Length} 张在线照片 · 最近拍摄 {SchoolTime(stored.Max(photo => photo.CapturedAt))}。");
            _loadedReport = detail;
            StudySessionRecords.IsVisible = true;
            foreach (var part in detail.Fragments!) FragmentList.Children.Add(Fragment(part));
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            if (_reportRead == read && !SessionEnded(ex)) ReportsStatus.Text = ErrorText(ex);
        }
        finally { if (_reportRead == read) _reportRead = null; }
    }

    private async void OpenSessionRecords(object? sender, RoutedEventArgs e)
    {
        if (_records is not null || _scope is not { } scope || _client is not { } client ||
            _loadedReport is not { } report || TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = _records = new StudyRecordsWindow(client, scope, _schoolName ?? scope.SchoolId,
            _className ?? scope.ClassId, null, report, _devices.GroupBy(row => row.Device.Id)
                .ToDictionary(group => group.Key, group => group.First().Caption));
        try { await window.ShowDialog(owner); }
        finally
        {
            if (_records == window) _records = null;
            if (StudySessionRecords.IsEffectivelyVisible) StudySessionRecords.Focus();
        }
    }

    private static string SchoolTime(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss");
    private static TextBlock Line(string text, string style) =>
        new() { Text = text, Classes = { style }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private static Border Fragment(StudyReportFragment part)
    {
        string outcome = part.Outcome switch { "Interrupted" => "中途中断", "Faulted" => "采集故障", "Stopped" => "已停止", _ => "已结束" };
        static string Number(double? value) => value?.ToString("0.0") ?? "无有效样本";
        var summary = part.Summary;
        var lines = new StackPanel { Spacing = 4 };
        lines.Children.Add(Line($"{SchoolTime(part.StartedAt)} 至 {SchoolTime(part.EndedAt)} · {outcome}", "label"));
        if (part.Timing is { ClockDiscontinuity: true } timing)
            lines.Children.Add(Line($"设备时钟发生偏差。上方结束时间按采集时长计算，设备记录的结束时间为 {SchoolTime(timing.ObservedEndedAt)}。", "muted"));
        lines.Children.Add(Line($"有效采样覆盖 {summary.Coverage:P1} · 学校收到于 {SchoolTime(part.ReceivedAt)}", "muted"));
        lines.Children.Add(Line($"信号电平（dBFS）  均值 {Number(summary.EnergyMeanDbfs)}  峰值 {Number(summary.PeakDbfs)}", "muted"));
        if (part.Estimate?.Summary is { } estimate)
            lines.Children.Add(Line($"估算声级（dB(A)）  均值 {Number(estimate.EnergyMeanDbA)}  最大 {Number(estimate.MaximumDbA)}  覆盖 {estimate.Coverage:P1}", "muted"));
        return new Border { Classes = { "card" }, Padding = new Avalonia.Thickness(12), Child = lines };
    }

    private static bool IsReadError(Exception ex) => ex is ConsoleApiException or HttpRequestException or OperationCanceledException;
    private bool SessionEnded(Exception ex) => TopLevel.GetTopLevel(this) is MainWindow main && main.HandleStudyEvidenceError(ex);
    private static string ErrorText(Exception ex) => ex is ConsoleApiException service ? service.Message : "读取失败，请刷新重试。";
}

public sealed partial class MainWindow
{
    internal bool HandleStudyEvidenceError(Exception error) => ReturnToLoginIfExpired(error);
}
