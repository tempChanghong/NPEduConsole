using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record DeviceRunReportRow(DeviceRunReport Report)
{
    public string Title => $"报告开始 {Report.StartedAt.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm:ss} · {Report.SourceLabel}";
    public string Subtitle => $"{Report.OutcomeLabel} · 有效采样 {Report.Summary.SampledSeconds:0.#} 秒" +
        (Report.Timing?.ClockDiscontinuity == true ? " · 设备时钟跳变" : "");
}

public sealed partial class DeviceRunReportsWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly DeviceFieldScope _scope = null!;
    private readonly string _school = "", _classroom = "", _screen = "", _device = "";
    private readonly DeviceFieldResult? _fromReceipt;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DeviceRunReport[] _reports = [];
    private StudyEvidenceReceipt? _live;
    private Window? _child;
    private StudyRecordsPage? _recordsPage;
    private CancellationTokenSource? _recordsLookup;
    private bool _closed, _busy, _loss, _compact, _showDetail, _needsReceiptFocus;
    private string? _selected;
    private long _accessCheckedAt = Stopwatch.GetTimestamp();

    public DeviceRunReportsWindow() => InitializeComponent();

    internal DeviceRunReportsWindow(ConsoleClient client, DeviceFieldScope scope, string school, string classroom, string screen,
        string device, DeviceFieldResult? fromReceipt = null, bool fromField = false) : this()
    {
        _client = client;
        _scope = scope;
        (_school, _classroom, _screen, _device) = (school, classroom, screen, device);
        _fromReceipt = fromReceipt?.Target.Scope == scope ? fromReceipt : null;
        _needsReceiptFocus = _fromReceipt?.SampleSessionId is not null;
        RunReportTarget.Text = $"{school} · {classroom} · {screen}";
        RunReportDevice.Text = $"原生设备 {device} · {_scope.DeviceId}";
        RunReportField.IsVisible = !fromField;
        RunReportRows.SelectionChanged += (_, _) => SelectRow();
        RunReportFilter.SelectionChanged += (_, _) => RenderRows();
        RunReportRows.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && RunReportRows.SelectedItem is DeviceRunReportRow) { ShowDetail(); e.Handled = true; }
        };
        _timer.Tick += async (_, _) =>
        {
            RenderLive();
            if (!_busy && !_loss && Stopwatch.GetElapsedTime(_accessCheckedAt) >= TimeSpan.FromSeconds(20)) await CheckAccessAsync();
        };
        Opened += async (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); Arrange(); await ReadAsync(); if (!_closed) _timer.Start(); };
        SizeChanged += (_, _) => Arrange();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _child?.Close();
            CloseRecordsPage();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        RenderLive();
        SelectRow();
    }

    internal bool Revoked { get; private set; }
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    private StudyEvidenceScope StudyScope => new(_scope.SchoolId, _scope.TermId, _scope.ClassId);

    private async void Refresh(object? sender, RoutedEventArgs args) => await ReadAsync();

    private async Task ReadAsync()
    {
        if (_busy || _closed || _loss || _recordsPage is not null) return;
        _recordsLookup?.Cancel();
        _busy = true;
        _accessCheckedAt = Stopwatch.GetTimestamp();
        RunReportRefresh.IsEnabled = RunReportField.IsEnabled = RunReportRecords.IsEnabled = false;
        RunReportFeedback.Text = "正在读取原设备的结束报告…";
        _live = null;
        RenderLive();
        try
        {
            string? issue = null;
            DeviceRunReports? list = null;
            try { list = await _client.ReadDeviceRunReportsAsync(_scope, _lifetime.Token); }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { issue = error.Message; }
            if (_closed || _loss) return;
            string? capture = _fromReceipt?.SampleSessionId?.ToString("D");
            _reports = list?.Items ?? [];
            if (capture is not null && !_reports.Any(row => row.SessionId == capture))
            {
                try
                {
                    var historic = await _client.FindDeviceRunClassReportAsync(_scope, capture, _lifetime.Token);
                    if (_closed || _loss) return;
                    if (historic is not null) _reports = [historic.Report, .. _reports];
                }
                catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
                { issue = Join(issue, "原班场次读取失败，请刷新核对。" + error.Message); }
            }

            if (list is not null)
            {
                try
                {
                    var live = await _client.ReadStudyReceiptAsync(StudyScope, _scope.DeviceId, _lifetime.Token);
                    if (!_closed && !_loss) _live = live;
                }
                catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { }
            }
            if (_closed || _loss) return;
            bool exact = capture is not null && _reports.Any(row => row.SessionId == capture);
            if (_needsReceiptFocus && exact)
            {
                _selected = capture;
                _showDetail = true;
                _needsReceiptFocus = false;
            }
            string status = list is null ? "设备报告读取失败。" : _reports.Length == 0 ? "暂无已上传的结束报告。" : $"已读取 {_reports.Length} 份结束报告。";
            if (list?.ScheduleReadFailed == true) status += " 旧排程会话读取失败，缺少来源的报告继续显示待核对。";
            if (_fromReceipt is not null) status += exact ? " 已定位此次处置对应的结束报告。" : capture is null
                ? " 本次处置回执没有可核对的采样会话，未自动选取报告。" : " 本次恢复尚未读到同一会话的结束报告。";
            RunReportFeedback.Text = Join(status, issue);
            RenderRows();
            RenderLive();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) { Handle(error); }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                RunReportRefresh.IsEnabled = RunReportField.IsEnabled = !_loss;
                SelectRow();
            }
        }
    }

    private void RenderRows()
    {
        string? filter = RunReportFilter.SelectedIndex switch { 1 => "Scheduled", 2 => "Manual", _ => null };
        var rows = _reports.Where(row => RunReportFilter.SelectedIndex == 0 || row.SessionKind == filter)
            .Select(row => new DeviceRunReportRow(row)).ToArray();
        string? keep = _selected;
        RunReportRows.ItemsSource = rows;
        RunReportRows.SelectedItem = rows.FirstOrDefault(row => row.Report.SessionId == keep) ??
            (!_compact && _fromReceipt is null ? rows.FirstOrDefault() : null);
        if (RunReportRows.SelectedItem is null) _showDetail = false;
        SelectRow();
        Arrange();
    }

    private void SelectRow()
    {
        var report = (RunReportRows.SelectedItem as DeviceRunReportRow)?.Report;
        if (_selected != report?.SessionId) _recordsLookup?.Cancel();
        _selected = report?.SessionId;
        RunReportDetail.SetReport(report);
        RunReportDetailStatus.IsVisible = report is not null;
        RunReportDetailStatus.Text = report?.Ownership is not null ? "按入库时的班级归属显示 · 报告为已结束会话" : "设备历史 · 无可核对的班级归属";
        if (report is not null && _fromReceipt is not null)
            RunReportDetailStatus.Text += _fromReceipt.SampleSessionId?.ToString("D") == report.SessionId
                ? " · 本次恢复对应报告" : " · 当前选中另一采样会话";
        RunReportRecords.IsVisible = report?.Ownership == StudyScope && (!_compact || _showDetail);
        RunReportRecords.IsEnabled = !_busy && !_loss && _recordsLookup is null;
        RunReportOpen.IsEnabled = report is not null && !_loss;
    }

    private void RenderLive() => RunReportLive.Text = _live is { } live
        ? "设备当下执行 · " + live.Describe().Replace("\n", " · ", StringComparison.Ordinal)
        : "设备当下执行 · 待核对";

    private void OpenReport(object? sender, RoutedEventArgs args) => ShowDetail();
    private void ShowDetail()
    {
        if (RunReportRows.SelectedItem is not DeviceRunReportRow) return;
        _showDetail = true;
        Arrange();
        RunReportBack.Focus();
    }

    private void GoBack(object? sender, RoutedEventArgs args)
    {
        _recordsLookup?.Cancel();
        if (!_compact || !_showDetail) { Close(); return; }
        _showDetail = false;
        Arrange();
        RunReportRows.Focus();
        if (RunReportRows.SelectedItem is { } selected)
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && !_showDetail && RunReportRows.SelectedItem == selected)
                    RunReportRows.ContainerFromItem(selected)?.Focus();
            }, DispatcherPriority.Loaded);
    }

    private async void OpenField(object? sender, RoutedEventArgs args)
    {
        if (_closed || _loss || _busy || _recordsPage is not null || _child is not null) return;
        _recordsLookup?.Cancel();
        var window = new DeviceFieldActionWindow(_client, _scope, _school, _classroom, _screen, _device, fromReports: true)
        { SessionExpired = Handle };
        _child = window;
        try { await window.ShowDialog(this); }
        finally { if (_child == window) _child = null; }
        if (_closed) return;
        Revoked |= window.Revoked;
        await ReadAsync();
        if (!_closed) RunReportField.Focus();
    }

    private async void OpenRecords(object? sender, RoutedEventArgs args)
    {
        if (_closed || _loss || _busy || _child is not null || _recordsPage is not null || _recordsLookup is not null ||
            RunReportRows.SelectedItem is not DeviceRunReportRow row || row.Report.Ownership != StudyScope) return;
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _recordsLookup = lookup;
        RunReportRecords.Content = "正在核对本场记录…";
        RunReportRecords.IsEnabled = false;
        try
        {
            var result = await _client.FindDeviceRunClassReportAsync(_scope, row.Report.SessionId, lookup.Token);
            if (lookup.IsCancellationRequested || _selected != row.Report.SessionId) return;
            if (result is null) { RunReportFeedback.Text = "已无法查到原班的对应场次，可能超出报告保留期。"; return; }
            var page = new StudyRecordsPage(_client, StudyScope, _school, _classroom, null, result.Group,
                new Dictionary<string, string> { [_scope.DeviceId] = _screen }, _scope, returnLabel: "返回运行报告");
            _recordsLookup = null;
            _recordsPage = page;
            _showDetail = true;
            page.ReturnRequested += ReturnFromRecords;
            page.AccessLost += Handle;
            ReportWorkspace.IsVisible = false;
            ReportRecordsHost.Content = page;
            ReportRecordsHost.IsVisible = true;
            page.FindControl<Button>("RecordsDone")?.Focus();
            await page.StartAsync();
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (!lookup.IsCancellationRequested) Handle(error);
        }
        finally
        {
            if (ReferenceEquals(_recordsLookup, lookup)) _recordsLookup = null;
            if (!_closed && _recordsLookup is null)
            {
                RunReportRecords.Content = "本场照片与退出记录";
                RunReportRecords.IsEnabled = !_busy && !_loss;
            }
        }
    }

    private void CloseRecordsPage()
    {
        if (_recordsPage is not { } page) return;
        _recordsPage = null;
        page.ReturnRequested -= ReturnFromRecords;
        page.AccessLost -= Handle;
        page.End();
        ReportRecordsHost.Content = null;
        ReportRecordsHost.IsVisible = false;
        ReportWorkspace.IsVisible = true;
    }

    private async void ReturnFromRecords()
    {
        CloseRecordsPage();
        if (_closed || _loss) return;
        RunReportRecords.Content = "本场照片与退出记录";
        RunReportRecords.IsEnabled = true;
        RunReportRecords.Focus();
        await CheckAccessAsync();
    }

    private async Task CheckAccessAsync()
    {
        if (_closed || _loss) return;
        _accessCheckedAt = Stopwatch.GetTimestamp();
        try { await _client.VerifyDeviceDiagnosticsAccessAsync(_scope.SchoolId, _lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (error.StatusCode is 401 or 403) { Handle(error); }
        catch (ConsoleApiException) { }
    }

    private void Handle(ConsoleApiException error)
    {
        if (_closed) return;
        RunReportFeedback.Text = error.Message;
        if (error.StatusCode is not (401 or 403)) return;
        if (_loss) return;
        _loss = true;
        _recordsLookup?.Cancel();
        _recordsPage?.RevokeAccess();
        CloseRecordsPage();
        _child?.Close();
        _reports = [];
        _live = null;
        _showDetail = false;
        RunReportTarget.Text = "当前学校的报告访问已失效";
        RunReportDevice.Text = "";
        ReportLiveToggle.IsChecked = false;
        ReportLiveToggle.IsEnabled = false;
        RunReportRefresh.IsEnabled = RunReportField.IsEnabled = RunReportFilter.IsEnabled = false;
        RenderRows();
        RenderLive();
        SessionExpired?.Invoke(error);
    }

    private void Arrange()
    {
        _compact = ClientSize.Width is > 0 and < 920;
        RunReportColumns.ColumnDefinitions[1].Width = _compact ? new(0) : new(1.6, GridUnitType.Star);
        RunReportColumns.ColumnSpacing = _compact ? 0 : 10;
        Grid.SetColumn(RunReportDetailPane, _compact ? 0 : 1);
        RunReportDetailPane.IsVisible = !_compact || _showDetail;
        RunReportListPane.IsVisible = !_compact || !_showDetail;
        RunReportOpen.IsVisible = _compact;
        RunReportRecords.IsVisible = (!_compact || _showDetail) && RunReportDetail.Report?.Ownership == StudyScope;
        RunReportBack.Content = _compact && _showDetail ? "返回报告列表" : "返回";
    }

    private static string Join(string? primary, string? extra) => string.Join(" ", new[] { primary, extra }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
