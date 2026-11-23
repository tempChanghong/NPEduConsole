using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record ClassCreationSubmission(ClassCreationContext Context, ClassCreationGrade Grade, string Code, string Name,
    bool IsStudentSelectable, Guid RequestId);

internal sealed record CreatedAdministrativeClass(string Id, string TermId, string Name);

public sealed partial class ClassCreationWindow : Window
{
    private ConsoleClient? _client;
    private School? _school;
    private ClassCreationContext? _context;
    private ClassCreationSubmission? _pending;
    private CancellationTokenSource? _request;
    private string? _rejectedCode;
    private string? _rejectedName;
    private bool _submitting;
    private bool _contextInvalid;
    private bool _blocked;
    private bool _closed;
    private bool _allowClose;
    private string? _initialGradeTermId;
    private readonly string? _requiredActiveTermId;
    private ClassCreationContext? _specifiedTerm;
    private bool _canRetrySpecifiedClass;

    internal event Action<ClassCreationSubmission?>? PendingChanged;
    internal event Action<ConsoleApiException>? SessionExpired;
    internal CreatedAdministrativeClass? ConfirmedClass { get; private set; }
    internal string? CloseNotice { get; private set; }

    public ClassCreationWindow()
    {
        InitializeComponent();
        CreationGrade.ItemTemplate = new FuncDataTemplate<ClassCreationGrade>((grade, _) =>
            new TextBlock { Text = grade is null ? null : $"{grade.Name}（{grade.Code}）" });
        Closing += (_, args) =>
        {
            if (!_allowClose && (_submitting || _renameSubmitting || _renameChecking ||
                                 _batchSubmitting || _batchValidating || _batchChecking))
            {
                args.Cancel = true;
                SetFeedback("正在提交请求，请等待学校返回结果。");
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            var request = _request;
            _request = null;
            request?.Cancel();
            _client = null;
            _school = null;
            _pending = null;
            _pendingGrade = null;
            _context = null;
            ResetRenameState();
            ResetBatchState();
            ClearFields();
        };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
    }

    internal ClassCreationWindow(ConsoleClient client, School school, ClassCreationSubmission? pending, GradeCreationSubmission? pendingGrade,
        ClassBatchSubmission? pendingBatch, string? initialGradeTermId = null, ClassCreationContext? specifiedTerm = null,
        string? preferredGradeId = null, string? requiredActiveTermId = null) : this()
    {
        _client = client;
        _school = school;
        _specifiedTerm = specifiedTerm;
        _requiredActiveTermId = requiredActiveTermId;
        _pending = pending;
        CreationSchool.Text = school.Name;
        if (pendingBatch is not null)
        {
            RestorePendingBatch(pendingBatch);
            Opened += async (_, _) =>
            {
                ExpandBatchWorkspace();
                await LoadContextAsync();
                if (!_closed && BatchVerifyReceiptButton.IsEffectivelyVisible) BatchVerifyReceiptButton.Focus();
            };
        }
        else if (pendingGrade is not null) RestorePendingGrade(pendingGrade);
        else if (pending is null && specifiedTerm is not null)
        {
            ShowContext(specifiedTerm, preferredGradeId ?? (specifiedTerm.Grades.Count == 1 ? specifiedTerm.Grades[0].Id : null));
            SetFeedback(CreationContextMessage(specifiedTerm));
            _initialGradeTermId = initialGradeTermId;
            if (initialGradeTermId is not null) Opened += (_, _) => OpenGradeCreation();
        }
        else if (pending is null)
        {
            _initialGradeTermId = initialGradeTermId;
            Opened += (_, _) => _ = LoadContextAsync();
        }
        else
        {
            ShowContext(pending.Context, pending.Grade.Id);
            CreationName.Text = pending.Name;
            CreationCode.Text = pending.Code;
            CreationStudentSelectable.IsChecked = pending.IsStudentSelectable;
            ShowUnconfirmed();
        }
        Render();
    }

    internal void CloseForScopeChange()
    {
        _allowClose = true;
        Close();
    }

    private async Task LoadContextAsync()
    {
        if (_client is not { } client || _school is not { } school || _request is not null || _pending is not null || _pendingGrade is not null || _blocked) return;
        using var request = new CancellationTokenSource();
        _request = request;
        string? selectedGrade = (CreationGrade.SelectedItem as ClassCreationGrade)?.Id;
        bool firstDirectory = _context is null;
        string? previousTerm = _context?.TermId;
        _contextInvalid = true;
        SetFeedback("正在读取学校当前学期和年级…");
        Render();
        try
        {
            var context = await ReadCreationContextAsync(client, school.Id, request.Token);
            if (_request != request) return;
            _contextInvalid = false;
            _gradeRenameVersionStale = false;
            string? gradeId = firstDirectory && context.Grades.Count == 1 ? context.Grades[0].Id
                : context.TermId == previousTerm ? selectedGrade : null;
            ShowContext(context, gradeId);
            if (_editingGradeRename) ApplyRenameContextRefresh(context);
            else if (_editingBatch) ApplyBatchContextRefresh(context);
            else SetFeedback(_editingGrade && previousTerm != context.TermId && context.TermId is not null
                ? "已切换学期，请核对新学期后再提交。" : CreationContextMessage(context));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode is 401 or 403) LoseAccess(error);
            else SetFeedback(error.Message, error: true);
        }
        finally { EndRequest(request); }
        if (_initialGradeTermId is { } targetTerm && _request is null && _context is { } current && !_closed)
        {
            _initialGradeTermId = null;
            if (current.TermId == targetTerm && (_requiredActiveTermId is null || current.Grades.Count == 0))
                OpenGradeCreation();
        }
    }

