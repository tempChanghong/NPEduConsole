using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassMaintenanceWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly string _schoolId = "";
    private readonly string _termId = "";
    private readonly string _classId = "";
    private readonly bool _initiallyActive;
    private readonly string _initialName = "";
    private readonly ClassCreationContext? _specifiedTerm;
    private readonly CancellationTokenSource _lifetime = new();
    private ClassMaintenanceSnapshot? _baseline;
    private ClassDeactivationPreview? _preview;
    private PendingChange? _unconfirmed;
    private bool _busy;
    private bool _checkingRequired;
    private bool _blocked;
    private bool _showImpact;
    private bool _closing;

    private sealed record PendingChange(bool Active, string Name, string Code, bool Selectable)
    {
        public bool Matches(ClassMaintenanceSnapshot room) => room.IsActive == Active && room.Name == Name &&
            room.Code == Code && room.IsStudentSelectable == Selectable;
    }

    public ClassMaintenanceWindow() => InitializeComponent();

    public ClassMaintenanceWindow(ConsoleClient client, School school, Classroom room, ClassCreationContext? specifiedTerm = null) : this()
    {
        _client = client;
        _schoolId = school.Id;
        _termId = room.TermId ?? throw new ArgumentException("A current term is required.", nameof(room));
        _classId = room.Id;
        _initiallyActive = room.IsActive;
        _initialName = room.Name;
        _specifiedTerm = specifiedTerm;
        MaintenanceSchool.Text = specifiedTerm is null ? school.Name : $"{school.Name} / {specifiedTerm.TermName} · {specifiedTerm.TermStatus}";
        MaintenanceHeading.Text = room.Name;
        Opened += async (_, _) => await ReadClassAsync();
        Closing += (_, args) => args.Cancel = _busy && !_closing;
        Closed += (_, _) =>
        {
            _closing = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public event Action<ConsoleApiException>? SessionExpired;
    public ClassMaintenanceSnapshot? ObservedChange { get; private set; }
    public string? Outcome { get; private set; }

    public void CloseForScopeChange()
    {
        _closing = true;
        Close();
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args) => await ReadClassAsync();

    private async Task ReadClassAsync()
    {
        if (_busy || _blocked || _closing) return;
        SetBusy(true);
        MaintenanceFeedback.Text = "正在读取服务器上的班级…";
        try
        {
            var room = _specifiedTerm is { } context
                ? await _client.GetTermClassMaintenanceAsync(context, _classId, _lifetime.Token)
                : await _client.GetCurrentClassMaintenanceAsync(_schoolId, _termId, _classId, _lifetime.Token);
            if (_closing) return;
            var previous = _baseline;
            _baseline = room;
            _preview = null;
            _showImpact = false;
            _checkingRequired = false;
            MaintenanceHeading.Text = room.Name;
            if (previous is null || previous.IsActive != room.IsActive)
            {
                FillFields(room);
                ObservedChange = room;
            }
            else if (previous.UpdatedAt != room.UpdatedAt)
            {
                ObservedChange = room;
            }
            if (previous is null && room.IsActive == _initiallyActive && room.Name == _initialName) ObservedChange = null;
            MaintenanceServerCard.IsVisible = previous is not null;
            MaintenanceServerState.Text = $"服务器当前班级：{room.Name}\n代码：{room.Code}\n学生自选：{(room.IsStudentSelectable ? "允许" : "不允许")} · {(room.IsActive ? "启用" : "已停用")}";
            if (_unconfirmed is { } pending)
            {
                Outcome = pending.Matches(room)
                    ? "已核对，服务器上的班级与本次提交一致。"
                    : "已核对，服务器上的班级与本次提交不同，请查看当前状态。";
                MaintenanceFeedback.Text = Outcome;
                ObservedChange = room;
                _unconfirmed = null;
            }
            else
            {
                MaintenanceFeedback.Text = previous is null ? "" : room.IsActive
                    ? "已读取服务器当前内容。草稿仍保留，请核对后再保存。"
                    : "已读取服务器当前状态。";
            }
        }
        catch (Exception error) when (IsServiceError(error)) { ReportError(error); }
        finally { SetBusy(false); }
        if (!_closing && _baseline?.IsActive == true && !_checkingRequired) MaintenanceName.Focus();
    }

    private void FillFields(ClassMaintenanceSnapshot room)
    {
        MaintenanceName.Text = room.Name;
        MaintenanceCode.Text = room.Code;
        MaintenanceStudentSelectable.IsChecked = room.IsStudentSelectable;
    }

    private PendingChange Draft() => new(true, MaintenanceName.Text?.Trim() ?? "",
        MaintenanceCode.Text?.Trim().ToUpperInvariant() ?? "", MaintenanceStudentSelectable.IsChecked == true);

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is not { IsActive: true } room || _showImpact || _checkingRequired) return;
        var draft = Draft();
        if (draft.Matches(room))
        {
            MaintenanceFeedback.Text = "班级内容没有变化。";
            return;
        }
        await WriteAsync(draft, ct => _client.EditCurrentClassAsync(room, draft.Name, draft.Code, draft.Selectable, ct), "已保存班级修改。");
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args) => await ReadImpactAsync();

    private async Task ReadImpactAsync(string? feedback = null)
    {
        if (_busy || _closing || _blocked || _checkingRequired || _baseline is not { IsActive: true } room) return;
        if (!Draft().Matches(room))
        {
            MaintenanceFeedback.Text = "还有未保存的班级修改。请先保存修改，或恢复原值后再查看停用影响。";
            return;
        }
        _showImpact = true;
        _preview = null;
        MaintenanceImpactSummary.SetImpact(null);
        MaintenanceFeedback.Text = feedback;
        SetBusy(true);
        try
        {
            var preview = await _client.PreviewClassDeactivationAsync(room, _lifetime.Token);
            if (_closing) return;
            if (preview.Classroom.UpdatedAt != room.UpdatedAt)
                throw new ConsoleApiException(409, "该班已被修改。请先核对服务器班级，再重新查看停用影响。", "ORGANIZATION_VERSION_CONFLICT");
            _preview = preview;
            MaintenanceImpactSummary.SetImpact(preview.Counts);
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (!_closing)
            {
                ReportError(error);
                MaintenanceImpactSummary.SetImpact(null, MaintenanceFeedback.Text);
            }
        }
        finally { SetBusy(false); }
    }

    private void CancelImpactClicked(object? sender, RoutedEventArgs args)
    {
        _showImpact = false;
        _preview = null;
        Render();
    }

    private async void DeactivateClicked(object? sender, RoutedEventArgs args)
    {
        if (_preview is not { } preview || _checkingRequired) return;
        var room = preview.Classroom;
        await WriteAsync(new(false, room.Name, room.Code, room.IsStudentSelectable),
            ct => _client.DeactivateCurrentClassAsync(preview, ct), "已停用班级。可在「已停用」筛选中找回原班。");
    }

    private async void ReactivateClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is not { IsActive: false } room || _checkingRequired) return;
        await WriteAsync(new(true, room.Name, room.Code, room.IsStudentSelectable),
            ct => _client.ReactivateCurrentClassAsync(room, ct), "已重新启用原班。可打开学生名单继续维护。");
    }

    private async Task WriteAsync(PendingChange change, Func<CancellationToken, Task<ClassMaintenanceSnapshot>> send, string success)
    {
        if (_busy || _blocked || _closing) return;
        SetBusy(true);
        MaintenanceFeedback.Text = "正在保存…";
        string? impactChanged = null;
        bool codeConflict = false;
        try
        {
            var room = await send(_lifetime.Token);
            if (_closing) return;
            if (!change.Matches(room)) throw new ConsoleApiException(0, "服务器回执与提交不一致。");
            ObservedChange = room;
            Outcome = success;
            CloseForScopeChange();
        }
        catch (ConsoleApiException error) when (error.Code == "CLASS_DEACTIVATION_IMPACT_CHANGED")
        {
            _preview = null;
            impactChanged = error.Message;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            codeConflict = error is ConsoleApiException { Code: "WORKSPACE_CODE_EXISTS" };
            if (!_closing && (error is HttpRequestException or OperationCanceledException ||
                error is ConsoleApiException { StatusCode: 0 or 408 or >= 500 }))
            {
                _unconfirmed = change;
                _checkingRequired = true;
                _preview = null;
                Outcome = "上次提交结果未确认。请重新打开班级维护核对服务器上的当前状态。";
                MaintenanceFeedback.Text = "提交结果暂未确认。请先点击「核对服务器班级」。";
            }
            else ReportError(error);
        }
        finally { SetBusy(false); }
        if (codeConflict && !_closing) MaintenanceCode.Focus();
        if (impactChanged is not null) await ReadImpactAsync(impactChanged);
    }

    private void ReportError(Exception error)
    {
        if (_closing) return;
        if (error is ConsoleApiException { StatusCode: 401 } expired)
        {
            SessionExpired?.Invoke(expired);
            return;
        }
        MaintenanceFeedback.Text = error is ConsoleApiException service ? service.Message : "暂时无法连接学校服务，请重试。";
        if (error is ConsoleApiException { Code: "CLASS_MAINTENANCE_TERM_CHANGED" or "ADMIN_CLASS_NOT_FOUND" or "CLASS_MAINTENANCE_UNSUPPORTED" }
            or ConsoleApiException { StatusCode: 403 })
        {
            _blocked = true;
            _preview = null;
            Outcome = MaintenanceFeedback.Text;
        }
        else if (error is ConsoleApiException { Code: "ORGANIZATION_VERSION_CONFLICT" or "CLASS_MAINTENANCE_STATE_CHANGED" or "EXPECTED_UPDATED_AT_INVALID" })
        {
            MaintenanceServerCard.IsVisible = false;
            _checkingRequired = true;
            _preview = null;
        }
    }

    private static bool IsServiceError(Exception error) =>
        error is ConsoleApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (!_closing) Render();
    }

    private void Render()
    {
        var room = _baseline;
        bool canWrite = !_busy && !_blocked && !_checkingRequired;
        MaintenanceProgress.IsVisible = _busy;
        MaintenanceFields.IsVisible = room?.IsActive == true && !_showImpact;
        MaintenanceFields.IsEnabled = !_busy && !_blocked;
        SaveMaintenanceButton.IsEnabled = canWrite;
        ReadDeactivationImpactButton.IsEnabled = canWrite;
        MaintenanceInactivePanel.IsVisible = room?.IsActive == false;
        ReactivateClassButton.IsVisible = MaintenanceInactivePanel.IsVisible;
        ReactivateClassButton.IsEnabled = canWrite;
        MaintenanceImpactPanel.IsVisible = _showImpact;
        MaintenanceScreenEffect.IsVisible = _preview?.Counts.ScreenBindings > 0;
        ConfirmDeactivateButton.IsVisible = _showImpact;
        ConfirmDeactivateButton.IsEnabled = canWrite && _preview is not null;
        RefreshImpactButton.IsVisible = _showImpact && _preview is null && !_checkingRequired && !_blocked;
        RefreshImpactButton.IsEnabled = !_busy;
        CancelImpactButton.IsVisible = _showImpact;
        CancelImpactButton.IsEnabled = !_busy;
        CheckMaintenanceButton.IsVisible = !_blocked && (room is null || _checkingRequired);
        CheckMaintenanceButton.IsEnabled = !_busy;
        CloseMaintenanceButton.IsEnabled = !_busy;
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
}
