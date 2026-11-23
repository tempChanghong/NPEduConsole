using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class OrganizationImportView
{
    private sealed record Outstanding(string SchoolId, OrganizationTerm Term, Operation Operation);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<Guid, Outstanding>> Requests = new();
    private Operation? Pending { get; set; }
    private Guid[] _shownRequests = [];

    internal Guid? RecoveryRequestId { get; init; }
    internal Func<Guid, string, bool, Task>? OpenPendingRequest { get; set; }
    internal string? ReceiptSubjectForGroup(string id)
    {
        var code = _receipt?.CourseGroups.FirstOrDefault(row => row.Id == id)?.Code;
        if (code is null) return null;
        var subject = _groups.FirstOrDefault(row => SameCode(row.Code, code))?.Subject;
        return subject is null ? null : _receipt!.Subjects.FirstOrDefault(row => SameCode(row.Code, subject))?.Id;
    }

    internal async Task<bool> HasCurrentTargetAsync(string termId, string gradeId)
    {
        if (_closed || _term?.Id != termId) return false;
        var status = _receipt?.TermStatus ?? _term.Status;
        try
        {
            var scope = await _client.GetOrganizationImportAsync(_school.Id, termId, _lifetime.Token, _archivedCorrection);
            if (_closed) return false;
            if (scope.Terms.Any(row => row.Id == termId && row.Status == status) && scope.Grades.Any(row => row.Id == gradeId)) return true;
        }
        catch (ConsoleApiException error) when (error.Code is "ORG_IMPORT_TERM_CHANGED" or "ORG_IMPORT_TARGET_INVALID") { }
        if (!_closed) Status("这份导入记录对应的学期状态或年级已变化。请从学期管理核对当前归属。");
        return false;
    }

    internal ManagedAcademicTerm? ReceiptTerm => _term is { } term && term.Id == _receipt?.TermId
        ? new(term.Id, term.Name, term.AcademicYear, term.Semester, term.Status, null, null) : null;
    internal void ObserveActiveImport() => ActiveTermChanged = true;
    internal OrganizationImportReceipt? ConfirmedReceipt => _receipt;
    internal bool ReturningWithDraft => _dirty && !_allowClose && Pending is null;
    internal void NavigationNotice(string text) => Status(text);

    internal static void EndSession(ConsoleClient client) => Requests.Remove(client);

    internal static void ForgetPending(ConsoleClient client, string schoolId)
    {
        if (!Requests.TryGetValue(client, out var requests)) return;
        foreach (var id in requests.Values.Where(row => row.SchoolId == schoolId).Select(row => row.Operation.RequestId).ToArray())
            requests.Remove(id);
    }

    private void RememberPending(Operation operation)
    {
        Requests.GetOrCreateValue(_client).Add(operation.RequestId, new(_school.Id, _term!, operation));
        Pending = operation;
    }

    private void ClearPending(Operation operation)
    {
        if (Requests.TryGetValue(_client, out var requests)) requests.Remove(operation.RequestId);
        if (Pending?.RequestId == operation.RequestId) Pending = null;
    }

    private bool HasUnresolvedTarget => _term is not null && Requests.TryGetValue(_client, out var requests) &&
        requests.Values.Any(row => row.SchoolId == _school.Id && row.Term.Id == _term.Id &&
            SameCode(row.Operation.Draft.Grade.Code, OrgGradeCode.Text ?? ""));

    private async Task<bool> RestorePendingAsync()
    {
        if (RecoveryRequestId is not { } id) return false;
        if (!Requests.TryGetValue(_client, out var requests) || !requests.TryGetValue(id, out var stored) ||
            stored.SchoolId != _school.Id || stored.Term.Id != _fixedTermId || stored.Operation.Draft.ArchivedCorrection != _archivedCorrection)
        {
            _scopeUnavailable = true;
            Status("这笔导入已结束核对。请返回原页。");
            return true;
        }

        var draft = stored.Operation.Draft;
        Pending = stored.Operation;
        _term = stored.Term;
        // A later change in term status can close the editing route. The personal receipt remains addressable.
        try { await LoadAsync(draft.TermId); }
        catch (ConsoleApiException error) when (error.Code == "ORG_IMPORT_TERM_CHANGED" || error.StatusCode is 0 or 404 or 408 or >= 500)
        {
            _scopeUnavailable = true;
        }
        if (_closed) return true;
        _scopeUnavailable |= _term?.Status != draft.TermStatus;
        SetDocument(new(draft.Grade, draft.Subjects, draft.AdministrativeClasses, draft.CourseGroups, draft.Declaration));
        await VerifyReceiptAsync(stored.Operation);
        return true;
    }

    private void UpdatePendingActions()
    {
        if (OrgPendingActions is null || _client is null || _closed) return;
        var available = RecoveryRequestId is null && Pending is null && !_scopeUnavailable &&
            Requests.TryGetValue(_client, out var requests)
            ? requests.Values.Where(row => row.SchoolId == _school.Id).ToArray() : [];
        var ids = available.Select(row => row.Operation.RequestId).ToArray();
        OrgPendingActions.IsVisible = ids.Length > 0;
        OrgPendingActions.IsEnabled = !_busy && !_showExcel && _replacement is null && !_discard;
        if (_shownRequests.SequenceEqual(ids)) return;
        _shownRequests = ids;
        OrgPendingActions.Children.Clear();
        foreach (var stored in available)
        {
            var op = stored.Operation;
            var button = new Button { Content = $"核对{stored.Term.Name} · {op.Draft.Grade.Name}（{op.Draft.Grade.Code}）导入" };
            button.Classes.Add("action");
            AutomationProperties.SetAutomationId(button, $"OrgResumePending-{op.RequestId:D}");
            ToolTip.SetTip(button, $"查询本人原请求 {op.RequestId:D}");
            button.Click += async (_, _) =>
            {
                if (_closed || _busy || OpenPendingRequest is not { } open) return;
                await open(op.RequestId, stored.Term.Id, op.Draft.ArchivedCorrection);
                if (_closed) return;
                Render();
                if (button.IsEffectivelyVisible && OrgPendingActions.Children.Contains(button)) button.Focus(NavigationMethod.Tab);
                else FocusLater(_stage switch
                {
                    WorkStage.Difference => OrgDifferences,
                    WorkStage.Result => OrgReceiptClasses,
                    _ => _fixedTermId is null ? (Control)OrgTerm : OrgGrade
                });
            };
            OrgPendingActions.Children.Add(button);
        }
    }
}
