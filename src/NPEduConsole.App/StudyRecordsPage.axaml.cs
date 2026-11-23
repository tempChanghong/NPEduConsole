using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StudyRecordsPage : UserControl
{
    private readonly ConsoleClient _client = null!;
    private readonly StudyEvidenceScope _scope = null!;
    private readonly DateOnly? _day;
    private readonly StudyReportGroup? _report;
    private readonly DeviceFieldScope? _reportDevice;
    private readonly string _classScope = "";
    private readonly IReadOnlyDictionary<string, string> _devices = new Dictionary<string, string>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(StudyRecordRow Row, DateTimeOffset Timestamp)> _rows = [];
    private CancellationTokenSource? _request;
    private string? _photoCursor, _exitCursor;
    private Bitmap? _bitmap;
    private string? _loadedPhoto;
    private StudyRecordRow? _removing;
    private bool _started, _closed, _denied, _showPreviewStatus;

    public StudyRecordsPage() => InitializeComponent();

    internal StudyRecordsPage(ConsoleClient client, StudyEvidenceScope scope, string school, string classroom,
        DateOnly? day, StudyReportGroup? report, IReadOnlyDictionary<string, string>? devices = null,
        DeviceFieldScope? reportDevice = null, string returnLabel = "关闭") : this()
    {
        _client = client;
        _scope = scope;
        _day = day;
        _report = report;
        _reportDevice = reportDevice;
        if (devices is not null) _devices = devices;
        _classScope = $"{school} · {classroom}";
        RecordsScope.Text = _classScope + " · " + (report is not null
            ? $"场次学校时间 {report.Window.Label} · {DeviceName(report.DeviceId)}"
            : day is { } date ? $"学校日期 {date:yyyy/M/d} · 全班各屏" : "全班各屏");
        RecordsAllDays.IsVisible = day is not null && report is null;
        RecordsDone.Content = returnLabel;
        Records.MoreRequested += () => _ = LoadAsync(more: true);
        Records.PreviewClosed += ClearPreview;
        Records.PreviewRequested += row => _ = PreviewAsync(row);
        Records.CompactPreviewChanged += UpdateChrome;
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
        UpdateChrome();
    }

    internal event Action? ReturnRequested;
    internal event Action<ConsoleApiException>? AccessLost;

    internal Task StartAsync()
    {
        if (_started || _closed) return Task.CompletedTask;
        _started = true;
        return LoadAsync();
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        ClearRecords();
        _lifetime.Dispose();
    }

    internal void RevokeAccess()
    {
        if (_closed || _denied) return;
        _denied = true;
        _request?.Cancel();
        ClearRecords();
        SetStatus("当前登录已失效或已无本校照片查看权限。请返回后重新核对账号和学校。");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        End();
        base.OnDetachedFromVisualTree(e);
    }

    private void CloseRecords(object? sender, RoutedEventArgs e) => ReturnRequested?.Invoke();

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _closed) return;
        e.Handled = true;
        if (_removing is not null) KeepPhoto();
        else if (Records.Current is not null) Records.ClosePreview();
        else ReturnRequested?.Invoke();
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await LoadAsync();
    private async void DateChanged(object? sender, RoutedEventArgs e)
    {
        if (!_started || _closed || _day is not { } date) return;
        RecordsScope.Text = _classScope + (RecordsAllDays.IsChecked == true ? " · 所有保留日期" : $" · 学校日期 {date:yyyy/M/d} · 全班各屏");
        await LoadAsync();
    }

    private async Task LoadAsync(bool more = false, string? message = null)
    {
        if (_closed || _denied) return;
        if (!more)
        {
            _request?.Cancel();
            ClearPreview();
            _rows.Clear();
            _photoCursor = _exitCursor = null;
            Records.SetRows([], false);
        }
        await RunAsync(more ? "正在读取更多记录…" : "正在读取服务器记录…", async token =>
        {
            if (_report is { } report)
            {
                var detail = _reportDevice is { } device && report.Fragments is [var fragment]
                    ? (await _client.FindDeviceRunClassReportAsync(device, fragment.SessionId, token))?.Group
                    : await _client.ReadStudyReportAsync(_scope, report, token);
                token.ThrowIfCancellationRequested();
                if (detail is null)
                {
                    SetStatus("已无法查到这次采样会话的在线记录。");
                    return;
                }
                if (detail.Photos is null || detail.Events is null)
                {
                    SetStatus("服务器没有返回本场记录明细，当前结果待核对。");
                    return;
                }
                foreach (var photo in detail.Photos.Where(row => row.State == "Stored")) AddPhoto(photo, detail.DeviceId);
                foreach (var exit in detail.Events) AddExit(exit, detail.DeviceId);
                ShowRows();
                SetStatus(detail.Truncated ? "本场记录已达到显示上限，仍可从班级入口查阅其他在线记录。" : Status(message),
                    showInPreview: detail.Truncated || message is not null);
                return;
            }

            DateOnly? date = RecordsAllDays.IsChecked == true ? null : _day;
            var photoPage = !more || _photoCursor is not null
                ? await _client.ReadStudyPhotosAsync(_scope, date, _photoCursor, token)
                : new StudyRecordPage<StudyPhotoRecord>([], null);
            var exitPage = !more || _exitCursor is not null
                ? await _client.ReadStudyExitsAsync(_scope, date, _exitCursor, token)
                : new StudyRecordPage<StudyExitRecord>([], null);
            token.ThrowIfCancellationRequested();
            _photoCursor = photoPage.Cursor;
            _exitCursor = exitPage.Cursor;
            foreach (var photo in photoPage.Items) AddPhoto(photo, photo.DeviceId);
            foreach (var exit in exitPage.Items) AddExit(exit, exit.DeviceId);
            ShowRows();
            SetStatus(Status(message), showInPreview: message is not null);
        });
    }

    private string Status(string? message) => message ?? (_rows.Count == 0 ? "此范围暂无可查看的在线记录。" : $"已读取 {_rows.Count} 条在线记录。");

    private void ShowRows() => Records.SetRows(_rows.OrderByDescending(row => row.Timestamp).ThenBy(row => row.Row.Key)
        .DistinctBy(row => row.Row.Key).Select(row => row.Row).ToArray(), _photoCursor is not null || _exitCursor is not null);

    private void AddPhoto(StudyPhotoRecord photo, string? device) => _rows.Add((new($"photo:{photo.EventId}",
        $"已存照片 · {Reason(photo.Reason)} · {DeviceName(device)}",
        $"拍摄 {SchoolTime(photo.CapturedAt)} · 到期 {SchoolTime(photo.ExpiresAt)}", photo.EventId), photo.CapturedAt));

    private void AddExit(StudyExitRecord exit, string? device) => _rows.Add((new($"exit:{exit.EventId}",
        $"{(exit.Reason == "ProcessRecovery" ? "异常恢复" : "退出验证记录")} · {Reason(exit.Reason)} · {DeviceName(device)}",
        $"服务器收到 {SchoolTime(exit.CreatedAt)}" +
        (exit.ExpiresAt is { } expires ? $" · 到期 {SchoolTime(expires)}" : "") +
        (exit.PhotoState == "Stored" && exit.PhotoEventId is not null ? " · 有关联画面" : " · 暂无可查看画面"),
        exit.PhotoState == "Stored" ? exit.PhotoEventId : null), exit.CreatedAt));

    private static string Reason(string reason) => reason switch
    {
        "Noise" => "连续超限", "ExitAttempt" or "ExitIntent" => "退出后台请求",
        "ProcessRecovery" => "进程恢复", "Timeout" => "管理验证超时", "InvalidSecret" => "管理口令未通过",
        _ => reason
    };

    private string DeviceName(string? id) => id is null ? "设备待核对" : _devices.GetValueOrDefault(id, $"设备 {id}");
    private static string SchoolTime(DateTimeOffset timestamp) => timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss");

    private async Task PreviewAsync(StudyRecordRow row)
    {
        if (_closed || _denied || _request is not null) return;
        ClearPreview();
        if (row.PhotoId is not { } photoId)
        {
            Records.SetPreview(row, null);
            return;
        }
        await RunAsync("正在读取这一张照片…", async token =>
        {
            var bytes = await _client.ReadStudyPhotoAsync(_scope, photoId, false, token);
            try
            {
                token.ThrowIfCancellationRequested();
                using var input = new MemoryStream(bytes);
                _bitmap = new Bitmap(input);
            }
            finally { Array.Clear(bytes); }
            _loadedPhoto = photoId;
            Records.SetPreview(row, _bitmap);
            SetStatus("照片已载入。", showInPreview: false);
        }, photoId);
    }

    private async void DownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (Records.Current?.PhotoId is not { } photoId || photoId != _loadedPhoto || _removing is not null ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        await RunAsync("请选择照片的保存位置。", async token =>
        {
            using var path = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存这一张自习照片", SuggestedFileName = $"study-{photoId}.jpg", DefaultExtension = "jpg",
                FileTypeChoices = [new("JPEG 图像") {Patterns = ["*.jpg", "*.jpeg"]}]
            });
            token.ThrowIfCancellationRequested();
            if (path is null) { SetStatus(Status(null), showInPreview: false); return; }
            var bytes = await _client.ReadStudyPhotoAsync(_scope, photoId, true, token);
            try
            {
                token.ThrowIfCancellationRequested();
                await using var output = await path.OpenWriteAsync();
                if (output.CanSeek) output.SetLength(0);
                await output.WriteAsync(bytes, token);
            }
            finally { Array.Clear(bytes); }
            SetStatus("已保存到所选位置。");
        }, photoId);
    }

    private void DeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_closed || _denied || _request is not null || Records.Current is not {PhotoId: { } id} row || id != _loadedPhoto) return;
        var photo = _rows.FirstOrDefault(entry => entry.Row.Key == $"photo:{id}").Row;
        SetRemoval(photo ?? row);
        RemoveRecordCancel.Focus();
    }

    private void CancelRemoval(object? sender, RoutedEventArgs e) => KeepPhoto();

    private void KeepPhoto()
    {
        SetRemoval(null);
        StudyRecordsDelete.Focus(NavigationMethod.Tab);
    }

    private void SetRemoval(StudyRecordRow? row)
    {
        _removing = row;
        RemoveRecordSummary.Text = row is null ? "" : $"删除服务器上的这张照片？\n{RecordsScope.Text}\n{row.Title} · {row.Details}";
        UpdateChrome();
    }

    private async void RemovePhoto(object? sender, RoutedEventArgs e)
    {
        if (_closed || _denied || _removing?.PhotoId is not { } id || id != _loadedPhoto ||
            Records.Current?.PhotoId != id || _request is not null) return;
        ClearPreview();
        string? outcome = null;
        await RunAsync("正在删除这一张照片…", async token =>
        {
            try
            {
                await _client.DeleteStudyPhotoAsync(_scope, id, token);
                outcome = "服务器已确认在线照片删除。";
            }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                outcome = "删除回执待核对。已重新读取服务器当前可查看的记录。";
            }
        });
        if (outcome is not null) await LoadAsync(message: outcome);
    }

    private void ClearPreview()
    {
        Records.SetPreview(null, null);
        _bitmap?.Dispose();
        _bitmap = null;
        _loadedPhoto = null;
        SetRemoval(null);
    }

    private void ClearRecords()
    {
        ClearPreview();
        _rows.Clear();
        Records.SetRows([], false);
        UpdateChrome();
    }

    private void SetStatus(string text, bool showInPreview = true)
    {
        RecordsStatus.Text = text;
        _showPreviewStatus = showInPreview;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var preview = Records.IsCompactPreview;
        RecordsFilters.IsVisible = !preview;
        RecordsStatus.IsVisible = !string.IsNullOrWhiteSpace(RecordsStatus.Text) && (!preview || _showPreviewStatus);
        RecordsBody.RowSpacing = preview && !RecordsStatus.IsVisible ? 0 : 7;
        RecordsTitle.FontSize = preview ? 20 : 23;

        var available = !_closed && !_denied;
        var hasPhoto = available && _loadedPhoto is { } id && Records.Current?.PhotoId == id;
        var confirming = hasPhoto && _removing is not null;
        StudyRecordsDownload.IsVisible = StudyRecordsDelete.IsVisible = hasPhoto && !confirming;
        RemoveRecordPanel.IsVisible = RemoveRecordCancel.IsVisible = RemoveRecordConfirm.IsVisible = confirming;
        StudyRecordsDownload.IsEnabled = StudyRecordsDelete.IsEnabled = RemoveRecordConfirm.IsEnabled = _request is null;
        RecordsRefresh.IsEnabled = RecordsAllDays.IsEnabled = available;
        Records.IsEnabled = available && _request is null;
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> action, string? photoId = null)
    {
        if (_closed || _denied) return;
        if (_request is {IsCancellationRequested: false}) return;
        using var request = _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetStatus(status);
        try { await action(request.Token); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error) when (error.StatusCode is 401 or 403)
        {
            if (_closed || _denied || _request != request) return;
            RevokeAccess();
            AccessLost?.Invoke(error);
        }
        catch (ConsoleApiException error)
        {
            if (_closed || _denied || _request != request) return;
            if (error.StatusCode == 404 && photoId is not null)
            {
                if (_loadedPhoto == photoId) ClearPreview();
                SetStatus("该照片已不可查看，可能已到期或删除。可以刷新记录。");
            }
            else SetStatus(error.StatusCode == 404 ? "记录已不可用。请刷新核对是否到期或已移除。" : error.Message);
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!_closed && !_denied && _request == request) SetStatus("无法读取图像或保存到所选位置，请重新核对。");
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                if (!_closed) UpdateChrome();
            }
        }
    }
}
