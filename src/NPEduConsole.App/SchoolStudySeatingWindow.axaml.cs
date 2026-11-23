using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;
using SkiaSharp;

namespace NPEduConsole.App;

public sealed record StudySeatingDeviceView(string Name, string Binding, string Configuration, string Download, string Applied, string Opened,
    StudySeatingBinding Registration)
{
    public bool CanOpenField => Registration.DeviceId is not null;
}

public sealed partial class SchoolStudySeatingWindow : Window
{
    private sealed record History(StudySeatingVersion Version)
    {
        public override string ToString() => $"版本 {Version.Revision} · {(Version.Hash is null ? "撤回" : Version.Restorable ? "有图像" : "仅保留记录")}";
    }
    private sealed record Operation(StudySeatingEdit Edit, Guid Id, byte[]? Preview);
    private sealed record ConfigurationEvidence(StudyEvidenceReceipt? Receipt, string? Error);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<StudyEvidenceScope, Operation>> PendingSaves = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, ConfigurationEvidence> _configuration = [];
    private readonly ConsoleClient _client = null!;
    private readonly StudyEvidenceScope _scope = null!;
    private string _schoolName = "", _classroomName = "";
    private DeviceFieldActionWindow? _fieldAction;
    private SchoolStudySeating? _snapshot;
    private StudySeatingEdit? _draft;
    private StudySeatingDeviceView[] _displayedDevices = [];
    private byte[]? _draftPreview;
    private Bitmap? _bitmap;
    private long _seatReadTime;
    private bool _busy, _sending, _closed, _allowClose, _filling, _retry, _needsReview, _discardAsked;

    public SchoolStudySeatingWindow() => InitializeComponent();

