using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record TeacherPinBatchPerson(string? AccountId, string Name, string Username, string Workspaces, bool Created);
internal sealed record TeacherPinBatchDisplay(string Identity, string State);

public sealed partial class TeacherBatchPinView : UserControl
{
    private sealed record Entry(TeacherPinBatchPerson Person, InitialPinDelivery<int> Delivery);

    private TeacherPinBatchScope? _scope;
    private string? _schoolName, _target, _website, _message;
    private TeacherPinBatchPerson[] _people = [];
    private Dictionary<string, InitialPinDelivery<int>>? _available;
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _directoryRetryExpiry = new() { Interval = TimeSpan.FromMinutes(10) };
    private Entry[]? _review;
    private bool _reviewing, _busy, _refreshRequired;

    public TeacherBatchPinView()
    {
        InitializeComponent();
        _directoryRetryExpiry.Tick += (_, _) =>
        {
            _directoryRetryExpiry.Stop();
            _available?.Clear();
            _message = "目录待核对时间已结束，本次 PIN 已清除。如需新的 PIN，请在教师账号页办理。";
            CloseOrOpenReview?.Invoke();
        };
    }

    internal event Action? CloseOrOpenReview;
    internal event Action? ReadWebsiteRequested;
    internal event Action? RefreshRequested;
    internal event Action? SaveRequested;
    internal bool Reviewing => _reviewing;
    internal bool DirectoryPending => _refreshRequired;

    internal void Bind(TeacherPinBatchScope scope, string schoolName, string target, TeacherPinBatchPerson[] people,
        Dictionary<string, InitialPinDelivery<int>> available)
    {
        Clear();
        _scope = scope;
        _schoolName = schoolName;
        _target = target;
        _people = people;
        _available = available;
    }

    internal void Clear()
    {
        _directoryRetryExpiry.Stop();
        _scope = null;
        _available = null;
        _schoolName = _target = _website = _message = null;
        _people = [];
        _delivered.Clear();
        _changed.Clear();
        _review = null;
        _reviewing = _refreshRequired = false;
        TeacherBatchPinRows.ItemsSource = null;
        IsVisible = TeacherBatchPinActions.IsVisible = TeacherBatchPinReview.IsVisible = false;
        TeacherBatchPinOpen.IsVisible = TeacherBatchPinRefresh.IsVisible = TeacherBatchPinSave.IsVisible = false;
        BatchPinTarget.Text = BatchPinWebsite.Text = BatchPinResult.Text = TeacherBatchPinStatus.Text = null;
    }

    internal void SetDirectoryPending(bool pending)
    {
        if (_scope is null) return;
        if (pending && !_refreshRequired) _directoryRetryExpiry.Start();
        if (!pending) _directoryRetryExpiry.Stop();
        _refreshRequired = pending;
        if (!pending) _message = null;
        else if (_available is { Count: > 0 }) _message = "账号已保存。目录暂时不可用，请留在本页，在 10 分钟内重新核对后另存。";
    }

    internal void RecordIndividualResult(string id, bool delivered)
    {
        if (_scope is null) return;
        (delivered ? _delivered : _changed).Add(id);
    }

    internal void Render(bool busy)
    {
        _busy = busy;
        int remaining = _available?.Count ?? 0;
        IsVisible = _scope is not null;
        TeacherBatchPinActions.IsVisible = !_reviewing;
        TeacherBatchPinReview.IsVisible = _reviewing;
        TeacherBatchPinOpen.IsVisible = remaining > 0 && !_refreshRequired;
        TeacherBatchPinOpen.IsEnabled = !busy;
        TeacherBatchPinOpen.Content = $"另存本次 {remaining} 位教师的初始 PIN…";
        TeacherBatchPinRefresh.IsVisible = _refreshRequired;
        TeacherBatchPinRefresh.IsEnabled = !busy;
        TeacherBatchPinRefresh.Content = remaining > 0 ? "重新核对可交付账号" : "刷新账号目录";
        BatchPinResult.Text = _message ?? (remaining == 0 && _delivered.Count > 0 ? $"已交付 {_delivered.Count} 位教师的初始 PIN。" : null);
        BatchPinResult.IsVisible = BatchPinResult.Text is not null;
        if (!_reviewing || _scope is not { } scope) return;

        BatchPinTarget.Text = $"{_target} · 学校代码 {scope.SchoolCode}";
        BatchPinWebsite.Text = _website is { } website ? $"师生网页 {website}" : "师生网页地址请向学校确认。";
        TeacherBatchPinStatus.Text = _message ?? $"{_review?.Length ?? 0} 位将写入 · {_people.Length - (_review?.Length ?? 0)} 位不纳入";
        TeacherBatchPinSave.Content = $"选择位置并另存 {_review?.Length ?? 0} 位…";
        TeacherBatchPinSave.IsVisible = _review is { Length: > 0 };
        TeacherBatchPinSave.IsEnabled = !busy;
        TeacherBatchPinCancel.IsEnabled = !busy;
    }

    internal void CancelReview()
    {
        if (!_reviewing || _busy) return;
        _reviewing = false;
        _review = null;
        TeacherBatchPinRows.ItemsSource = null;
        CloseOrOpenReview?.Invoke();
        FocusOpen();
    }

