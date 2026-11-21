using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record ClassBatchSubmission(ClassCreationContext Context, ClassCreationGrade Grade, Guid RequestId,
    IReadOnlyList<BatchClassRow> Rows, string Start, string End, string NamePrefix, string CodePrefix);

/// <summary>
/// 按年级批量开班：生成逐行可编辑的草稿，先核对，再用固定请求编号保存；回执未确认时按编号核对。
/// 只负责界面与本地校验，学期/权限/唯一性最终由 KV 判定。
/// </summary>
public sealed partial class ClassCreationWindow
{
    private readonly SubjectSetupFlow _batchSubjectSetup = new();
    private readonly ObservableCollection<BatchDraftRow> _batchRows = [];
    private bool _editingBatch;
    private bool _batchSubmitting;
    private bool _batchValidating;
    private bool _batchChecking;
    private bool _batchUncertain;
    private bool _batchUnsupported;
    private bool _batchNormalizing;
    private bool _batchRowsEdited;
    private bool _batchRegenerateConfirmed;
    private bool _batchHasRejectedRows;
    private (string Start, string End, string NamePrefix, string CodePrefix)? _batchPreviewSettings;
    private string? _batchValidatedSignature;
    private ClassCreationGrade? _batchGrade;
    private string? _batchTermId;
    private Guid? _batchRequestId;
    private string? _batchSubmittedSignature;
    private BatchClassReceipt? _batchReceipt;
    private ClassBatchSubmission? _pendingBatch;
    private (double Width, double Height, PixelPoint Position)? _singleEditorBounds;
    internal event Action<ClassBatchSubmission?>? PendingBatchChanged;
    internal int? ConfirmedBatchCount { get; private set; }

    private void SetPendingBatch(ClassBatchSubmission? pending)
    {
        _pendingBatch = pending;
        PendingBatchChanged?.Invoke(pending);
    }

    private void RestorePendingBatch(ClassBatchSubmission pending)
    {
        _editingBatch = _batchUncertain = true;
        _pendingBatch = pending;
        _batchGrade = pending.Grade;
        _batchTermId = pending.Context.TermId;
        _batchRequestId = pending.RequestId;
        _batchSubmittedSignature = BatchSignature(pending.Rows);
        ShowContext(pending.Context, pending.Grade.Id);
        BatchStartNumber.Text = pending.Start;
        BatchEndNumber.Text = pending.End;
        BatchNamePrefix.Text = pending.NamePrefix;
        BatchCodePrefix.Text = pending.CodePrefix;
        _batchPreviewSettings = CurrentBatchSettings();
        for (int i = 0; i < pending.Rows.Count; i++)
        {
            var row = pending.Rows[i];
            _batchRows.Add(new BatchDraftRow(i + 1, row.Code, row.Name, row.IsStudentSelectable, OnBatchRowEdited));
        }
        BatchRows.ItemsSource = _batchRows;
        BatchSummary.Text = $"待核对的原批次 · {pending.Rows.Count} 个班";
        ShowBatchTarget();
        SetFeedback("已恢复原批次和请求编号，请先核对结果。");
    }

