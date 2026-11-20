using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public partial class StudyEvidenceView : UserControl
{
    private sealed record DeviceOption(Device Device, string Caption) { public override string ToString() => Caption; }
    private sealed record ReportOption(StudyReportGroup Report, string Caption) { public override string ToString() => Caption; }
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private ConsoleClient? _client;
    private StudyEvidenceScope? _scope;
    private CancellationTokenSource? _deviceRead, _reportRead;
    private StudyEvidenceReceipt? _receipt;
    private DeviceOption[] _devices = [];
    private string? _cursor;

    public StudyEvidenceView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => RenderReceipt();
        DetachedFromVisualTree += (_, _) => _clock.Stop();
        AttachedToVisualTree += (_, _) => { if (_receipt is not null) RenderReceipt(); };
    }

    public void SetScope(ConsoleClient? client, string? schoolId, string? termId, Classroom? classroom)
    {
        Cancel(ref _deviceRead);
        Cancel(ref _reportRead);
        _clock.Stop();
        _receipt = null;
        _scope = null;
        _client = client;
        DevicePicker.ItemsSource = null;
        ReceiptTime.IsVisible = false;
        ReadReceiptButton.IsEnabled = false;
        ClearReports();
        _devices = [];
        IsVisible = client is not null && schoolId is not null && termId is not null && classroom is not null;
        if (client is null || schoolId is null || termId is null || classroom is null) return;
        _scope = new(schoolId, termId, classroom.Id);
        var devices = classroom.Screens.SelectMany(s => s.Devices.Select(d => new DeviceOption(d, $"{s.Name} · {d.Name}"))).ToArray();
        var duplicateNames = devices.GroupBy(item => item.Caption).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        _devices = devices.Select(item => duplicateNames.Contains(item.Caption)
            ? item with { Caption = $"{item.Caption}（{item.Device.Id}）" } : item).ToArray();
        DevicePicker.IsEnabled = _devices.Length > 0;
        DevicePicker.ItemsSource = _devices;
        ReceiptStatus.Text = _devices.Length == 0 ? "本班暂无已接入的大屏。" : "请选择设备查看回执。";
        if (_devices.Length > 0) DevicePicker.SelectedIndex = 0;
        ReportsStatus.Text = "可查询最近 30 天已上传的自习报告。";
        ReadReportsButton.IsEnabled = true;
    }

    private static void Cancel(ref CancellationTokenSource? active)
    {
        var previous = active;
        active = null;
        previous?.Cancel();
    }

    private async void DeviceChanged(object? sender, SelectionChangedEventArgs e) => await ReadReceipt();
    private async void RefreshReceipt(object? sender, RoutedEventArgs e) => await ReadReceipt();
    private async Task ReadReceipt()
    {
        Cancel(ref _deviceRead);
        _clock.Stop();
        _receipt = null;
        ReceiptTime.IsVisible = false;
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
        _cursor = null;
        ReportPicker.ItemsSource = null;
        ReportPicker.IsVisible = false;
        EarlierReports.IsVisible = false;
        FragmentList.Children.Clear();
    }

    private async void RefreshReports(object? sender, RoutedEventArgs e) => await ReadReports(null);
    private async void ReadEarlier(object? sender, RoutedEventArgs e) { if (_cursor is { } cursor) await ReadReports(cursor); }

    private async Task ReadReports(string? cursor)
    {
        if (_scope is not { } scope || _client is not { } client) return;
        Cancel(ref _reportRead);
        ClearReports();
        using var read = _reportRead = new();
        ReportsStatus.Text = "正在查询本班报告…";
        ReadReportsButton.IsEnabled = false;
        try
        {
            var page = await client.ReadStudyReportsAsync(scope, cursor, read.Token);
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
        if (ReportPicker.SelectedItem is not ReportOption selected || _scope is not { } scope || _client is not { } client) return;
        Cancel(ref _reportRead);
        using var read = _reportRead = new();
        ReadReportsButton.IsEnabled = true;
        FragmentList.Children.Clear();
        ReportsStatus.Text = "正在读取本场报告…";
        try
        {
            var detail = await client.ReadStudyReportAsync(scope, selected.Report, read.Token);
            if (_reportRead != read) return;
            ReportsStatus.Text = $"学校已收到 {detail.FragmentCount} 个片段，按本次自习的时间顺序列出。";
            foreach (var part in detail.Fragments!) FragmentList.Children.Add(Fragment(part));
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            if (_reportRead == read && !SessionEnded(ex)) ReportsStatus.Text = ErrorText(ex);
        }
        finally { if (_reportRead == read) _reportRead = null; }
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
