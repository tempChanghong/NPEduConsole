using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record DeviceDiagnosticDisplayRow(string Name, string Status);

public sealed partial class DeviceDiagnosticsWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<DeviceDiagnosticPage> _pages = [];
    private readonly DispatcherTimer _accessCheck = new() { Interval = TimeSpan.FromSeconds(20) };
    private DeviceDiagnosticsExport? _review;
    private string _filter = "ALL", _search = "";
    private bool _busy, _closed, _forbidden;

    public DeviceDiagnosticsWindow() => InitializeComponent();

    internal DeviceDiagnosticsWindow(ConsoleClient client, School school, string search) : this()
    {
        _client = client;
        _school = school;
        DiagnosticSchool.Text = school.Name + " · 全校原生设备目录";
        DiagnosticSearch.Text = search.Length <= DiagnosticSearch.MaxLength ? search : "";
        DiagnosticSearch.TextChanged += (_, _) => InvalidateQuery();
        DiagnosticFilter.SelectionChanged += (_, _) => InvalidateQuery();
        DiagnosticSearch.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await LoadAsync(false); } };
        Opened += async (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); await LoadAsync(false); if (!_closed) _accessCheck.Start(); };
        Closed += (_, _) => { _closed = true; _accessCheck.Stop(); Clear(); _lifetime.Cancel(); _lifetime.Dispose(); };
        _accessCheck.Tick += async (_, _) =>
        {
            if (_review is not null && !_busy && !_closed && !_forbidden)
                await Run(() => _client.VerifyDeviceDiagnosticsAccessAsync(_school.Id, _lifetime.Token));
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickLocation { get; set; }

    private string SelectedFilter => (DiagnosticFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
    private string Search => DiagnosticSearch.Text?.Trim() ?? "";

    private void InvalidateQuery()
    {
        if (_closed || _forbidden || _review is not null) return;
        Clear();
        DiagnosticNotice.Text = "筛选已更改，读取后可核对导出范围。";
        Render();
    }

    private void Clear()
    {
        _review = null;
        _pages.Clear();
        DiagnosticRows.ItemsSource = null;
        DiagnosticScopeText.Text = DiagnosticCounts.Text = DiagnosticObservation.Text = "";
    }

    private async void Read(object? sender, RoutedEventArgs e) => await LoadAsync(false);
    private async void More(object? sender, RoutedEventArgs e) => await LoadAsync(true);

    private async Task LoadAsync(bool more)
    {
        if (_busy || _closed || _forbidden || _review is not null) return;
        string? cursor = more ? _pages.LastOrDefault()?.NextCursor : null;
        if (more && cursor is null) return;
        if (!more) Clear();
        string filter = _filter = SelectedFilter;
        string search = _search = Search;
        await Run(async () =>
        {
            DiagnosticNotice.Text = "正在读取原生设备…";
            var page = await _client.ReadDeviceDiagnosticPageAsync(_school.Id, filter, search, cursor, _lifetime.Token);
            if (_closed || filter != SelectedFilter || search != Search) return;
            _pages.Add(page);
            DiagnosticRows.ItemsSource = _pages.SelectMany(p => p.Items).Select(row =>
                new DeviceDiagnosticDisplayRow(row.DeviceName ?? row.ScreenBindingName ?? row.DeviceId,
                    row.State != "ACTIVE" ? "授权不可用" : row.Connectivity switch { "ONLINE" => "在线", "OFFLINE" => "离线", _ => "待核对" })).ToArray();
            DiagnosticNotice.Text = page.MatchedTotal == 0 ? "本次筛选没有匹配的原生设备。" : "仅导出已经加载的设备状态。";
        });
    }

    private void Review(object? sender, RoutedEventArgs args)
    {
        if (_closed || _busy || _forbidden || _pages.Count == 0) return;
        string? version = typeof(DeviceDiagnosticsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _review = DeviceDiagnostics.Create(_client.ServerUri, version, _school.Id, _filter, _search.Length > 0, _pages, DateTimeOffset.UtcNow);
        string caption = _filter switch { "ATTENTION" => "需要核对", "ONLINE" => "在线", "INACTIVE" => "授权不可用", _ => "全部状态" };
        DiagnosticScopeText.Text = $"{_school.Name} · {caption} · " +
            (_review.HasSearch ? $"目录搜索「{_search}」" : "未应用搜索");
        DiagnosticCounts.Text = $"服务器匹配 {_review.MatchedTotal} 台 · 已加载 {_review.LoadedCount} 台 · 实际导出 {_review.ExportedCount} 台" +
            (_review.OmittedLoadedCount == 0 ? "" : $"\n超过 200 台上限，已加载的 {_review.OmittedLoadedCount} 台不写入。");
        DiagnosticObservation.Text = $"观测时间 {Stamp(_review.OldestObservationAt)}" +
            (_review.LatestObservationAt == _review.OldestObservationAt ? "" : $" 至 {Stamp(_review.LatestObservationAt)}") +
            (_review.Stale ? " · 部分观测已过期或时间异常" : " · 按此次读取的时间") +
            (_review.HasMore ? "\n仍有下一页，未加载的设备不写入。" : "\n已到当前分页末尾。");
        Render();
        DiagnosticSave.Focus();
    }

    private void Back(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (_review is null) { Close(); return; }
        _review = null;
        Render();
        DiagnosticReview.Focus();
    }

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (_review is not { } snapshot || _closed || _busy || _forbidden) return;
        var token = _lifetime.Token;
        await Run(async () =>
        {
            await _client.VerifyDeviceDiagnosticsAccessAsync(_school.Id, token);
            if (_closed) return;
            var pick = PickLocation ?? StorageProvider.SaveFilePickerAsync;
            using var file = await pick(new FilePickerSaveOptions
            {
                Title = "保存原生设备诊断", SuggestedFileName = "npep-device-diagnostics.json", DefaultExtension = "json",
                FileTypeChoices = [new("JSON") { Patterns = ["*.json"] }], ShowOverwritePrompt = true
            });
            if (_closed || file is null) return;
            await _client.VerifyDeviceDiagnosticsAccessAsync(_school.Id, token);
            if (_closed || !ReferenceEquals(_review, snapshot)) return;
            if (!snapshot.Stale && DateTimeOffset.UtcNow - snapshot.OldestObservationAt > TimeSpan.FromMinutes(1))
            {
                _review = null;
                DiagnosticNotice.Text = "选择位置期间观测已过期，请重新核对导出范围。";
                return;
            }
            string? target = file.TryGetLocalPath();
            if (target is null) { DiagnosticNotice.Text = "请选择 Windows 本机可写入的文件位置。"; return; }
            await WriteDiagnosticAsync(target, snapshot, token);
            if (!_closed) DiagnosticNotice.Text = $"已保存 {snapshot.ExportedCount} 台设备的诊断 JSON。";
        });
    }

    private static async Task WriteDiagnosticAsync(string path, DeviceDiagnosticsExport snapshot, CancellationToken token)
    {
        string target = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".npep-diagnostic-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                created = true;
                await output.WriteAsync(Encoding.UTF8.GetBytes(DeviceDiagnostics.Serialize(snapshot)), token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
    }

    private async Task Run(Func<Task> action)
    {
        _busy = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _forbidden = true;
                Clear();
                DiagnosticSearch.Text = "";
                DiagnosticSchool.Text = "当前学校的诊断访问已失效";
                SessionExpired?.Invoke(error);
            }
            if (!_closed) DiagnosticNotice.Text = error.Message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { if (!_closed) DiagnosticNotice.Text = "无法保存到所选位置，请核对目录权限后重试。"; }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        bool review = _review is not null;
        bool ready = !_busy && !_forbidden;
        DiagnosticListPane.IsVisible = !review;
        DiagnosticReviewPane.IsVisible = review;
        DiagnosticRead.IsEnabled = DiagnosticFilter.IsEnabled = DiagnosticSearch.IsEnabled = ready && !review;
        int loaded = _pages.Sum(page => page.Items.Length);
        DiagnosticMore.IsEnabled = ready && _pages.LastOrDefault()?.NextCursor is not null;
        DiagnosticMore.IsVisible = _pages.LastOrDefault()?.NextCursor is not null;
        DiagnosticReview.IsVisible = !review;
        DiagnosticReview.IsEnabled = ready && _pages.Count > 0;
        DiagnosticSave.IsVisible = review;
        DiagnosticSave.IsEnabled = ready && review;
        DiagnosticReturn.Content = review ? "返回设备筛选" : "返回";
        DiagnosticLoadState.Text = _pages.Count == 0 ? "读取后显示匹配和已加载数量。" :
            $"匹配 {_pages[^1].MatchedTotal} 台，已加载 {loaded} 台。最多导出 200 台。";
    }

    private static string Stamp(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss 'UTC+8'");
}