    private void OpenClassBatchClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _editingBatch || _editingGrade || _editingGradeRename || _request is not null || _blocked ||
            !GradeContextReady || _context is not { TermId: not null } context || _batchUnsupported ||
            CreationGrade.SelectedItem is not ClassCreationGrade grade) return;

        _editingBatch = true;
        _batchGrade = grade;
        _batchTermId = context.TermId;
        _batchRequestId = null;
        _batchSubmittedSignature = null;
        _batchUncertain = false;
        _batchValidatedSignature = null;
        _batchReceipt = null;
        _batchRowsEdited = false;
        _batchRegenerateConfirmed = false;
        _batchHasRejectedRows = false;
        _batchRows.Clear();
        BatchStartNumber.Text = "1";
        BatchEndNumber.Text = "1";
        BatchNamePrefix.Text = grade.Name;
        BatchCodePrefix.Text = $"{grade.Code}-C";
        BatchSummary.Text = "输入起止编号和前缀生成预览，一批最多 100 个班。";
        BatchResults.ItemsSource = null;
        BatchResults.SelectedItem = null;
        BatchResultSummary.Text = null;
        BatchResultPane.IsVisible = false;
        if (!ReferenceEquals(BatchRows.ItemsSource, _batchRows)) BatchRows.ItemsSource = _batchRows;
        ShowBatchTarget();
        SetFeedback(null);
        ExpandBatchWorkspace();
        Render();
        BatchEndNumber.Focus();
        BatchEndNumber.SelectAll();
    }

    private void BatchCancelClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_editingBatch || _request is not null || _batchUncertain || _batchSubmitting ||
            _batchValidating || _batchChecking) return;
        ResetBatchState();
        Title = "新建行政班";
        MinWidth = 440;
        MinHeight = 480;
        if (_singleEditorBounds is { } bounds)
        {
            Width = bounds.Width;
            Height = bounds.Height;
            Position = bounds.Position;
        }
        _singleEditorBounds = null;
        SetFeedback(_context is { } context ? CreationContextMessage(context) : null);
        Render();
        OpenClassBatchButton.Focus();
    }

    private void BatchInputChanged(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_editingBatch || _batchNormalizing) return;
        _batchRegenerateConfirmed = false;
        _batchValidatedSignature = null;
        if (BatchSettingsChanged) SetFeedback("起止编号或前缀已变化，请重新生成预览后再保存。");
        Render();
    }

    private void BatchPreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_editingBatch || _request is not null || _blocked || _batchUnsupported || _batchUncertain) return;
        if (_batchRows.Count > 0 && _batchRowsEdited && !_batchRegenerateConfirmed)
        {
            _batchRegenerateConfirmed = true;
            SetFeedback("重新生成预览会覆盖逐行修改。确认覆盖请再次点击「生成预览」。");
            Render();
            return;
        }
        if (!TryReadBatchRange(out int start, out int end, out string? rangeError))
        {
            SetFeedback(rangeError, error: true);
            return;
        }
        string namePrefix = (BatchNamePrefix.Text ?? "").Trim();
        string codePrefix = (BatchCodePrefix.Text ?? "").Trim().ToUpperInvariant();
        if (namePrefix.Length == 0)
        {
            SetFeedback("班级名称前缀不能为空。", error: true);
            BatchNamePrefix.Focus();
            return;
        }
        if (codePrefix.Length == 0)
        {
            SetFeedback("班级代码前缀不能为空。", error: true);
            BatchCodePrefix.Focus();
            return;
        }

        _batchNormalizing = true;
        try
        {
            BatchNamePrefix.Text = namePrefix;
            BatchCodePrefix.Text = codePrefix;
        }
        finally { _batchNormalizing = false; }

        _batchRows.Clear();
        int count = end - start + 1;
        for (int index = 0; index < count; index++)
        {
            int number = start + index;
            _batchRows.Add(new BatchDraftRow(number - start + 1, $"{codePrefix}{number:D2}", $"{namePrefix}{number}班", true,
                OnBatchRowEdited));
        }
        _batchPreviewSettings = CurrentBatchSettings();
        _batchRowsEdited = false;
        _batchRegenerateConfirmed = false;
        _batchHasRejectedRows = false;
        _batchValidatedSignature = null;
        BatchResults.SelectedItem = null;
        BatchSummary.Text = $"预览 {_batchRows.Count} 个班 · {start} 至 {end} 号";
        BatchRowScroll.Offset = default;
        SetFeedback($"已生成 {_batchRows.Count} 行预览。可逐行修改名称、代码或学生可选，再点「核对」。");
        Render();
    }

    private async void BatchValidateClicked(object? sender, RoutedEventArgs args) => await ValidateBatchAsync();

    private async Task ValidateBatchAsync()
    {
        if (_closed || _client is not { } client || _context is not { } context || _batchGrade is not { } grade ||
            _request is not null || _blocked || _contextInvalid || _batchUnsupported || _batchUncertain ||
            BatchSettingsChanged || !_editingBatch) return;
        if (!TryBuildBatchRows(out var rows, out string? localError))
        {
            SetFeedback(localError, error: true);
            Render();
            return;
        }

        using var request = new CancellationTokenSource();
        _request = request;
        _batchValidating = true;
        SetFeedback("正在核对本批班级…");
        Render();
        try
        {
            var issues = await client.ValidateClassBatchAsync(context, grade.Id, rows, request.Token);
            if (_request != request) return;
            ApplyBatchIssues(issues);
            if (issues.Count == 0)
            {
                _batchValidatedSignature = BatchSignature(rows);
                SetFeedback($"核对通过：{rows.Count} 个班可创建。保存时服务器会再次检查。");
            }
            else
            {
                SetFeedback($"{FirstIssueMessage(issues)} 草稿已保留。修正后请重新核对。", error: true);
                CloseNotice = "批量开班核对未通过：草稿已保留。";
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            HandleBatchError(error);
        }
        finally
        {
            if (_request == request) _batchValidating = false;
            EndRequest(request);
        }
    }

    private async void BatchSubmitClicked(object? sender, RoutedEventArgs args) => await SubmitBatchAsync();

    private async Task SubmitBatchAsync()
    {
        if (_closed || _client is not { } client || _context is not { } context || _batchGrade is not { } grade ||
            _request is not null || _blocked || _contextInvalid || _batchUnsupported || _batchHasRejectedRows || BatchSettingsChanged || !_editingBatch) return;
        if (!TryBuildBatchRows(out var rows, out string? localError))
        {
            SetFeedback(localError, error: true);
            Render();
            return;
        }

        string signature = BatchSignature(rows);
        if (_batchUncertain)
        {
            SetFeedback("上一次保存的结果尚未确认。请先「核对结果」；确认服务器未记录后，可用同一编号重试本批。", error: true);
            Render();
            return;
        }
        if (_batchReceipt is not null)
        {
            SetFeedback("本批已创建。可在下方服务器回执中选择班级打开名单；如需再建一批，请改动输入后重新预览并保存。");
            Render();
            return;
        }

        if (_batchRequestId is null || _batchSubmittedSignature != signature)
        {
            _batchRequestId = Guid.NewGuid();
            _batchSubmittedSignature = signature;
        }
        Guid requestId = _batchRequestId.Value;
        SetPendingBatch(new(context, grade, requestId, Array.AsReadOnly(rows.ToArray()),
            BatchStartNumber.Text ?? "", BatchEndNumber.Text ?? "", BatchNamePrefix.Text ?? "", BatchCodePrefix.Text ?? ""));

        using var request = new CancellationTokenSource();
        _request = request;
        _batchSubmitting = true;
        SetFeedback("正在提交批量开班请求…");
        Render();
        try
        {
            var receipt = await client.CreateClassBatchAsync(context, grade.Id, rows, requestId, request.Token);
            if (_request != request) return;
            ApplyBatchReceipt(receipt);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.BatchIssues is { Count: > 0 } issues)
            {
                SetPendingBatch(null);
                ApplyBatchIssues(issues);
                SetFeedback($"{FirstIssueMessage(issues)} 本批未建班，草稿已保留。修正后请重新核对。", error: true);
                CloseNotice = "批量开班未保存：服务器列出需修正的行，草稿已保留。";
            }
            else if (error.StatusCode is 0 or 408 or >= 500)
            {
                _batchUncertain = true;
                SetFeedback("保存结果未确认。本批草稿与请求编号已保留；请先「核对结果」，确认服务器未记录后可明确重试同一编号。", error: true);
                CloseNotice = "批量开班结果待核对：保存结果未确认，请按请求编号核对。";
            }
            else
            {
                if (error.Code != "CLASS_BATCH_REQUEST_CONFLICT") SetPendingBatch(null);
                HandleBatchError(error);
            }
        }
        finally
        {
            if (_request == request) _batchSubmitting = false;
            EndRequest(request);
        }
    }

    private async void BatchVerifyReceiptClicked(object? sender, RoutedEventArgs args) => await VerifyBatchReceiptAsync();

    private async Task VerifyBatchReceiptAsync()
    {
        if (_closed || _client is not { } client || _pendingBatch is not { } pending ||
            _request is not null || _blocked || !_editingBatch) return;

        using var request = new CancellationTokenSource();
        _request = request;
        _batchChecking = true;
        SetFeedback("正在按请求编号查询服务器回执…");
        Render();
        try
        {
            var receipt = await client.GetClassBatchResultAsync(pending.Context, pending.Grade.Id, pending.RequestId, request.Token);
            if (_request != request) return;
            if (receipt is null)
            {
                _batchUncertain = false;
                SetPendingBatch(null);
                SetFeedback("服务器未记录该请求编号。本批已解锁：输入未变时可再次「保存开班」，服务器会用同一编号核对；如需修改输入，请先重新生成预览。");
                CloseNotice = null;
            }
            else ApplyBatchReceipt(receipt);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode is 0 or 408 or >= 500)
            {
                _batchUncertain = true;
                SetFeedback("核对未能完成。本批编号与草稿仍保留，可稍后再点「核对结果」。", error: true);
            }
            else HandleBatchError(error);
        }
        finally
        {
            if (_request == request) _batchChecking = false;
            EndRequest(request);
        }
    }

    private void BatchResultSelectionChanged(object? sender, SelectionChangedEventArgs args) => Render();

    private async void BatchOpenSubjectSetupClicked(object? sender, RoutedEventArgs args)
    {
        if (!BatchOpenSubjectSetupButton.IsEnabled || _batchReceipt is not { } receipt || _client is not { } client ||
            _school is not { } school || BatchResults.SelectedItem is not BatchResultItem item) return;
        await _batchSubjectSetup.OpenAsync(this, client, school, receipt.TermId, item.Class.Id,
            receipt.Classes.Select(room => room.Id).ToArray(), error => SessionExpired?.Invoke(error));
        if (!_closed) BatchOpenSubjectSetupButton.Focus();
    }

    private void BatchOpenRosterClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _batchReceipt is not { } receipt || BatchResults.SelectedItem is not BatchResultItem item) return;
        if (receipt.Classes.All(created => created.Id != item.Class.Id)) return;
        if (_contextInvalid || _context is not { } context || receipt.TermId != context.TermId)
        {
            SetFeedback("该批属于其他学期，当前界面的班级列表只含当前学期，无法直接打开。请切到对应学期查看名单。", error: true);
            return;
        }
        ConfirmedClass = new CreatedAdministrativeClass(item.Class.Id, receipt.TermId, item.Class.Name);
        _allowClose = true;
        Close(ConfirmedClass);
    }

    private void ApplyBatchReceipt(BatchClassReceipt receipt)
    {
        if (_pendingBatch is not { } pending || receipt.TermId != pending.Context.TermId ||
            receipt.GradeId != pending.Grade.Id || receipt.RequestId != pending.RequestId ||
            !pending.Rows.SequenceEqual(receipt.Classes.Select(item => new BatchClassRow(item.Code, item.Name, item.IsStudentSelectable))))
        {
            _batchReceipt = null;
            _batchUncertain = true;
            BatchResults.ItemsSource = null;
            BatchResultSummary.Text = null;
            SetFeedback("服务器回执与本批的学期、年级或请求编号不一致，不能据此认定本批已创建。请按请求编号核对。", error: true);
            CloseNotice = "批量开班结果待核对：回执与请求编号不一致。";
            Render();
            return;
        }

        _batchReceipt = receipt;
        _batchUncertain = false;
        SetPendingBatch(null);
        _batchValidatedSignature = null;
        BatchResults.ItemsSource = receipt.Classes.Select(created => new BatchResultItem(created)).ToArray();
        BatchResults.SelectedItem = null;
        bool sameTerm = !_contextInvalid && _context is { } context && receipt.TermId == context.TermId;
        ConfirmedBatchCount = sameTerm ? receipt.Classes.Count : null;
        BatchResultSummary.Text = $"共 {receipt.Classes.Count} 个班 · 请求编号 {receipt.RequestId:D}" +
            (sameTerm ? "" : " · 该批不属于当前学期，不能在当前学期打开名单。");
        SetFeedback(sameTerm
            ? $"已创建 {receipt.Classes.Count} 个班。选择其中一班即可打开学生名单。"
            : $"服务器确认原学期已创建 {receipt.Classes.Count} 个班。");
        CloseNotice = sameTerm ? null : $"服务器确认原学期已创建 {receipt.Classes.Count} 个班。";
        Render();
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !ReferenceEquals(_batchReceipt, receipt)) return;
            BatchResultPane.BringIntoView();
            BatchResults.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyBatchIssues(IReadOnlyList<BatchClassIssue> issues)
    {
        _batchHasRejectedRows = issues.Count > 0;
        foreach (var row in _batchRows) row.ClearError();
        int applied = 0;
        foreach (var issue in issues)
        {
            if (issue.Row < 1 || issue.Row > _batchRows.Count) continue;
            var row = _batchRows[issue.Row - 1];
            string field = issue.Field switch
            {
                "code" => "班级代码",
                "name" => "班级名称",
                "isStudentSelectable" => "学生可选",
                _ => "本行"
            };
            string message = string.IsNullOrWhiteSpace(issue.Message) ? issue.Code : issue.Message;
            string issueValue = issue.Field == "code" ? row.Code : issue.Field == "name" ? row.Name : "";
            string detail = string.IsNullOrWhiteSpace(issueValue) || message.Contains(issueValue, StringComparison.Ordinal)
                ? $"{field}：{message}" : $"{field}「{issueValue}」：{message}";
            row.SetError(row.HasError ? $"{row.Error} {detail}" : detail, issue.Field);
            applied++;
        }
        _batchValidatedSignature = null;
        FocusFirstBatchIssue();
        if (applied < issues.Count)
            SetFeedback($"服务器指出 {issues.Count} 处问题，其中 {applied} 处已定位到行；其余请按提示整体检查。", error: true);
    }

    private void ExpandBatchWorkspace()
    {
        if (!_editingBatch) return;
        _singleEditorBounds ??= (Width, Height, Position);
        Title = "按年级批量开班";
        MinWidth = 700;
        BatchWorkspaceWindow.FitSchoolWindow(this);
    }

    private void BatchLocateIssueClicked(object? sender, RoutedEventArgs args) => FocusFirstBatchIssue();

    private void FocusFirstBatchIssue()
    {
        if (_batchRows.FirstOrDefault(candidate => candidate.HasError) is not { } row) return;
        string id = row.ErrorField switch
        {
            "name" => row.NameId,
            "isStudentSelectable" => row.StudentSelectableId,
            _ => row.CodeId
        };
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_editingBatch || !_batchRows.Contains(row) || !row.HasError) return;
            var field = BatchRows.GetVisualDescendants().OfType<Control>().FirstOrDefault(element =>
                ReferenceEquals(element.DataContext, row) && AutomationProperties.GetAutomationId(element) == id);
            if (field is null || !field.IsEffectivelyEnabled) return;
            field.Focus();
            field.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("batch-row"))?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private static string FirstIssueMessage(IReadOnlyList<BatchClassIssue> issues)
    {
        var issue = issues[0];
        return issues.Count == 1 ? $"第 {issue.Row} 行：{issue.Message}"
            : $"第 {issue.Row} 行：{issue.Message} 共 {issues.Count} 处需修正。";
    }

    private bool TryBuildBatchRows(out IReadOnlyList<BatchClassRow> rows, out string? error)
    {
        rows = [];
        error = null;
        if (_batchRows.Count == 0)
        {
            error = "请先生成预览。";
            return false;
        }
        if (_batchRows.Count > 100)
        {
            error = "一次最多 100 个班。";
            return false;
        }

        _batchNormalizing = true;
        try
        {
            foreach (var row in _batchRows)
            {
                row.SetNormalized((row.Code ?? "").Trim().ToUpperInvariant(), (row.Name ?? "").Trim());
                row.ClearError();
            }
        }
        finally { _batchNormalizing = false; }

        int count = _batchRows.Count;
        var codes = new string[count];
        var names = new string[count];
        var messages = new List<string>?[count];
        var fields = new string?[count];
        for (int index = 0; index < count; index++)
        {
            codes[index] = _batchRows[index].Code;
            names[index] = _batchRows[index].Name;
            if (codes[index].Length == 0) Add(index, "code", "班级代码：不能为空。");
            else if (!ValidCode(codes[index]))
                Add(index, "code", "班级代码：需为 2–64 位，以英文字母或数字开头，其余可用英文字母、数字和 . _ -。");
            if (names[index].Length is < 1 or > 191) Add(index, "name", "班级名称：需为 1 至 191 个字符。");
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            if (codes[index].Length == 0 || !ValidCode(codes[index])) continue;
            if (!seen.TryGetValue(codes[index], out int first))
            {
                seen[codes[index]] = index;
                continue;
            }
            Add(first, "code", $"班级代码：与第 {_batchRows[index].Position} 行重复。");
            Add(index, "code", $"班级代码：与第 {_batchRows[first].Position} 行重复。");
        }

        for (int index = 0; index < count; index++)
        {
            if (messages[index] is not { Count: > 0 } parts) continue;
            _batchRows[index].SetError(string.Join(" ", parts), fields[index]);
            error ??= $"第 {_batchRows[index].Position} 行需修正。";
        }
        if (error is not null)
        {
            _batchHasRejectedRows = true;
            FocusFirstBatchIssue();
            return false;
        }

        var list = new List<BatchClassRow>(count);
        for (int index = 0; index < count; index++)
            list.Add(new BatchClassRow(codes[index], names[index], _batchRows[index].IsStudentSelectable));
        rows = list;
        return true;

        void Add(int index, string field, string message)
        {
            fields[index] ??= field;
            (messages[index] ??= []).Add(message);
        }
    }

    private void OnBatchRowEdited()
    {
        if (_closed || _batchNormalizing) return;
        _batchRowsEdited = true;
        _batchRegenerateConfirmed = false;
        _batchValidatedSignature = null;
        Render();
    }

    private void HandleBatchError(ConsoleApiException error)
    {
        if (error.StatusCode is 401 or 403)
        {
            LoseAccess(error);
            return;
        }
        switch (error.Code)
        {
            case "CLASS_BATCH_UNSUPPORTED":
                _batchUnsupported = true;
                SetFeedback($"{error.Message} 单个建班仍可使用。", error: true);
                CloseNotice = "当前 KV 不支持批量开班。";
                break;
            case "CLASS_BATCH_TERM_CHANGED":
                _contextInvalid = true;
                SetFeedback($"{error.Message} 本批草稿已保留；请刷新学期与年级并重新选择后再保存。", error: true);
                CloseNotice = "批量开班未完成：当前学期已改变，草稿已保留。";
                break;
            case "CLASS_BATCH_REQUEST_CONFLICT":
                _batchUncertain = true;
                SetFeedback($"{error.Message} 请先「核对结果」，确认后再决定是否重试。", error: true);
                CloseNotice = "批量开班结果待核对：请求编号与已有回执不一致。";
                break;
            case "GRADE_INVALID":
                _contextInvalid = true;
                SetFeedback($"{error.Message} 本批草稿已保留；请刷新学期与年级后重新选择。", error: true);
                CloseNotice = "批量开班未完成：年级已变化，草稿已保留。";
                break;
            default:
                SetFeedback(error.Message, error: true);
                break;
        }
        Render();
    }

    private void ApplyBatchContextRefresh(ClassCreationContext context)
    {
        if (!_editingBatch) return;
        if (_batchTermId is { } originalTerm && context.TermId != originalTerm)
        {
            _contextInvalid = true;
            SetFeedback(_batchUncertain
                ? "当前启用学期已改变，仍可用原请求编号核对原学期的结果。"
                : "当前启用学期已改变。请返回建班，重新选择年级后再开班。", error: true);
            return;
        }
        if (_batchGrade is { } grade && context.Grades.FirstOrDefault(candidate => candidate.Id == grade.Id) is { } current)
        {
            _batchGrade = current;
            _contextInvalid = false;
            ShowBatchTarget();
            SetFeedback(_batchUncertain ? "已恢复原批次和请求编号，请先核对结果。"
                : $"年级信息已刷新，本批草稿已保留（{_batchRows.Count} 个班）。");
        }
        else
        {
            _contextInvalid = true;
            SetFeedback("当前学期中已找不到原年级。本批草稿已保留，但暂不能提交；请重新进入批量开班并选择年级。", error: true);
            CloseNotice = "批量开班未提交：年级已变化，草稿已保留。";
        }
    }

    private void RenderBatch(bool busy)
    {
        if (_closed || BatchFields is null) return;
        BatchFields.IsVisible = _editingBatch;
        StandardCreationBody.IsVisible = !_editingBatch;
        if (!_editingBatch)
        {
            OpenClassBatchButton.IsEnabled = !busy && GradeContextReady && !_editingGrade && !_editingGradeRename &&
                !_blocked && !_batchUnsupported && _pending is null && _pendingGrade is null &&
                CreationGrade.SelectedItem is ClassCreationGrade;
            return;
        }

        bool settled = !_batchUncertain && !_batchUnsupported;
        bool canEdit = !busy && !_blocked && settled && !_contextInvalid && _batchReceipt is null;
        BatchDraftPane.IsVisible = _batchReceipt is null;
        BatchRowScroll.IsVisible = _batchRows.Count > 0;
        BatchEmpty.IsVisible = _batchRows.Count == 0;
        BatchLocateIssueButton.IsVisible = _batchRows.Any(row => row.HasError);
        BatchLocateIssueButton.IsEnabled = canEdit;
        BatchCloseButton.IsEnabled = !_batchSubmitting && !_batchValidating && !_batchChecking;
        BatchCloseButton.Content = _batchUncertain ? "关闭，稍后确认" : "关闭";
        ReloadBatchContextButton.IsVisible = _contextInvalid && !_batchUncertain && _batchReceipt is null;
        ReloadBatchContextButton.IsEnabled = ReloadCreationContext.IsEnabled;
        BatchPreviewButton.IsVisible = BatchValidateButton.IsVisible = BatchSubmitButton.IsVisible =
            _batchReceipt is null && !_batchUncertain;
        BatchOpenRosterButton.IsVisible = BatchOpenSubjectSetupButton.IsVisible = _batchReceipt is not null;
        BatchStartNumber.IsEnabled = BatchEndNumber.IsEnabled = BatchNamePrefix.IsEnabled = BatchCodePrefix.IsEnabled = canEdit;
        BatchRows.IsEnabled = canEdit;
        BatchPreviewButton.IsEnabled = canEdit;
        BatchValidateButton.Content = _batchValidating ? "正在核对…" : _batchValidatedSignature is null ? "核对" : "重新核对";
        BatchValidateButton.IsEnabled = canEdit && !BatchSettingsChanged && _batchRows.Count > 0;
        BatchSubmitButton.Content = _batchSubmitting ? "正在保存…" : _batchUncertain ? "请先核对结果" : "保存开班";
        BatchSubmitButton.IsEnabled = canEdit && !_batchHasRejectedRows && !BatchSettingsChanged && _batchRows.Count > 0;
        BatchSubmitButton.Classes.Set("accent", BatchSubmitButton.IsEnabled);
        BatchVerifyReceiptButton.IsVisible = _batchUncertain && _batchRequestId is not null;
        BatchVerifyReceiptButton.Content = _batchChecking ? "正在核对…" : "核对结果";
        BatchVerifyReceiptButton.IsEnabled = !busy && !_blocked;
        BatchCancelButton.IsEnabled = !busy && !_batchUncertain;
        BatchResultPane.IsVisible = _batchReceipt is not null;
        BatchOpenRosterButton.IsEnabled = !busy && BatchResults.SelectedItem is BatchResultItem item &&
            _batchReceipt is { } receipt && receipt.Classes.Any(created => created.Id == item.Class.Id) &&
            !_contextInvalid && _context is { } context && receipt.TermId == context.TermId;
        BatchOpenSubjectSetupButton.IsEnabled = BatchOpenRosterButton.IsEnabled;
        OpenClassBatchButton.IsEnabled = false;
    }

    private void ShowBatchTarget() =>
        BatchTarget.Text = _batchGrade is { } grade
            ? $"{_school?.Name}  /  {_context?.TermName ?? "当前学期"}  /  {grade.Name}（{grade.Code}）"
            : null;

    private void ResetBatchState()
    {
        _editingBatch = false;
        _batchSubmitting = false;
        _batchValidating = false;
        _batchChecking = false;
        _batchUncertain = false;
        _batchUnsupported = false;
        _batchNormalizing = false;
        _batchRowsEdited = false;
        _batchRegenerateConfirmed = false;
        _batchHasRejectedRows = false;
        _batchPreviewSettings = null;
        _batchValidatedSignature = null;
        _batchGrade = null;
        _batchTermId = null;
        _batchRequestId = null;
        _batchSubmittedSignature = null;
        _batchReceipt = null;
        _pendingBatch = null;
        _batchRows.Clear();
        if (BatchFields is null) return;
        BatchTarget.Text = null;
        BatchSummary.Text = "输入起止编号和前缀生成预览，一批最多 100 个班。";
        BatchResults.ItemsSource = null;
        BatchResults.SelectedItem = null;
        BatchResultSummary.Text = null;
        BatchResultPane.IsVisible = false;
    }

    private bool TryReadBatchRange(out int start, out int end, out string? error)
    {
        start = end = 0;
        error = null;
        if (!int.TryParse((BatchStartNumber.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
            !int.TryParse((BatchEndNumber.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
        {
            error = "起始与结束编号需为数字。";
            return false;
        }
        if (start < 1)
        {
            error = "起始编号至少为 1。";
            return false;
        }
        if (end < start)
        {
            error = "结束编号不能小于起始编号。";
            return false;
        }
        if (end - start >= 100)
        {
            error = "一次最多创建 100 个班。";
            return false;
        }
        return true;
    }

    private static string BatchSignature(IReadOnlyList<BatchClassRow> rows) => JsonSerializer.Serialize(rows);

    private bool BatchSettingsChanged => _batchPreviewSettings is { } settings && settings != CurrentBatchSettings();

    private (string Start, string End, string NamePrefix, string CodePrefix) CurrentBatchSettings() =>
        ((BatchStartNumber.Text ?? "").Trim(), (BatchEndNumber.Text ?? "").Trim(),
            (BatchNamePrefix.Text ?? "").Trim(), (BatchCodePrefix.Text ?? "").Trim().ToUpperInvariant());
}

internal sealed class BatchDraftRow(int position, string code, string name, bool isStudentSelectable, Action edited)
    : INotifyPropertyChanged
{
    private string _code = code;
    private string _name = name;
    private bool _isStudentSelectable = isStudentSelectable;
    private string? _error;

    public string Position { get; } = position.ToString(CultureInfo.InvariantCulture);
    public string CodeId => "BatchClassCode-" + Position;
    public string NameId => "BatchClassName-" + Position;
    public string StudentSelectableId => "BatchClassStudentChoice-" + Position;
    public string? ErrorField { get; private set; }

    public string Code
    {
        get => _code;
        set
        {
            value ??= "";
            if (_code == value) return;
            _code = value;
            Notify(nameof(Code));
            edited();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            value ??= "";
            if (_name == value) return;
            _name = value;
            Notify(nameof(Name));
            edited();
        }
    }

    public bool IsStudentSelectable
    {
        get => _isStudentSelectable;
        set
        {
            if (_isStudentSelectable == value) return;
            _isStudentSelectable = value;
            Notify(nameof(IsStudentSelectable));
            edited();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value) return;
            _error = value;
            Notify(nameof(Error));
            Notify(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetNormalized(string code, string name)
    {
        if (_code != code)
        {
            _code = code;
            Notify(nameof(Code));
        }
        if (_name != name)
        {
            _name = name;
            Notify(nameof(Name));
        }
    }

    public void SetError(string error, string? field)
    {
        if (!HasError) ErrorField = field;
        Error = error;
    }

    public void ClearError()
    {
        ErrorField = null;
        Error = null;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class BatchResultItem(BatchCreatedClass createdClass)
{
    public BatchCreatedClass Class { get; } = createdClass;
    public string StudentChoiceText => Class.IsStudentSelectable ? "允许" : "不允许";
}
