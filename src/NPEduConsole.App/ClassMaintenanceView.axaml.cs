using System.Net.Http;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassMaintenanceView : UserControl
{
    private readonly ConsoleClient _client = null!;
    private readonly string _schoolId = "";
    private readonly string _schoolName = "";
    private readonly string _termId = "";
    private readonly string _classId = "";
    private readonly bool _initiallyActive;
    private readonly string _initialName = "";
    private readonly string? _initialCode;
    private readonly ClassCreationContext? _specifiedTerm;
    private string? _activeTermName;
    private readonly CancellationTokenSource _lifetime = new();
    private ClassMaintenanceSnapshot? _baseline;
    private ClassDeactivationPreview? _preview;
    private PendingChange? _unconfirmed;
    private bool _busy;
    private bool _checkingRequired;
    private bool _blocked;
    private bool _showImpact;
    private bool _closing;
    private bool _inactiveDraft;
    private bool _archiveEditing, _allowClose, _confirmDiscard;

    private sealed record PendingChange(bool Active, string Name, string Code, bool Selectable)
    {
        public bool Matches(ClassMaintenanceSnapshot room) => room.IsActive == Active && FieldsMatch(room);

        public bool FieldsMatch(ClassMaintenanceSnapshot room) => room.Name == Name && room.Code == Code &&
            room.IsStudentSelectable == Selectable;
    }

    public ClassMaintenanceView() => InitializeComponent();

    internal ClassMaintenanceView(ConsoleClient client, School school, Classroom room, ClassCreationContext? specifiedTerm = null) : this()
    {
        _client = client;
        _schoolId = school.Id;
        _schoolName = school.Name;
        _termId = room.TermId ?? throw new ArgumentException("A current term is required.", nameof(room));
        _classId = room.Id;
        _initiallyActive = room.IsActive;
        _initialName = room.Name;
        _initialCode = room.Code;
        _specifiedTerm = specifiedTerm;
        SetIdentity(room.Name, room.GradeName, room.Code, room.IsActive);
    }

    public event Action<ConsoleApiException>? SessionExpired;
    internal Action? CloseRequested { get; set; }
    public ClassMaintenanceSnapshot? ObservedChange { get; private set; }
    public string? Outcome { get; private set; }

    internal Task StartAsync() => ReadClassAsync();

    internal void End()
    {
        if (_closing) return;
        _closing = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _baseline = null;
        _preview = null;
        _unconfirmed = null;
        MaintenanceName.Clear();
        MaintenanceCode.Clear();
        MaintenanceServerState.Text = null;
        MaintenanceImpactSummary.SetImpact(null);
    }

    private void SetIdentity(string name, string? grade, string? code, bool active)
    {
        string term = _specifiedTerm is { } selected ? $"{selected.TermName} · {selected.TermStatus}" :
            _activeTermName is { } title ? $"{title} · 启用中" : "当前启用学期";
        MaintenanceSchool.Text = $"{_schoolName} / {term} / {grade ?? "未设年级"} / {code ?? "代码待核对"} · {(active ? "班级启用" : "班级停用")}";
        MaintenanceHeading.Text = $"班级资料 · {name}";
        ConfirmDeactivateLabel.Text = $"停用「{name}」";
        AutomationProperties.SetName(ConfirmDeactivateButton, ConfirmDeactivateLabel.Text);
        ReactivateClassButton.Content = $"重新启用「{name}」";
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args) => await ReadClassAsync();

    private async Task ReadClassAsync()
    {
        if (_busy || _blocked || _closing) return;
        SetBusy(true);
        SetFeedback("正在读取服务器上的班级…");
        try
        {
            if (_specifiedTerm is null && _activeTermName is null)
            {
                try
                {
                    var active = await _client.GetClassCreationContextAsync(_schoolId, _lifetime.Token);
                    if (_closing) return;
                    if (active.TermId != _termId)
                        throw new ConsoleApiException(409, "当前启用学期已变化。请返回班级目录核对。", "CLASS_MAINTENANCE_TERM_CHANGED");
                    _activeTermName = active.TermName;
                }
                catch (ConsoleApiException error) when (error.Code == "CLASS_CREATION_UNSUPPORTED") { }
            }
            var room = _specifiedTerm is { } context
                ? await _client.GetTermClassMaintenanceAsync(context, _classId, _lifetime.Token)
                : await _client.GetCurrentClassMaintenanceAsync(_schoolId, _termId, _classId, _lifetime.Token);
            if (_closing) return;
            var previous = _baseline;
            var draft = Draft();
            bool preserveDraft = _inactiveDraft || (previous is { IsActive: true } && !draft.Matches(previous));
            _inactiveDraft = !room.IsActive && preserveDraft && !draft.FieldsMatch(room);
            _baseline = room;
            _preview = null;
            _showImpact = false;
            _checkingRequired = false;
            SetIdentity(room.Name, room.GradeName, room.Code, room.IsActive);
            if (previous is null || previous.IsActive != room.IsActive)
            {
                if (!preserveDraft) FillFields(room);
                ObservedChange = room;
            }
            else if (previous.UpdatedAt != room.UpdatedAt)
            {
                ObservedChange = room;
            }
            if (previous is null && room.IsActive == _initiallyActive && room.Name == _initialName && room.Code == _initialCode) ObservedChange = null;
            ShowServerState(room, previous is not null);
            if (_unconfirmed is { } pending)
            {
                Outcome = pending.Matches(room)
                    ? "已核对，服务器上的班级与本次提交一致。"
                    : "已核对，服务器上的班级与本次提交不同，请查看当前状态。";
                SetFeedback(Outcome);
                ObservedChange = room;
                _unconfirmed = null;
            }
            else
            {
                SetFeedback(_inactiveDraft ? "班级已停用，原草稿仍保留。重新启用后可以继续编辑和保存。" :
                    previous is null ? null : room.IsActive
                    ? "已读取服务器当前内容。草稿仍保留，请核对后再保存。"
                    : "已读取服务器当前状态。");
            }
        }
        catch (Exception error) when (IsServiceError(error)) { ReportError(error); }
        finally { SetBusy(false); }
        if (!_closing && !_checkingRequired && _baseline is not null)
        {
            if (ArchiveReadOnly) CorrectArchivedClass.Focus();
            else if (_baseline.IsActive || _inactiveDraft) MaintenanceName.Focus();
            else ReactivateClassButton.Focus();
        }
    }

    private void FillFields(ClassMaintenanceSnapshot room)
    {
        MaintenanceName.Text = room.Name;
        MaintenanceCode.Text = room.Code;
        MaintenanceStudentSelectable.IsChecked = room.IsStudentSelectable;
    }

    private void ShowServerState(ClassMaintenanceSnapshot room, bool visible)
    {
        MaintenanceServerCard.IsVisible = visible;
        MaintenanceServerState.Text = $"服务器当前班级：{room.Name}\n代码：{room.Code}\n学生自选：{(room.IsStudentSelectable ? "允许" : "不允许")} · {(room.IsActive ? "启用" : "已停用")}";
    }

    private PendingChange Draft() => new(true, MaintenanceName.Text?.Trim() ?? "",
        MaintenanceCode.Text?.Trim().ToUpperInvariant() ?? "", MaintenanceStudentSelectable.IsChecked == true);

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is not { IsActive: true } room || _showImpact || _checkingRequired || ArchiveReadOnly) return;
        var draft = Draft();
        if (draft.Matches(room))
        {
            SetFeedback("班级内容没有变化。");
            return;
        }
        await WriteAsync(draft, ct => _client.EditCurrentClassAsync(room, draft.Name, draft.Code, draft.Selectable, ct), "已保存班级修改。");
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args) => await ReadImpactAsync();

    private async Task ReadImpactAsync(string? feedback = null)
    {
        if (_busy || _closing || _blocked || _checkingRequired || ArchiveReadOnly || _baseline is not { IsActive: true } room) return;
        if (!Draft().Matches(room))
        {
            SetFeedback("还有未保存的班级修改。请先保存修改，或恢复原值后再查看停用影响。");
            return;
        }
        _showImpact = true;
        _preview = null;
        MaintenanceImpactSummary.SetImpact(null);
        SetFeedback(feedback);
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
        if (!_closing && _showImpact) CancelImpactButton.Focus();
    }

    private void CancelImpactClicked(object? sender, RoutedEventArgs args)
    {
        _showImpact = false;
        _preview = null;
        Render();
    }

    private async void DeactivateClicked(object? sender, RoutedEventArgs args)
    {
        if (_preview is not { } preview || _checkingRequired || ArchiveReadOnly) return;
        var room = preview.Classroom;
        await WriteAsync(new(false, room.Name, room.Code, room.IsStudentSelectable),
            ct => _client.DeactivateCurrentClassAsync(preview, ct), "已停用班级。可在「已停用」筛选中找回原班。");
    }

    private async void ReactivateClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is not { IsActive: false } room || _checkingRequired || ArchiveReadOnly) return;
        await WriteAsync(new(true, room.Name, room.Code, room.IsStudentSelectable),
            ct => _client.ReactivateCurrentClassAsync(room, ct), "已重新启用原班。可打开学生名单继续维护。");
    }

    private async Task WriteAsync(PendingChange change, Func<CancellationToken, Task<ClassMaintenanceSnapshot>> send, string success)
    {
        if (_busy || _blocked || _closing) return;
        SetBusy(true);
        SetFeedback("正在保存…");
        string? impactChanged = null;
        bool codeConflict = false;
        bool resumedDraft = false;
        try
        {
            var room = await send(_lifetime.Token);
            if (_closing) return;
            if (!change.Matches(room)) throw new ConsoleApiException(0, "服务器回执与提交不一致。");
            ObservedChange = room;
            Outcome = success;
            if (_inactiveDraft && room.IsActive)
            {
                _baseline = room;
                _inactiveDraft = false;
                SetIdentity(room.Name, room.GradeName, room.Code, room.IsActive);
                ShowServerState(room, true);
                SetFeedback("已重新启用原班。请核对后保存原草稿。");
                resumedDraft = true;
            }
            else _allowClose = true;
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
                SetFeedback("提交结果暂未确认。请先点击「核对服务器班级」。");
            }
            else ReportError(error);
        }
        finally { SetBusy(false); }
        if (_allowClose) CloseRequested?.Invoke();
        if (resumedDraft && !_closing) MaintenanceName.Focus();
        if (codeConflict && !_closing) MaintenanceCode.Focus();
        if (impactChanged is not null) await ReadImpactAsync(impactChanged);
    }

    private void ReportError(Exception error)
    {
        if (_closing) return;
        if (error is ConsoleApiException { StatusCode: 401 or 403 } expired)
        {
            SessionExpired?.Invoke(expired);
            return;
        }
        SetFeedback(error is ConsoleApiException service ? service.Message : "暂时无法连接学校服务，请重试。");
        if (error is ConsoleApiException { Code: "CLASS_MAINTENANCE_TERM_CHANGED" or "ADMIN_CLASS_NOT_FOUND" or "CLASS_MAINTENANCE_UNSUPPORTED" })
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

    private void SetFeedback(string? message)
    {
        MaintenanceFeedback.Text = message;
        MaintenanceFeedback.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (!_closing) Render();
    }

    private void Render()
    {
        var room = _baseline;
        bool active = room?.IsActive == true;
        bool canWrite = !_busy && !_blocked && !_checkingRequired && !_confirmDiscard && !ArchiveReadOnly;
        MaintenanceProgress.IsVisible = _busy;
        MaintenanceFields.IsVisible = (active || _inactiveDraft) && !_showImpact;
        MaintenanceInactiveDraftLabel.IsVisible = _inactiveDraft;
        MaintenanceName.IsReadOnly = _inactiveDraft;
        MaintenanceCode.IsReadOnly = _inactiveDraft;
        MaintenanceCodeHint.IsVisible = !_inactiveDraft;
        MaintenanceStudentSelectable.IsVisible = !_inactiveDraft;
        MaintenanceInactiveSelectable.IsVisible = _inactiveDraft;
        if (_inactiveDraft)
            MaintenanceInactiveSelectable.Text = MaintenanceStudentSelectable.IsChecked == true ? "学生自选：允许" : "学生自选：不允许";
        MaintenanceActivity.IsVisible = active && !_showImpact && !ArchiveReadOnly;
        MaintenanceFields.IsEnabled = !_busy && !_blocked && !_confirmDiscard && !ArchiveReadOnly;
        CorrectArchivedClass.IsVisible = ArchiveReadOnly && room is not null;
        MaintenanceDiscard.IsVisible = _confirmDiscard;
        MaintenanceActions.IsVisible = !_confirmDiscard;
        SaveMaintenanceButton.IsVisible = active && !_showImpact && !ArchiveReadOnly;
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

    private bool ArchiveReadOnly => _specifiedTerm?.TermStatus == "已归档" && !_archiveEditing;

    private void CorrectArchivedClicked(object? sender, RoutedEventArgs args)
    {
        _archiveEditing = true;
        Render();
        if (_baseline?.IsActive == true) MaintenanceName.Focus();
        else ReactivateClassButton.Focus();
    }

    internal bool CanClose()
    {
        if (_allowClose) return true;
        if (_busy) return false;
        if (!_inactiveDraft && (_baseline is not { IsActive: true } room || Draft().Matches(room))) return true;
        _confirmDiscard = true;
        Render();
        KeepMaintenanceDraft.Focus();
        return false;
    }

    private void KeepDraftClicked(object? sender, RoutedEventArgs args)
    {
        _confirmDiscard = false;
        Render();
        MaintenanceName.Focus();
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        _allowClose = true;
        CloseRequested?.Invoke();
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();
}