    private async Task<ClassCreationContext> ReadCreationContextAsync(ConsoleClient client, string schoolId, CancellationToken ct)
    {
        var context = _specifiedTerm?.TermId is { } termId
            ? (await client.GetTermClassStructureAsync(schoolId, termId, ct)).Creation
            : await client.GetClassCreationContextAsync(schoolId, ct);
        if (_requiredActiveTermId is { } required && context.TermId != required)
            throw new ConsoleApiException(409, "已切换启用学期。请关闭此页，从当前学期的班级入口办理。");
        return context;
    }

    private void ShowContext(ClassCreationContext context, string? gradeId)
    {
        _context = context;
        CreationTerm.Text = context.IsSpecifiedTerm
            ? $"办理学期 {context.TermName} · {context.TermStatus} ／ 全校启用 {context.ActiveTermName ?? "暂无"}"
            : context.TermName ?? "暂无生效学期";
        CreationGrade.ItemsSource = context.Grades;
        CreationGrade.SelectedItem = context.Grades.FirstOrDefault(grade => grade.Id == gradeId);
    }

    private async void SubmitClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _request is not null || _blocked || _contextInvalid || _editingGrade ||
            _editingGradeRename || _editingBatch) return;
        var submission = _pending;
        bool readReceipt = submission?.Context.IsSpecifiedTerm == true && !_canRetrySpecifiedClass;
        if (submission is null)
        {
            if (_context is not { TermId: not null } context || CreationGrade.SelectedItem is not ClassCreationGrade grade) return;
            string code = NormalizedCode();
            string name = (CreationName.Text ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Length > 191)
            {
                SetFeedback("班级名称最多 191 个字符。", error: true);
                _rejectedName = name;
                CreationName.Focus();
                return;
            }
            if (!ValidCode(code))
            {
                SetFeedback("班级代码需为 2–64 位，以英文字母或数字开头，其余可用英文字母、数字和 . _ -。", error: true);
                _rejectedCode = code;
                CreationCode.Focus();
                return;
            }
            CreationCode.Text = code;
            submission = new(context, grade, code, name, CreationStudentSelectable.IsChecked == true, Guid.NewGuid());
        }

        using var request = new CancellationTokenSource();
        _request = request;
        _submitting = true;
        SetPending(submission);
        SetFeedback(readReceipt ? "正在核对原建班请求…" : "正在提交建班请求…");
        Render();
        try
        {
            string? id = readReceipt
                ? await client.GetScopedClassCreationResultAsync(submission.Context, submission.Grade.Id, submission.RequestId, request.Token)
                : await client.CreateClassAsync(submission.Context, submission.Grade.Id, submission.Code, submission.Name,
                    submission.IsStudentSelectable, submission.RequestId, request.Token);
            if (_request != request) return;
            if (id is null)
            {
                _canRetrySpecifiedClass = true;
                SetFeedback("暂未查到本次回执。可使用原编号重试，学校会再次核对学期和年级。");
                return;
            }
            SetPending(null);
            ConfirmedClass = new(id, submission.Context.TermId!, submission.Name);
            _allowClose = true;
            Close(ConfirmedClass);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ArgumentException error)
        {
            if (_request != request) return;
            SetPending(null);
            SetFeedback(error.Message, error: true);
        }
        catch (ConsoleApiException error)
        {
            if (_request == request) HandleCreateError(error);
        }
        finally { EndRequest(request); }
    }

    private void HandleCreateError(ConsoleApiException error)
    {
        if (error.StatusCode is 0 or 408 or >= 500)
        {
            _canRetrySpecifiedClass = false;
            ShowUnconfirmed();
            return;
        }

        SetPending(null);
        if (error.StatusCode is 401 or 403)
        {
            LoseAccess(error);
            return;
        }

        switch (error.Code)
        {
            case "WORKSPACE_CODE_EXISTS":
                SetFeedback("此班级代码已存在。请更换代码后再创建。", error: true);
                _rejectedCode = NormalizedCode();
                break;
            case "CLASS_CREATION_TERM_CHANGED":
            case "TERM_STRUCTURE_SCOPE_CHANGED":
            case "GRADE_INVALID":
                _contextInvalid = true;
                CreationGrade.SelectedItem = null;
                SetFeedback("学期或年级已变化。请刷新学期与年级，然后重新选择年级。", error: true);
                break;
            case "CLASS_CREATION_TARGET_CHANGED":
                _blocked = true;
                CloseNotice = "此前的行政班已创建，但班级状态已变化。请刷新班级列表核对。";
                SetFeedback(CloseNotice, error: true);
                break;
            case "CLASS_CREATION_REQUEST_CONFLICT":
                _blocked = true;
                CloseNotice = "无法用当前请求确认建班结果。请先刷新班级列表核对。";
                SetFeedback(CloseNotice, error: true);
                break;
            case "CLASS_CREATION_UNSUPPORTED":
                _blocked = true;
                SetFeedback(error.Message, error: true);
                break;
            default:
                SetFeedback(error.Message, error: true);
                break;
        }
    }

    private void LoseAccess(ConsoleApiException error)
    {
        _blocked = true;
        SetPending(null);
        SetPendingGrade(null);
        SetPendingBatch(null);
        ResetRenameState();
        ResetBatchState();
        ClearFields();
        CloseNotice = error.StatusCode != 403 ? null : _editingGrade
            ? "当前账号没有权限创建年级。若此前结果未确认，请在恢复权限后核对年级。"
            : "当前账号没有权限新建行政班。若此前结果未确认，请在恢复权限后核对班级列表。";
        SetFeedback(CloseNotice ?? "登录已失效，请重新登录。", error: true);
        if (error.StatusCode == 401) SessionExpired?.Invoke(error);
    }

    private void SetPending(ClassCreationSubmission? pending)
    {
        _pending = pending;
        _canRetrySpecifiedClass = false;
        PendingChanged?.Invoke(pending);
    }

    private void ShowUnconfirmed() => SetFeedback(_pending?.Context.IsSpecifiedTerm == true
        ? "建班结果未确认。请先只读核对原请求，关闭后仍可从本学期的建班窗口继续。"
        : "建班结果未确认。重试确认会沿用相同信息；关闭后可在当前登录中再次打开本校建班窗口继续。", error: true);

    private void ClearFields()
    {
        CreationGrade.ItemsSource = null;
        CreationName.Clear();
        CreationCode.Clear();
        NewGradeName.Clear();
        NewGradeCode.Clear();
        RenameGradeName.Clear();
        GradeRenameServerName.Text = null;
        GradeRenameServerName.IsVisible = false;
    }

    private void EndRequest(CancellationTokenSource request)
    {
        if (_request != request) return;
        _request = null;
        _submitting = false;
        Render();
    }

    private void SetFeedback(string? message, bool error = false)
    {
        _rejectedCode = _rejectedName = null;
        CreationFeedback.Text = message;
        CreationFeedback.IsVisible = message is not null;
        CreationFeedback.Classes.Set("creation-error", error);
        BatchFeedback.Text = message;
        BatchFeedback.Classes.Set("creation-error", error);
        BatchFeedbackPanel.Classes.Set("has-issue", error);
        BatchFeedbackPanel.IsVisible = message is not null;
    }

    private void Render()
    {
        if (_closed || SubmitClassCreationButton is null) return;
        bool busy = _request is not null;
        bool ready = _context is { TermId: not null, Grades.Count: > 0 } && !_contextInvalid && !_blocked;
        // 改名/批量开班期间收起建班表单：控件不销毁，已填的班级草稿保留；避免在 530×660 下把当前模式的按钮挤出视野。
        CreationFields.IsVisible = !_editingGrade && !_editingGradeRename && !_editingBatch;
        CreationFields.IsEnabled = ready && !busy && _pending is null && !_editingGradeRename && !_editingBatch;
        ReloadCreationContext.IsEnabled = !busy && _pending is null && _pendingGrade is null && !_blocked;
        SubmitClassCreationButton.Content = _submitting ? "正在处理…" : _pending?.Context.IsSpecifiedTerm == true
            ? _canRetrySpecifiedClass ? "按原编号重试" : "核对结果"
            : _pending is not null ? "重试确认" : "创建并录入名单";
        SubmitClassCreationButton.IsVisible = !_editingGrade && !_editingGradeRename && !_editingBatch;
        SubmitClassCreationButton.IsEnabled = !_editingGrade && !_editingGradeRename && !_editingBatch && !busy && !_blocked &&
            (_pending is not null || ready && CreationGrade.SelectedItem is ClassCreationGrade &&
                !string.IsNullOrWhiteSpace(CreationName.Text) && !string.IsNullOrWhiteSpace(CreationCode.Text));
        bool batchBusy = _batchSubmitting || _batchValidating || _batchChecking;
        CloseClassCreationButton.IsEnabled = !_submitting && !_renameSubmitting && !_renameChecking && !batchBusy;
        CloseClassCreationButton.Content = !_submitting && !_renameSubmitting && !_renameChecking && !batchBusy &&
            (_pending is not null || _pendingGrade is not null || _batchUncertain) ? "关闭，稍后确认" : "关闭";
        RenderGradeCreation(busy);
        RenderGradeRename(busy);
        OpenGradeRenameButton.IsVisible = _specifiedTerm is null;
        RenderBatch(busy);
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await LoadContextAsync();
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    private void FieldsChanged(object? sender, RoutedEventArgs args)
    {
        if (!_closed && (_rejectedCode is { } code && code != NormalizedCode() ||
                         _rejectedName is { } name && name != (CreationName.Text ?? "").Trim())) SetFeedback(null);
        Render();
    }
    private void NormalizeCode(object? sender, RoutedEventArgs args)
    {
        if (!_closed && _pending is null) CreationCode.Text = NormalizedCode();
    }

    private string NormalizedCode() => (CreationCode.Text ?? "").Trim().ToUpperInvariant();
    private static bool ValidCode(string code) => code.Length is >= 2 and <= 64 && char.IsAsciiLetterOrDigit(code[0]) &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