    internal SchoolStudySeatingWindow(ConsoleClient client, string school, string classroom, StudyEvidenceScope scope) : this()
    {
        _client = client;
        _scope = scope;
        _schoolName = school;
        _classroomName = classroom;
        SeatTitle.Text = school + " · " + classroom + "座位图";
        _timer.Tick += (_, _) => { if (SeatTabs.SelectedIndex == 1) RenderDevices(); };
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(async () =>
            {
                if (Pending is { } operation)
                {
                    SeatPublished.Text = "本班座位图的原操作回执待核对。";
                    ShowPendingPreview(operation);
                    await VerifyAsync(operation);
                    if (!_closed && Pending is not null) await LoadAsync(preserve: true);
                    return;
                }
                await LoadAsync();
            });
        };
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = _sending || _draft is not null && Pending is null;
            if (args.Cancel && !_sending) WarnClose();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _bitmap?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        UpdateControls();
    }

    internal void CloseForScopeChange() { _allowClose = true; _fieldAction?.Close(); Close(); }
    private Dictionary<StudyEvidenceScope, Operation> Operations => PendingSaves.GetOrCreateValue(_client);
    private Operation? Pending => Operations.GetValueOrDefault(_scope);
    private History? SelectedHistory => SeatVersions.SelectedItem as History;
    private bool PreviewSelected => SeatTabs.SelectedItem == SeatPreviewTab;

    private async Task LoadAsync(bool preserve = false)
    {
        if (!preserve) { _draft = null; _draftPreview = null; _needsReview = false; }
        _seatReadTime = Stopwatch.GetTimestamp();
        _configuration.Clear();
        _snapshot = null;
        SeatPublished.Text = "正在核对学校当前发布版本…";
        RenderDevices();
        var result = await _client.ReadSchoolSeatingAsync(_scope, _lifetime.Token);
        if (_closed) return;
        _snapshot = result;
        var current = result.Current;
        SeatPublished.Text = current.Revision == 0 ? "学校尚未发布座位图。" :
            $"学校已{(current.Hash is null ? "撤回" : "发布")} · 版本 {current.Revision} · {Stamp(current.CreatedAt)}";
        _filling = true;
        SeatVersions.ItemsSource = result.Versions.Select(item => new History(item)).ToArray();
        SeatVersions.SelectedItem = null;
        _filling = false;
        if (Pending is { } operation) ShowPendingPreview(operation);
        else if (_draft is not null) SetImage(_draftPreview, _draft.Action == "withdraw" ? "待确认撤回当前座位图" : "保留的发布草稿");
        else await ReadImageAsync(current);
        if (_closed) return;
        RenderDevices();
        _timer.Start();
        foreach (var binding in result.Bindings.Where(row => row.DeviceId is not null))
        {
            try
            {
                var receipt = await _client.ReadStudyReceiptAsync(_scope, binding.DeviceId!, _lifetime.Token);
                if (_closed) return;
                _configuration[binding.BindingId] = new(receipt, null);
            }
            catch (ConsoleApiException error)
            { if (!_closed) _configuration[binding.BindingId] = new(null, error.Message); }
            if (_closed) return;
            RenderDevices();
        }
    }

    private async Task ReadImageAsync(StudySeatingVersion version)
    {
        if (!version.Restorable)
        {
            SetImage(null, version.Hash is null ? "暂无可预览的座位图" : $"版本 {version.Revision} · 图像已过保留期");
            return;
        }
        byte[] bytes = await _client.ReadSchoolSeatingImageAsync(_scope, version, _lifetime.Token);
        if (!_closed) SetImage(bytes, $"预览版本 {version.Revision} · {version.Width} × {version.Height}");
    }

    private async void VersionSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null || _busy || SelectedHistory is not { } entry) return;
        _draft = null;
        _draftPreview = null;
        _needsReview = false;
        await RunAsync(() => ReadImageAsync(entry.Version));
    }

    private async void ChooseFile(object? sender, RoutedEventArgs args)
    {
        if (_busy || _snapshot is null || Pending is not null) return;
        await RunAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "选择本班座位图", AllowMultiple = false,
                FileTypeFilter = [new("PNG 或 JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
            });
            if (_closed || files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            byte[] block = new byte[65536];
            for (int transferred; (transferred = await stream.ReadAsync(block, _lifetime.Token)) != 0;)
            {
                if (memory.Length + transferred > 20 * 1024 * 1024) throw new ArgumentException("请选择不超过 20 MiB 的图片。");
                memory.Write(block, 0, transferred);
            }
            byte[] image = await Task.Run(() => NormalizePng(memory.ToArray()), _lifetime.Token);
            if (!_closed) StageImage(image);
        });
    }

    internal void StageImage(byte[] normalized)
    {
        if (_snapshot is not { } snapshot || Pending is not null) return;
        Stage(new(_scope, snapshot.Current.Revision, "image", normalized), normalized, "待发布的新图片");
    }

    private async void StageRestore(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is not { } snapshot || SelectedHistory?.Version is not {Restorable: true} version) return;
        await RunAsync(async () =>
        {
            var image = await _client.ReadSchoolSeatingImageAsync(_scope, version, _lifetime.Token);
            if (!_closed) Stage(new(_scope, snapshot.Current.Revision, "restore", RestoreRevision: version.Revision),
                image, $"将版本 {version.Revision} 的图片重新发布为新版本");
        });
    }

    private void StageWithdraw(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is {Current.Hash: not null} snapshot)
            Stage(new(_scope, snapshot.Current.Revision, "withdraw"), null, "待确认撤回当前座位图");
    }

    private void Stage(StudySeatingEdit edit, byte[]? preview, string caption)
    {
        _draft = edit; _draftPreview = preview; _needsReview = _discardAsked = false;
        SeatClose.Content = "返回";
        SeatFeedback.Text = "";
        SetImage(preview, caption);
        UpdateControls();
    }

    private void SetImage(byte[]? bytes, string caption)
    {
        var previous = _bitmap;
        _bitmap = bytes is null ? null : new Bitmap(new MemoryStream(bytes));
        SeatImage.Source = _bitmap;
        previous?.Dispose();
        SeatImageLabel.Text = caption;
    }

    internal static byte[] NormalizePng(byte[] original)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg) ||
            codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 30_000_000)
            throw new ArgumentException("请重新选择可读取的 PNG 或 JPEG 图片，最长图像建议在 2400 像素以内。");
        using var source = SKBitmap.Decode(codec) ?? throw new ArgumentException("图片内容无法读取。");
        using var sourceImage = SKImage.FromBitmap(source);
        double scale = Math.Min(1, 2400d / Math.Max(codec.Info.Width, codec.Info.Height));
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int width = Math.Max(1, (int)(codec.Info.Width * scale)), height = Math.Max(1, (int)(codec.Info.Height * scale));
            using var raster = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(raster))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
            }
            using var encoded = raster.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is not null && encoded.Size > 0)
            {
                byte[] plain = WithoutPngMetadata(encoded.AsSpan());
                if (plain.Length <= 2097152) return plain;
            }
            scale *= 0.72;
        }
        throw new ArgumentException("图片缩小后仍超出上传限制，请选择更小的文件。");
    }

    private static byte[] WithoutPngMetadata(ReadOnlySpan<byte> png)
    {
        // The KV seating format accepts only normalized image chunks. In
        // particular Skia emits its own ancillary sBIT; preserve the trusted
        // encoder's critical chunks (with their original CRCs) and omit metadata.
        using var stream = new MemoryStream(png.Length);
        stream.Write(png[..8]);
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            var type = png.Slice(offset + 4, 4);
            var chunk = png.Slice(offset, length + 12);
            if (type.SequenceEqual("IHDR"u8) || type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                stream.Write(chunk);
            offset += chunk.Length;
        }
        return stream.ToArray();
    }

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (_closed || _busy || !PreviewSelected) return;
        if (Pending is { } previous)
        {
            if (_retry) await RunAsync(() => SubmitAsync(previous));
            return;
        }
        if (_draft is not { } edit || _snapshot is null) return;
        if (_needsReview)
        {
            _draft = edit with {Revision = _snapshot.Current.Revision};
            _needsReview = false;
            SeatFeedback.Text = "已采用学校当前版本作为基准。请核对这份草稿并再次确认。";
            UpdateControls();
            return;
        }
        var operation = new Operation(edit, Guid.NewGuid(), _draftPreview);
        Operations[_scope] = operation;
        await RunAsync(() => SubmitAsync(operation));
    }

    private async Task SubmitAsync(Operation operation)
    {
        _retry = false;
        try
        {
            var saved = await PublishAsync(operation);
            if (!_closed) await SavedAsync(saved);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        { if (!_closed) await VerifyAsync(operation); }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            Operations.Remove(_scope);
            if (error.Code == "SEATING_CHANGED")
            {
                _draft = operation.Edit;
                _draftPreview = operation.Preview;
                _needsReview = true;
                await LoadAsync(preserve: true);
            }
            if (!_closed) SeatFeedback.Text = error.Message;
        }
    }

    private async Task<StudySeatingVersion> PublishAsync(Operation operation)
    {
        _sending = true;
        UpdateControls();
        try { return await _client.SaveSchoolSeatingAsync(operation.Edit, operation.Id, _lifetime.Token); }
        finally { _sending = false; if (!_closed) UpdateControls(); }
    }

    private async Task SavedAsync(StudySeatingVersion result)
    {
        Operations.Remove(_scope);
        SeatFeedback.Text = $"已核对学校{(result.Hash is null ? "撤回" : "发布")} · 版本 {result.Revision}。逐屏状态请单独核对。";
        await LoadAsync();
    }

    private async Task VerifyAsync(Operation operation)
    {
        _retry = false;
        SeatFeedback.Text = "正在按原编号核对本次操作…";
        var result = await _client.ReadSchoolSeatingSaveAsync(operation.Edit, operation.Id, _lifetime.Token);
        if (_closed) return;
        if (result is not null) await SavedAsync(result);
        else
        {
            _retry = true;
            ShowPendingPreview(operation);
            SeatFeedback.Text = "服务器暂未找到本次回执。可继续核对，或在「预览与发布」中查看原操作并确认重试。";
        }
    }

    private void ShowPendingPreview(Operation operation)
    {
        var action = operation.Edit.Action switch
        {
            "withdraw" => "撤回座位图",
            "restore" => $"重新发布历史版本 {operation.Edit.RestoreRevision}",
            _ => "发布新座位图"
        };
        var baseline = operation.Edit.Revision == 0 ? "原先尚未发布" : $"原学校版本 {operation.Edit.Revision}";
        SetImage(operation.Preview, $"原操作：{action} · {baseline}");
    }

    private void SeatTabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, SeatTabs)) UpdateControls();
    }

    private void OpenRetryPreview(object? sender, RoutedEventArgs args)
    {
        if (_busy || !_retry || Pending is null) return;
        SeatTabs.SelectedItem = SeatPreviewTab;
        SeatSave.Focus();
    }

    private async void CheckPending(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyAsync(operation));
    }

    private async void RefreshSeat(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        int? revision = _draft?.Revision;
        await LoadAsync(preserve: true);
        if (!_closed && Pending is null && revision is not null && _snapshot?.Current.Revision != revision) _needsReview = true;
    });

    private async void CancelDraft(object? sender, RoutedEventArgs args) => await RunAsync(() => LoadAsync());

    private void RenderDevices()
    {
        if (_snapshot is not { } snapshot)
        {
            _displayedDevices = [];
            SeatDevices.ItemsSource = null;
            SeatDeviceNote.Text = "正在读取逐屏证据。";
            return;
        }
        double elapsed = Stopwatch.GetElapsedTime(_seatReadTime).TotalMilliseconds;
        SeatDeviceNote.Text = snapshot.Bindings.Length == 0 ? "本班暂无启用的大屏。" : "每行是一台当前绑定的大屏。上报过期后显示待核对。";
        var rows = snapshot.Bindings.Select(binding =>
        {
            var state = _configuration.GetValueOrDefault(binding.BindingId);
            var receipt = snapshot.Receipts.FirstOrDefault(row => row.BindingId == binding.BindingId && row.DeviceId == binding.DeviceId);
            bool observation = receipt is {Fresh: true, ObservationAgeMs: >= 0} && receipt.ObservationAgeMs + elapsed < 15000;
            bool freshDownload = receipt is {Downloaded: true, DownloadedAgeMs: >= 0} && receipt.DownloadedAgeMs + elapsed < 15000;
            var config = state?.Receipt;
            string version = config?.Observation?.Version is {Length: >= 12} v ? v[..12] : "暂无";
            string configuration = binding.DeviceId is null ? "配置版本 · 等待现场接入" :
                config is null ? "配置版本 · 待核对" + (state?.Error is { } error ? " · " + error : "") :
                config.Observation is null ? "配置版本 · 待核对 · 尚无教室回执" :
                $"配置版本 · {version} · {config.Describe().Split('\n')[0]}{At(config.ReceivedAt)}";
            string download = freshDownload ? $"下载 · 已收到当前图 v{snapshot.Current.Revision}{At(receipt!.DownloadedAt)}" :
                $"下载 · 待核对{PriorVersion(receipt?.DownloadedRevision, receipt?.DownloadedAt)}";
            string applied = observation && receipt!.Current
                ? $"展示应用 · {(snapshot.Current.Hash is null ? "已确认清除" : $"已应用当前图 v{snapshot.Current.Revision}")}{At(receipt.ReceivedAt)}"
                : $"展示应用 · 待核对{PriorVersion(receipt?.Revision, receipt?.ReceivedAt)}";
            string opened = observation && receipt is {Visible: true, RenderAgeMs: >= 0 and < 1000, AppliedHash: not null}
                ? $"最近打开 · {(receipt.Current ? "当前图" : "旧图")} v{receipt.Revision}{At(receipt.ReceivedAt)}"
                : observation && receipt is {Visible: false} ? $"最近打开 · 上次观测未打开{At(receipt.ReceivedAt)}"
                : $"最近打开 · 待核对{At(receipt?.ReceivedAt)}";
            return new StudySeatingDeviceView(binding.Name, "绑定编号 " + binding.BindingId, configuration, download, applied, opened, binding);
        }).ToArray();
        if (_displayedDevices.SequenceEqual(rows)) return;
        _displayedDevices = rows;
        SeatDevices.ItemsSource = rows;
    }

    private async void OpenFieldAction(object? sender, RoutedEventArgs args)
    {
        if (_busy || _fieldAction is not null || sender is not Button { DataContext: StudySeatingDeviceView selected } anchor ||
            selected.Registration is not { DeviceId: { } device } registration) return;
        var window = new DeviceFieldActionWindow(_client, new(_scope.SchoolId, _scope.TermId, _scope.ClassId, registration.BindingId, device),
            _schoolName, _classroomName, registration.Name, device);
        _fieldAction = window;
        try { await window.ShowDialog(this); }
        finally { if (_fieldAction == window) _fieldAction = null; }
        if (_closed) return;
        if (window.Revoked) await RunAsync(() => LoadAsync(preserve: true));
        if (_closed) return;
        if (anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled && anchor.Focus()) anchor.BringIntoView();
        else if (_displayedDevices.FirstOrDefault(row => row.Registration.BindingId == registration.BindingId) is { } row)
        {
            SeatDevices.SelectedItem = row;
            SeatDevices.ScrollIntoView(row);
            SeatDevices.Focus();
        }
    }

    private static string PriorVersion(int? revision, DateTimeOffset? at) => revision is > 0 ? $" · 上次版本 {revision}{At(at)}" : At(at);
    private static string At(DateTimeOffset? time) => time is null ? "" : " · " + Stamp(time);
    private static string Stamp(DateTimeOffset? time) => time?.ToOffset(TimeSpan.FromHours(8)).ToString("M/d HH:mm:ss") ?? "暂无时间";

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || _closed) return;
        _busy = true;
        UpdateControls();
        try { await operation(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (!_closed)
            {
                if (_snapshot is null) SeatPublished.Text = "学校当前发布版本待核对。";
                SeatFeedback.Text = (Pending is null ? "" : "本次发布回执待核对。") + error.Message;
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        { if (!_closed) SeatFeedback.Text = error is ArgumentException ? error.Message : "图片暂不可读，请重新选择或刷新。"; }
        finally { _busy = false; if (!_closed) UpdateControls(); }
    }

    private void UpdateControls()
    {
        if (_client is null) return;
        var pending = Pending;
        bool editable = !_busy && _snapshot is not null && pending is null;
        SeatChoose.IsEnabled = SeatVersions.IsEnabled = editable;
        SeatUseCurrent.IsEnabled = editable && (SelectedHistory is not null || _draft is not null);
        SeatRestore.IsEnabled = editable && SelectedHistory?.Version.Restorable == true;
        SeatWithdraw.IsEnabled = editable && _snapshot?.Current.Hash is not null;
        SeatRefresh.IsEnabled = SeatDevices.IsEnabled = !_busy;
        SeatClose.IsEnabled = !_sending;
        SeatCheck.IsVisible = pending is not null;
        SeatCheck.IsEnabled = !_busy;
        SeatRetryInPreview.IsVisible = !PreviewSelected && pending is not null && _retry;
        SeatRetryInPreview.IsEnabled = !_busy;
        SeatSave.IsVisible = PreviewSelected && (pending is not null ? _retry : _draft is not null);
        SeatSave.IsEnabled = SeatSave.IsVisible && !_busy && (pending is not null || editable);
        SeatSave.Content = pending is not null ? "确认按原编号重试" : _needsReview ? "采用当前版本" :
            _draft?.Action switch {"withdraw" => "确认撤回", "restore" => "确认重新发布", _ => "确认发布"};
    }

    private void WarnClose()
    {
        _discardAsked = true;
        SeatClose.Content = "放弃草稿";
        SeatFeedback.Text = "本班仍有未发布的座位图操作。确认放弃后可以返回。";
    }

    private void CloseSeat(object? sender, RoutedEventArgs args)
    {
        if (_sending) return;
        if (_draft is not null && Pending is null)
        {
            if (!_discardAsked) { WarnClose(); return; }
            _allowClose = true;
        }
        Close();
    }
}
