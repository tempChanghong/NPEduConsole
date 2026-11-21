using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record GradeCreationSubmission(ClassCreationContext Context, string Code, string Name, Guid RequestId);

public sealed partial class ClassCreationWindow
{
    private GradeCreationSubmission? _pendingGrade;
    private bool _editingGrade, _gradeRetryReady, _gradeUnsupported;

    internal event Action<GradeCreationSubmission?>? PendingGradeChanged;

    private static string? CreationContextMessage(ClassCreationContext context) => context.TermId is null
        ? "该学校暂无启用学期，启用后可创建年级或行政班。"
        : context.Grades.Count == 0 ? "当前学期暂无年级。请先新建年级。" : null;

    private bool GradeContextReady => _context?.TermId is not null && !_contextInvalid && !_blocked;

    private void RestorePendingGrade(GradeCreationSubmission submission)
    {
        _pendingGrade = submission;
        _editingGrade = true;
        ShowContext(submission.Context, null);
        NewGradeName.Text = submission.Name;
        NewGradeCode.Text = submission.Code;
        ShowGradeUnconfirmed();
    }

    private void OpenGradeCreationClicked(object? sender, RoutedEventArgs args)
    {
        if (!GradeContextReady || _gradeUnsupported || _request is not null || _pending is not null) return;
        _editingGrade = true;
        SetFeedback(null);
        Render();
        Dispatcher.UIThread.Post(() => { if (_editingGrade && !_closed) NewGradeName.Focus(); }, DispatcherPriority.Loaded);
    }

    private void BackFromGradeCreationClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _pendingGrade is not null || _blocked) return;
        _editingGrade = false;
        NewGradeName.Clear();
        NewGradeCode.Clear();
        SetFeedback(_contextInvalid ? "请刷新学期与年级后继续。"
            : _context is { } context ? CreationContextMessage(context) : null);
        Render();
    }

    private async void SubmitGradeClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || !_editingGrade || !GradeContextReady || _gradeUnsupported || _request is not null) return;
        var submission = _pendingGrade;
        bool create = submission is null || _gradeRetryReady;
        if (submission is null)
        {
            string code = (NewGradeCode.Text ?? "").Trim().ToUpperInvariant();
            string name = (NewGradeName.Text ?? "").Trim();
            if (name.Length is < 1 or > 191)
            {
                SetFeedback("年级名称需为 1 至 191 个字符。", error: true);
                NewGradeName.Focus();
                return;
            }
            if (!ValidCode(code))
            {
                SetFeedback("年级代码需为 2–64 位，以英文字母或数字开头，其余可用英文字母、数字和 . _ -。", error: true);
                NewGradeCode.Focus();
                return;
            }
            NewGradeCode.Text = code;
            submission = new(_context!, code, name, Guid.NewGuid());
        }

        using var request = new CancellationTokenSource();
        _request = request;
        _submitting = create;
        SetPendingGrade(submission);
        SetFeedback(create ? "正在提交创建年级请求…" : "正在核对原创建请求…");
        Render();
        bool confirmed = false;
        try
        {
            var grade = create
                ? await client.CreateCurrentGradeAsync(submission.Context, submission.Code, submission.Name, submission.RequestId, request.Token)
                : await client.GetCurrentGradeCreationResultAsync(submission.Context, submission.Code, submission.Name, submission.RequestId, request.Token);
            if (_request != request) return;
            if (grade is null)
            {
                _gradeRetryReady = true;
                SetFeedback("学校暂无本次请求的回执。可重试原请求，学校会核对同一请求编号。");
                return;
            }

            confirmed = true;
            SetFeedback("学校已确认创建，正在刷新当前学期年级…");
            var directory = await client.GetClassCreationContextAsync(submission.Context.SchoolId, request.Token);
            if (_request != request) return;
            SetPendingGrade(null);
            _editingGrade = _contextInvalid = false;
            NewGradeName.Clear();
            NewGradeCode.Clear();
            bool exactGrade = directory.TermId == submission.Context.TermId && directory.Grades.Any(candidate =>
                candidate.Id == grade.Id && candidate.Code == grade.Code && candidate.Name == grade.Name);
            ShowContext(directory, exactGrade ? grade.Id : null);
            CloseNotice = exactGrade ? $"已创建年级「{grade.Name}」，可继续新建行政班。"
                : "年级已创建，但当前学期或原年级记录已变化，请核对年级列表。";
            SetFeedback(exactGrade ? $"已创建并选中「{grade.Name}」，可继续填写班级。" : CloseNotice, error: !exactGrade);
            if (exactGrade) Dispatcher.UIThread.Post(() => { if (!_closed && !_editingGrade) CreationName.Focus(); }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (confirmed && error.StatusCode is 0 or 408 or >= 500)
                SetFeedback("年级已创建，年级列表暂未刷新。请核对同一请求后继续。", error: true);
            else HandleGradeError(error);
        }
        finally { EndRequest(request); }
    }

    private void HandleGradeError(ConsoleApiException error)
    {
        if (error.StatusCode is 0 or 408 or >= 500)
        {
            ShowGradeUnconfirmed();
            return;
        }
        if (error.StatusCode is 401 or 403)
        {
            LoseAccess(error);
            return;
        }

        SetPendingGrade(null);
        if (error.Code is "GRADE_CREATION_TERM_CHANGED" or "GRADE_CREATION_TARGET_CHANGED" or "GRADE_CREATION_REQUEST_CONFLICT")
        {
            _contextInvalid = true;
            CreationGrade.SelectedItem = null;
            if (error.Code == "GRADE_CREATION_TARGET_CHANGED") CloseNotice = error.Message;
        }
        if (error.Code == "GRADE_CREATION_UNSUPPORTED") _gradeUnsupported = true;
        SetFeedback(error.Message, error: true);
        if (error.Code == "GRADE_CODE_EXISTS") Dispatcher.UIThread.Post(() => { if (!_closed && _editingGrade) NewGradeCode.Focus(); }, DispatcherPriority.Loaded);
    }

    private void SetPendingGrade(GradeCreationSubmission? submission)
    {
        _pendingGrade = submission;
        _gradeRetryReady = false;
        PendingGradeChanged?.Invoke(submission);
    }

    private void ShowGradeUnconfirmed() => SetFeedback("创建结果待核对。先查询本次请求，再决定是否重试。", error: true);

    private void GradeFieldsChanged(object? sender, RoutedEventArgs args) => Render();

    private void RenderGradeCreation(bool busy)
    {
        OpenGradeCreationButton.IsVisible = !_editingGrade;
        OpenGradeCreationButton.IsEnabled = !busy && GradeContextReady && !_gradeUnsupported && _pending is null &&
            !_editingGradeRename;
        GradeCreationFields.IsVisible = _editingGrade;
        NewGradeName.IsEnabled = NewGradeCode.IsEnabled = !busy && GradeContextReady && !_gradeUnsupported && _pendingGrade is null;
        SubmitGradeCreationButton.Content = _pendingGrade is null ? "创建年级" : _gradeRetryReady ? "重试原请求" : "核对结果";
        SubmitGradeCreationButton.IsEnabled = !busy && GradeContextReady && !_gradeUnsupported && (_pendingGrade is not null ||
            !string.IsNullOrWhiteSpace(NewGradeName.Text) && !string.IsNullOrWhiteSpace(NewGradeCode.Text));
        BackFromGradeCreationButton.IsEnabled = !busy && _pendingGrade is null && !_blocked;
    }
}
