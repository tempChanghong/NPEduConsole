using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public partial class TermDetailsView : UserControl
{
    private sealed record Operation(TermDetailsDraft Draft, TermDetailsPreview Preview, Guid Number);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Operation>> PendingByClient = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly ManagedAcademicTerm? _original;
    private TermDetailsDraft? _draft;
    private TermDetailsPreview? _preview;
    private bool _busy, _writing, _retry, _closed;

    public TermDetailsView() => InitializeComponent();

    internal TermDetailsView(ConsoleClient client, School school, ManagedAcademicTerm? original, TermDetailsDraft? seed) : this()
    {
        _client = client;
        _school = school;
        var pending = Pending;
        if (pending is not null) original = pending.Preview.Before;
        _original = original;
        var values = pending?.Draft ?? seed;
        DetailsHeading.Text = original is null ? "建立空白学期草稿" : "更正学期资料";
        DetailsScope.Text = $"{school.Name} · {original?.ToString() ?? "新学期草稿"}";
        DetailsPeriod.IsVisible = original is null;
        DetailsYear.Text = (values?.AcademicYear ?? original?.AcademicYear ?? DateTime.Today.Year).ToString(CultureInfo.InvariantCulture);
        DetailsSemester.Text = (values?.Semester ?? original?.Semester ?? 1).ToString(CultureInfo.InvariantCulture);
        DetailsName.Text = values?.Name ?? original?.Name;
        DetailsStartsAt.Text = values?.ChangeStart == true ? values.StartsAt : DateText(original?.StartsAt);
        DetailsEndsAt.Text = values?.ChangeEnd == true ? values.EndsAt : DateText(original?.EndsAt);
        Render();
    }

    internal Action? CloseRequested { get; set; }
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal ManagedAcademicTerm? Saved { get; private set; }
    internal static bool HasPending(ConsoleClient client, string schoolId) =>
        PendingByClient.TryGetValue(client, out var operations) && operations.ContainsKey(schoolId);
    private Dictionary<string, Operation> Operations => PendingByClient.GetOrCreateValue(_client);
    private Operation? Pending => Operations.GetValueOrDefault(_school.Id);
    internal bool CanClose() => !_writing;

    internal async Task StartAsync()
    {
        if (Pending is { } op)
        {
            ShowReview(op.Preview);
            await RunAsync(() => VerifyAsync(op));
        }
        if (!_closed) (Pending is null ? DetailsName : (Control)DetailsVerify).Focus();
    }

    internal void End()
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static string DateText(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private TermDetailsDraft? ReadDraft()
    {
        string name = DetailsName.Text?.Trim() ?? "";
        string start = DetailsStartsAt.Text?.Trim() ?? "", end = DetailsEndsAt.Text?.Trim() ?? "";
        if (name.Length == 0 || !int.TryParse(DetailsYear.Text, out int year) || year is < 2000 or > 2200 ||
            !int.TryParse(DetailsSemester.Text, out int semester) || semester is < 1 or > 3 || !ValidDate(start) || !ValidDate(end))
        {
            DetailsStatus.Text = "请填写学期名称、有效学年及期序。日期可留空，填写时请用 YYYY-MM-DD。";
            return null;
        }
        bool startChanged = start != DateText(_original?.StartsAt), endChanged = end != DateText(_original?.EndsAt);
        string? newName = name == _original?.Name ? null : name;
        if (newName is null && !startChanged && !endChanged)
        { DetailsStatus.Text = "学期资料未修改。"; return null; }
        return new(_original?.Id, newName, year, semester, startChanged, start.Length == 0 ? null : start,
            endChanged, end.Length == 0 ? null : end);
    }

    private static bool ValidDate(string text) => text.Length == 0 ||
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private void Edited(object? sender, TextChangedEventArgs args)
    {
        if (_client is null || _closed || _busy || Pending is not null) return;
        _preview = null;
        DetailsReview.IsVisible = false;
        DetailsStatus.Text = "";
        Render();
    }

    private async void ApplyClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } pending)
        {
            if (_retry) await SubmitAsync(pending);
            return;
        }
        if (_preview is { } reviewed && _draft is { } draft)
        {
            var op = new Operation(draft, reviewed, Guid.NewGuid());
            Operations[_school.Id] = op;
            await SubmitAsync(op);
        }
        else if (ReadDraft() is { } proposed)
        {
            var preview = await _client.PreviewTermDetailsAsync(_school.Id, proposed, _lifetime.Token);
            if (_closed) return;
            _draft = proposed;
            _preview = preview;
            ShowReview(preview);
            DetailsStatus.Text = "请核对预览后确认。";
        }
    });

    private async Task SubmitAsync(Operation op)
    {
        _writing = true;
        _retry = false;
        Render();
        try
        {
            var result = await _client.CommitTermDetailsAsync(_school.Id, op.Draft, op.Preview.Version, op.Number, _lifetime.Token);
            if (_closed) return;
            if (result.Receipt is { } receipt) { Finish(receipt); return; }
            Operations.Remove(_school.Id);
            _preview = null;
            ShowReview(result.Preview!);
            DetailsStatus.Text = "服务器上的学期已变化。本次未保存，原输入已保留，请重新预览。";
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException ||
            error is ConsoleApiException { StatusCode: 0 or 408 or >= 500 })
        {
            if (_closed) return;
            _writing = false;
            Render();
            await VerifyAsync(op);
        }
        catch (ConsoleApiException)
        {
            Operations.Remove(_school.Id);
            _preview = null;
            throw;
        }
        finally { _writing = false; }
    }

    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } op) await RunAsync(() => VerifyAsync(op));
    }

    private async Task VerifyAsync(Operation op)
    {
        try { Finish(await _client.GetTermDetailsReceiptAsync(_school.Id, op.Draft, op.Number, _lifetime.Token)); }
        catch (ConsoleApiException error) when (error.Code == "TERM_DETAILS_RECEIPT_NOT_FOUND")
        {
            if (_closed) return;
            _retry = true;
            DetailsStatus.Text = "暂未找到原回执。可以继续只读核对，或按同一编号重试。";
        }
    }

    private void Finish(TermDetailsReceipt receipt)
    {
        if (_closed) return;
        Operations.Remove(_school.Id);
        Saved = receipt.Term;
        _writing = false;
        CloseRequested?.Invoke();
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (ConsoleApiException error)
        {
            if (!_closed)
            {
                DetailsStatus.Text = error.Message;
                if (error.StatusCode is 401 or 403) SessionExpired?.Invoke(error);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            if (!_closed) DetailsStatus.Text = Pending is null ? "连接失败，请重试读取。" : "暂不能核对原操作，请按原编号再查。";
        }
        finally { _busy = false; Render(); }
    }

    private void ShowReview(TermDetailsPreview preview)
    {
        static string Describe(string name, DateTimeOffset? from, DateTimeOffset? to) =>
            $"{name} · {(from is null ? "起日未设" : DateText(from))} 至 {(to is null ? "止日未设" : DateText(to))}";
        DetailsBefore.Text = preview.Before is { } before ? "现在：" + Describe(before.Name, before.StartsAt, before.EndsAt) : "现在：此学年及期序尚无学期";
        var after = preview.After;
        DetailsAfter.Text = "保存后：" + Describe(after.Name, after.StartsAt, after.EndsAt) +
            (preview.Before is null ? " · 空白草稿" : " · 保留学期状态与大屏绑定");
        DetailsReview.IsVisible = true;
        DetailsReview.BringIntoView();
    }

    private void Render()
    {
        if (_closed || _client is null) return;
        bool pending = Pending is not null;
        DetailsInputs.IsEnabled = !_busy && !pending;
        DetailsBack.IsEnabled = !_writing;
        DetailsVerify.IsVisible = pending;
        DetailsVerify.IsEnabled = !_busy;
        DetailsApply.IsVisible = !pending || _retry;
        DetailsApply.IsEnabled = !_busy;
        DetailsApply.Content = pending ? "按原编号重试" : _preview is null ? "预览" : _original is null ? "确认建立草稿" : "确认保存";
    }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (CanClose()) CloseRequested?.Invoke();
    }
}