    internal async Task ReadWebsiteAsync(ConsoleClient client, CancellationToken token)
    {
        var scope = _scope;
        if (scope is null || !_reviewing) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var overview = await client.GetSchoolOverviewAsync(scope.SchoolId, deadline.Token);
            if (_scope != scope || !_reviewing || token.IsCancellationRequested) return;
            var web = SchoolAdminBrowser.Link(overview.AdminUrl, scope.SchoolId, null, "teachers");
            const string adminPath = "/classworks-admin";
            _website = web?.AbsolutePath.EndsWith(adminPath, StringComparison.Ordinal) == true
                ? web.GetLeftPart(UriPartial.Path)[..^adminPath.Length] + "/" : null;
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { }
    }

    internal async Task<bool> SaveAsync(ConsoleClient client, Window owner,
        Func<FilePickerSaveOptions, Task<IStorageFile?>>? selectFile, CancellationToken token)
    {
        if (!_reviewing || _scope is not { } scope || _schoolName is not { } schoolName ||
            _available is not { } available || _review is not { Length: > 0 } review) return false;
        _message = null;
        try
        {
            var select = selectFile ?? owner.StorageProvider.SaveFilePickerAsync;
            using var file = await select(new FilePickerSaveOptions
            {
                Title = "另存本次教师初始 PIN", SuggestedFileName = "教师初始凭据.csv", DefaultExtension = "csv",
                FileTypeChoices = [new("CSV 文件") { Patterns = ["*.csv"] }], ShowOverwritePrompt = true
            });
            if (!StillCurrent() || file is null) return false;
            var proofs = review.Select(entry => new TeacherPinBatchProof(entry.Delivery.Id, entry.Delivery.Username, entry.Delivery.Version)).ToArray();
            var changed = await client.VerifyTeacherPinBatchAsync(scope, proofs, token);
            if (!StillCurrent()) return false;
            if (changed.Count > 0)
            {
                foreach (var id in changed)
                {
                    available.Remove(id);
                    _changed.Add(id);
                }
                _review = null;
                _message = "账号或 PIN 已变化，文件未修改。请返回结果，重新核对仍可交付的教师。";
                ShowRows(changedFirst: true);
                return false;
            }
            var rows = review.Select(entry => entry.Delivery.CsvRow(entry.Person.Name, entry.Person.Workspaces)).ToArray();
            if (!await PinHandoffFile.WriteAsync(file, new UTF8Encoding(true), writer =>
                TeacherPinCsv.WriteAsync(writer, schoolName, scope.SchoolCode, rows, token), token, StillCurrent)) return false;
            foreach (var entry in review)
            {
                available.Remove(entry.Delivery.Id);
                _delivered.Add(entry.Delivery.Id);
            }
            _reviewing = false;
            _review = null;
            _message = $"已另存 {review.Length} 位教师的初始 PIN。";
            TeacherBatchPinRows.ItemsSource = null;
            return true;
        }
        catch (ConsoleApiException error) when (error.Code == "TEACHER_PIN_BATCH_SCOPE_CHANGED" || error.StatusCode is 404 or 409 or 426)
        {
            if (!StillCurrent()) return false;
            available.Clear();
            _review = null;
            _message = "原学校、学期或登录方式已变化，文件未修改。本次 PIN 已清除。";
            ShowRows();
            return false;
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (StillCurrent()) _message = "暂时无法核对账号，文件未修改。可以在本页重试。";
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            if (StillCurrent()) _message = "文件未保存，请核对文件位置或占用状态后重试。";
            return false;
        }

        bool StillCurrent() => !token.IsCancellationRequested && _scope == scope && _reviewing && _review == review &&
            review.All(entry => available.GetValueOrDefault(entry.Delivery.Id) == entry.Delivery);
    }

    private void ShowRows(bool changedFirst = false)
    {
        var people = changedFirst ? _people.OrderByDescending(person => _changed.Contains(person.AccountId ?? "")) : _people.AsEnumerable();
        TeacherBatchPinRows.ItemsSource = people.Select(person => new TeacherPinBatchDisplay($"{person.Name}（{person.Username}）",
            person.AccountId is { } id && _available?.ContainsKey(id) == true ? "可交付" :
            _delivered.Contains(person.AccountId ?? "") ? "已交付" :
            _changed.Contains(person.AccountId ?? "") ? "账号或 PIN 已变化" :
            person.Created ? "本页无可交付 PIN" : "沿用原凭据")).ToArray();
        if (changedFirst && TeacherBatchPinRows.Items[0] is { } first) TeacherBatchPinRows.ScrollIntoView(first);
    }

    private void OpenClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _refreshRequired || _available is not { Count: > 0 } available) return;
        _review = _people.Where(person => person.AccountId is { } id && available.ContainsKey(id))
            .Select(person => new Entry(person, available[person.AccountId!])).ToArray();
        if (_review.Length == 0) return;
        _reviewing = true;
        _message = null;
        ShowRows();
        CloseOrOpenReview?.Invoke();
        ReadWebsiteRequested?.Invoke();
    }

    internal void FocusReviewReturn() => FocusLater(TeacherBatchPinCancel);
    internal void FocusAfterFilePicker() => FocusLater(TeacherBatchPinSave.IsVisible ? TeacherBatchPinSave : TeacherBatchPinCancel);
    private void FocusOpen() => FocusLater(TeacherBatchPinOpen);
    private static void FocusLater(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (target.IsEffectivelyEnabled && target.IsEffectivelyVisible) target.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    private void RefreshClicked(object? sender, RoutedEventArgs args) => RefreshRequested?.Invoke();
    private void CancelClicked(object? sender, RoutedEventArgs args) => CancelReview();
    private void SaveClicked(object? sender, RoutedEventArgs args) => SaveRequested?.Invoke();
}
