using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class TeacherAccessWindow : Window
{
    private sealed record Pending(Guid Request, string Version, string Label);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string, TeacherAccessTarget), Pending>> Requests = new();
    private readonly ConsoleClient _client;
    private readonly School _school;
    private readonly TeacherAccessTarget _target;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _scope = Text();
    private readonly TextBlock _impact = Text();
    private readonly TextBlock _notice = Text();
    private readonly Button _confirm = new() { Classes = { "action", "accent" } };
    private readonly Button _reload = new() { Content = "重新核对", Classes = { "action" } };
    private readonly Button _back = new() { Content = "返回", Classes = { "action" } };
    private TeacherAccessPreview? _preview;
    private bool _busy, _writing, _closed, _forced, _mayRetry;

    internal Action<ConsoleApiException>? AccessLost { get; init; }
    internal bool Changed { get; private set; }

    internal TeacherAccessWindow(ConsoleClient client, School school, TeacherAccessTarget target)
    {
        (_client, _school, _target) = (client, school, target);
        Title = target.Action switch
        {
            "REMOVE" => "移除这条直接访问",
            "REVOKE" => "撤回这条邀请",
            _ => "核对原任课的访问"
        };
        Width = 550;
        Height = 400;
        MinWidth = 400;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10 };
        _scope.Text = $"{school.Name} · 原学期 {target.TermId}\n教学空间编号 {target.WorkspaceId}";
        _scope.FontWeight = FontWeight.SemiBold;
        root.Children.Add(_scope);
        var contents = new StackPanel { Spacing = 9, Children = {_impact, _notice} };
        var scroll = new ScrollViewer { Content = contents, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, Children = {_back, _reload, _confirm} };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Identify(_scope, "TeacherAccessScope");
        Identify(_impact, "TeacherAccessImpact");
        Identify(_notice, "TeacherAccessNotice");
        Identify(_confirm, "TeacherAccessConfirm");
        Identify(_reload, "TeacherAccessReload");
        Identify(_back, "TeacherAccessBack");
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
        _confirm.Click += async (_, _) => await RunAsync(SaveAsync);
        _reload.Click += async (_, _) => await RunAsync(ReadAsync);
        _back.Click += (_, _) => Close();
        Opened += async (_, _) => { _back.Focus(); await RunAsync(ReadAsync); };
        Closing += (_, args) => { if (_writing && !_forced) args.Cancel = true; };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || _writing) return;
            args.Handled = true;
            Close();
        };
        Render();
    }

    private Dictionary<(string, TeacherAccessTarget), Pending> PendingRequests => Requests.GetOrCreateValue(_client);
    private (string, TeacherAccessTarget) KeyOfRequest => (_school.Id, _target);

    internal static (TeacherAccessTarget Target, Guid Id, string Label)[] PendingFor(ConsoleClient client, string schoolId, string termId) =>
        Requests.TryGetValue(client, out var saved) ? saved.Where(row => row.Key.Item1 == schoolId && row.Key.Item2.TermId == termId)
            .Select(row => (row.Key.Item2, row.Value.Request, row.Value.Label)).ToArray() : [];

    internal static void Forget(ConsoleClient client, string? schoolId = null)
    {
        if (schoolId is null) { Requests.Remove(client); return; }
        if (!Requests.TryGetValue(client, out var saved)) return;
        foreach (var key in saved.Keys.Where(key => key.Item1 == schoolId).ToArray()) saved.Remove(key);
    }

    internal void CloseForScopeChange()
    {
        _forced = true;
        Close();
    }

    private async Task ReadAsync()
    {
        if (PendingRequests.TryGetValue(KeyOfRequest, out var pending))
        {
            await VerifyAsync(pending);
            return;
        }
        _preview = null;
        var observed = await _client.PreviewTeacherAccessAsync(_school.Id, _target, _lifetime.Token);
        if (!_closed) Present(observed);
    }

    private async Task SaveAsync()
    {
        if (PendingRequests.TryGetValue(KeyOfRequest, out var saved) && !_mayRetry) return;
        if (saved is null && _preview is not { CanApply: true }) return;
        var pending = saved ?? new(Guid.NewGuid(), _preview!.Version, $"{Title} · {_preview.RoomName}（{_preview.RoomCode}） · {_preview.DisplayName}");
        _mayRetry = false;
        PendingRequests[KeyOfRequest] = pending;
        TeacherAccessResult? response = null;
        try
        {
            _writing = true;
            Render();
            response = await _client.CommitTeacherAccessAsync(_school.Id, _target, pending.Version, pending.Request, _lifetime.Token);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500) { }
        catch (ConsoleApiException)
        {
            PendingRequests.Remove(KeyOfRequest);
            _preview = null;
            throw;
        }
        finally { _writing = false; if (!_closed) Render(); }
        if (_closed) return;
        if (response is null) await VerifyAsync(pending);
        else if (response.Receipt is { } receipt) Accept(receipt);
        else if (response.Preview is { } current)
        {
            PendingRequests.Remove(KeyOfRequest);
            Present(current);
            _notice.Text = "原关系已变化，本次未保存。请核对服务器当前值。" + (current.Reason is { } reason ? "\n" + reason : "");
            _back.Focus();
        }
    }

    private async Task VerifyAsync(Pending pending)
    {
        _mayRetry = false;
        _notice.Text = $"正在核对本人请求 {pending.Request:D}";
        var receipt = await _client.GetTeacherAccessReceiptAsync(_school.Id, _target, pending.Request, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null) Accept(receipt);
        else
        {
            _mayRetry = true;
            _notice.Text = $"暂未找到本人回执 {pending.Request:D}。可以只读重查，或按相同目标和编号重试。";
        }
    }

    private void Accept(TeacherAccessReceipt receipt)
    {
        PendingRequests.Remove(KeyOfRequest);
        Changed = true;
        _preview = null;
        _notice.Text = $"已核对本人回执 {receipt.RequestId:D}。";
        _back.Focus();
    }

    private void Present(TeacherAccessPreview preview)
    {
        _scope.Text = $"{_school.Name} · {preview.TermName}（{TermStatus(preview.TermStatus)}）\n{preview.RoomName}（{preview.RoomCode}）";
        var text = new List<string>
        {
            (preview.DisplayName ?? "原对象已变化") + (preview.Identity is { } identity ? $"（{identity}）" : ""),
            preview.Action == "REVOKE" ? $"待认领邀请 · {Role(preview.CurrentRole)}" :
                $"直接访问　{Role(preview.CurrentRole)} → {Role(preview.NewRole)}"
        };
        if (preview.Subjects.Length > 0) text.Add("原任课保持　" + string.Join("、", preview.Subjects));
        if (preview.Duties.Length > 0) text.Add($"职责访问保持　{Role(preview.DutyRole)} · {string.Join("、", preview.Duties)}");
        if (preview.FutureInvitation) text.Add("独立邀请仍在；归档解除后可按届时的认领规则办理。");
        _impact.Text = string.Join("\n", text);
        _notice.Text = preview.Reason;
        _preview = preview;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy || Changed) return;
        _busy = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            _notice.Text = error.Message;
            if (error.StatusCode is 401 or 403)
            {
                Forget(_client, error.StatusCode == 403 ? _school.Id : null);
                _impact.Text = null;
                _preview = null;
                AccessLost?.Invoke(error);
            }
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        bool pending = PendingRequests.ContainsKey(KeyOfRequest);
        _back.IsEnabled = !_writing;
        _confirm.Content = pending ? "按原编号重试" : _target.Action == "REPAIR" ? "确认补办直接访问" : "确认" + Title;
        _confirm.IsEnabled = !_busy && !Changed && (pending ? _mayRetry : _preview?.CanApply == true);
        _reload.Content = pending ? "只读核对回执" : "重新核对";
        _reload.IsEnabled = !_busy && !Changed;
    }

    private static string TermStatus(string status) => status switch { "ACTIVE" => "启用", "DRAFT" => "草稿", _ => "归档" };
    private static string Role(string? role) => role switch { "TEACHER" => "教师", "ASSISTANT" => "助教", "VIEWER" => "只读", null => "无", _ => role };
    private static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap };
    private static void Identify(Control control, string name) => AutomationProperties.SetAutomationId(control, name);
}
